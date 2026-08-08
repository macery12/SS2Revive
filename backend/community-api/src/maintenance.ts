export async function cleanupExpiredState(env: Env, nowMs: number): Promise<void> {
  const retentionCutoff = nowMs - 24 * 60 * 60 * 1000;
  await env.DB.batch([
    env.DB.prepare(`DELETE FROM download_leases WHERE expires_at <= ?`).bind(nowMs),
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
    env.DB.prepare(`DELETE FROM download_usage_daily WHERE day_utc < ?`)
      .bind(new Date(nowMs - 8 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)),
  ]);
}
