/**
 * A validator that is evicted between claiming its lease and completing leaves its row in
 * 'validating' forever: the claim requires status 'uploaded', and the queue message is acked once
 * the claim fails. Reclaim expired leases so the reservation can finish or expire normally, and
 * release the quarantine object it was holding.
 */
const VALIDATION_LEASE_TTL_MS = 10 * 60 * 1000;

async function reclaimStaleValidations(env: Env, nowMs: number): Promise<void> {
  const stale = await env.DB.prepare(
    `SELECT id, quarantine_key, expires_at FROM map_uploads
      WHERE status = 'validating' AND validation_started_at IS NOT NULL
        AND validation_started_at <= ? LIMIT 100`,
  ).bind(nowMs - VALIDATION_LEASE_TTL_MS).all<{ id: string; quarantine_key: string; expires_at: number }>();
  if (stale.results.length === 0) return;

  const expired = stale.results.filter((row) => row.expires_at <= nowMs);
  const recoverable = stale.results.filter((row) => row.expires_at > nowMs);
  if (expired.length > 0) {
    await env.MAP_BUCKET.delete(expired.map((row) => row.quarantine_key));
  }
  await env.DB.batch([
    // Past its reservation window: terminate and stop holding the quarantine object.
    ...expired.map((row) => env.DB.prepare(
      `UPDATE map_uploads
          SET status = 'expired', completed_at = ?, validation_lease = NULL,
              validation_started_at = NULL, validation_lease_expires_at = NULL,
              quota_state = CASE WHEN quota_state = 'reserved' THEN 'none' ELSE quota_state END
        WHERE id = ? AND status = 'validating'`,
    ).bind(nowMs, row.id)),
    // Still inside its window: drop the dead lease so a redelivery can claim it again. Any
    // in-flight validator that survives cannot complete, because completion compares the lease.
    ...recoverable.map((row) => env.DB.prepare(
      `UPDATE map_uploads
          SET status = 'uploaded', validation_lease = NULL,
              validation_started_at = NULL, validation_lease_expires_at = NULL
        WHERE id = ? AND status = 'validating'`,
    ).bind(row.id)),
  ]);
}

/**
 * Approved objects are written before the D1 publication batch commits, so a failure in between
 * can leave an object no row references. Sweep a bounded slice of the approved prefix and delete
 * anything D1 does not know about. Objects are only considered once they are old enough that no
 * publication could still be mid-flight.
 */
const ORPHAN_GRACE_MS = 60 * 60 * 1000;

async function reconcileOrphanedObjects(env: Env, nowMs: number): Promise<void> {
  const state = await env.DB.prepare(
    `SELECT value FROM maintenance_state WHERE key = 'approved_object_cursor'`,
  ).first<{ value: string }>();
  const listed = await env.MAP_BUCKET.list({
    prefix: "approved/",
    limit: 200,
    ...(state === null ? {} : { cursor: state.value }),
  });
  const candidates = listed.objects.filter((object) => object.uploaded.getTime() <= nowMs - ORPHAN_GRACE_MS);
  const referenced = new Set<string>();
  for (let index = 0; index < candidates.length; index += 50) {
    const slice = candidates.slice(index, index + 50);
    const placeholders = slice.map(() => "?").join(", ");
    const rows = await env.DB.prepare(
      `SELECT bundle_key AS key FROM map_versions WHERE bundle_key IN (${placeholders})
       UNION ALL
       SELECT thumbnail_key AS key FROM map_versions WHERE thumbnail_key IN (${placeholders})`,
    ).bind(...slice.map((object) => object.key), ...slice.map((object) => object.key)).all<{ key: string }>();
    for (const row of rows.results) referenced.add(row.key);
  }
  const orphans = candidates.filter((object) => !referenced.has(object.key)).map((object) => object.key);
  if (orphans.length > 0) {
    await env.MAP_BUCKET.delete(orphans);
    console.log(JSON.stringify({ event: "orphan_objects_collected", count: orphans.length }));
  }

  if (listed.truncated && listed.cursor !== undefined) {
    await env.DB.prepare(
      `INSERT INTO maintenance_state (key, value, updated_at)
       VALUES ('approved_object_cursor', ?, ?)
       ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at`,
    ).bind(listed.cursor, nowMs).run();
  } else {
    await env.DB.prepare(`DELETE FROM maintenance_state WHERE key = 'approved_object_cursor'`).run();
  }
}

export async function cleanupExpiredState(env: Env, nowMs: number): Promise<void> {
  const retentionCutoff = nowMs - 24 * 60 * 60 * 1000;
  await reclaimStaleValidations(env, nowMs);
  const expiredUploads = await env.DB.prepare(
    `SELECT id, quarantine_key FROM map_uploads
      WHERE status IN ('reserved', 'uploaded') AND expires_at <= ? LIMIT 100`,
  ).bind(nowMs).all<{ id: string; quarantine_key: string }>();
  if (expiredUploads.results.length > 0) {
    await env.MAP_BUCKET.delete(expiredUploads.results.map((row) => row.quarantine_key));
    await env.DB.batch(expiredUploads.results.map((row) => env.DB.prepare(
      `UPDATE map_uploads SET status = 'expired', completed_at = ?,
                              quota_state = CASE WHEN quota_state = 'reserved' THEN 'none' ELSE quota_state END
        WHERE id = ? AND status IN ('reserved', 'uploaded')`,
    ).bind(nowMs, row.id)));
  }
  await env.DB.batch([
    env.DB.prepare(`DELETE FROM steam_openid_sessions WHERE expires_at <= ?`).bind(retentionCutoff),
    env.DB.prepare(`DELETE FROM refresh_tokens WHERE expires_at <= ?`).bind(retentionCutoff),
    env.DB.prepare(
      `DELETE FROM refresh_token_families
        WHERE expires_at <= ? AND NOT EXISTS
          (SELECT 1 FROM refresh_tokens t WHERE t.family_id = refresh_token_families.id)`,
    ).bind(retentionCutoff),
    env.DB.prepare(
      `DELETE FROM auth_sessions
        WHERE expires_at <= ? AND NOT EXISTS
          (SELECT 1 FROM refresh_token_families f WHERE f.session_id = auth_sessions.id)`,
    ).bind(retentionCutoff),
    env.DB.prepare(
      `DELETE FROM device_auth_sessions
        WHERE expires_at <= ? AND NOT EXISTS
          (SELECT 1 FROM steam_openid_sessions o WHERE o.device_session_id = device_auth_sessions.id)`,
    ).bind(retentionCutoff),
    env.DB.prepare(`DELETE FROM upload_usage_daily WHERE day_utc < ?`)
      .bind(new Date(nowMs - 8 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)),
    env.DB.prepare(
      `DELETE FROM map_uploads
        WHERE completed_at IS NOT NULL AND completed_at <= ?
          AND status IN ('published', 'rejected', 'cancelled', 'expired')`,
    ).bind(nowMs - 30 * 24 * 60 * 60 * 1000),
    // Resolved moderation reports and their events are kept for a quarter, then released.
    env.DB.prepare(
      `DELETE FROM map_reports WHERE status != 'open' AND resolved_at IS NOT NULL AND resolved_at <= ?`,
    ).bind(nowMs - 90 * 24 * 60 * 60 * 1000),
    env.DB.prepare(`DELETE FROM moderation_events WHERE created_at <= ?`)
      .bind(nowMs - 365 * 24 * 60 * 60 * 1000),
  ]);
  await reconcileOrphanedObjects(env, nowMs);
}
