import {
  hasOnlyKeys,
  isCanonicalUuid,
  type AuthPrincipal,
} from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { purgeMapCache } from "./edge-cache";
import { emptyResponse, HttpError, jsonResponse, readJsonObject, requireMethod } from "./http";

const REPORT_REASONS = new Set([
  "BROKEN",
  "NSFW_CONTENT",
  "COPY",
  "LEVEL_DESIGN_COPYRIGHT",
  "FARMING",
  "OTHER",
]);
const MAX_REPORTS_PER_DAY = 20;

interface OwnershipRow {
  owner_steam_id64: string | null;
  creator_ids_json: string | null;
  status: "published" | "unpublished" | "archived";
  current_revision: number;
  archive_operation_id: string | null;
}

interface ModerationReportRow {
  id: string;
  map_id: string;
  title: string;
  reporter_steam_id64: string;
  reason: string;
  note: string;
  created_at: number;
}

function gamePlayerId(steamId64: string): string {
  return `STEAM-${steamId64}`.padEnd(36, "-");
}

function creatorIds(text: string | null): string[] {
  if (text === null) return [];
  try {
    const value = JSON.parse(text) as unknown;
    return Array.isArray(value) && value.every((item) => typeof item === "string") ? value : [];
  } catch {
    return [];
  }
}

async function ownershipRow(env: Env, mapId: string): Promise<OwnershipRow | null> {
  return env.DB.prepare(
    `SELECT m.owner_steam_id64, m.status, m.current_revision, m.archive_operation_id,
            v.creator_ids_json
       FROM maps m
       LEFT JOIN map_versions v ON v.map_id = m.id AND v.revision = m.current_revision
      WHERE m.id = ?`,
  ).bind(mapId).first<OwnershipRow>();
}

async function requireOwner(env: Env, mapId: string, steamId64: string): Promise<OwnershipRow> {
  if (!isCanonicalUuid(mapId)) throw new HttpError(404, "map_not_found", "The map was not found.");
  const row = await ownershipRow(env, mapId);
  if (row === null) throw new HttpError(404, "map_not_found", "The map was not found.");
  if (row.owner_steam_id64 === steamId64) return row;
  if (row.owner_steam_id64 === null && creatorIds(row.creator_ids_json).includes(gamePlayerId(steamId64))) {
    const repaired = await env.DB.prepare(
      `UPDATE maps SET owner_steam_id64 = ? WHERE id = ? AND owner_steam_id64 IS NULL`,
    ).bind(steamId64, mapId).run();
    if ((repaired.meta.changes ?? 0) === 1) return { ...row, owner_steam_id64: steamId64 };
    const reread = await ownershipRow(env, mapId);
    if (reread?.owner_steam_id64 === steamId64) return reread;
  }
  throw new HttpError(403, "map_owner_required", "Only the map owner can change this publication.");
}

async function readEmptyObject(request: Request): Promise<void> {
  const body = await readJsonObject(request, 1024);
  if (!hasOnlyKeys(body, []) || Object.keys(body).length !== 0) {
    throw new HttpError(400, "invalid_request", "The request body must be an empty object.");
  }
}

export async function unpublishOwnMap(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  ctx: ExecutionContext,
  principal: AuthPrincipal,
  mapId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "POST");
  await readEmptyObject(request);
  const row = await requireOwner(env, mapId, principal.steamId64);
  if (row.status !== "published") {
    throw new HttpError(409, "map_not_published", "The map is not currently published.");
  }
  const results = await env.DB.batch([
    env.DB.prepare(
      `UPDATE maps SET status = 'unpublished', unpublished_at_ms = ?, archived_at_ms = NULL
        WHERE id = ? AND owner_steam_id64 = ? AND status = 'published'`,
    ).bind(nowMs, mapId, principal.steamId64),
    env.DB.prepare(
      `UPDATE map_versions SET status = 'unpublished' WHERE map_id = ? AND revision = ?`,
    ).bind(mapId, row.current_revision),
  ]);
  if ((results[0]?.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "map_state_changed", "The map state changed before it could be unpublished.");
  }
  purgeMapCache(ctx, config, mapId, [row.current_revision]);
  return jsonResponse({ requestId, mapId, status: "unpublished" }, 200, requestId, {
    "Cache-Control": "no-store",
  });
}

export async function archiveOwnMap(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  ctx: ExecutionContext,
  principal: AuthPrincipal,
  mapId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "DELETE");
  if (request.body !== null || Number(request.headers.get("Content-Length") ?? "0") !== 0) {
    throw new HttpError(400, "invalid_request", "The delete request must not have a body.");
  }
  const row = await requireOwner(env, mapId, principal.steamId64);
  if (row.status === "archived" && row.archive_operation_id !== null) {
    return emptyResponse(204, requestId);
  }
  const archiveOperationId = crypto.randomUUID();
  const versions = await env.DB.prepare(
    `SELECT revision, bundle_key, thumbnail_key FROM map_versions WHERE map_id = ?`,
  ).bind(mapId).all<{ revision: number; bundle_key: string; thumbnail_key: string | null }>();
  const results = await env.DB.batch([
    env.DB.prepare(
      `UPDATE maps SET status = 'archived', archived_at_ms = ?,
                       unpublished_at_ms = COALESCE(unpublished_at_ms, ?),
                       owner_steam_id64 = COALESCE(owner_steam_id64, ?),
                       moderation_reason = NULL, archive_operation_id = ?
        WHERE id = ? AND (owner_steam_id64 = ? OR owner_steam_id64 IS NULL)
          AND archive_operation_id IS NULL`,
    ).bind(nowMs, nowMs, principal.steamId64, archiveOperationId,
      mapId, principal.steamId64),
    env.DB.prepare(
      `UPDATE account_storage
          SET retained_bytes = MAX(0, retained_bytes - COALESCE((
                SELECT SUM(size_bytes + COALESCE(thumbnail_size_bytes, 0))
                  FROM map_versions WHERE map_id = ?
              ), 0)), updated_at = ?
        WHERE steam_id64 = ?
          AND EXISTS (SELECT 1 FROM maps WHERE id = ? AND owner_steam_id64 = ?
                       AND status = 'archived' AND archive_operation_id = ?)`,
    ).bind(mapId, nowMs, principal.steamId64, mapId, principal.steamId64, archiveOperationId),
    env.DB.prepare(
      `DELETE FROM map_tags WHERE map_id = ?
        AND EXISTS (SELECT 1 FROM maps WHERE id = ? AND owner_steam_id64 = ?
                    AND status = 'archived' AND archive_operation_id = ?)`,
    ).bind(mapId, mapId, principal.steamId64, archiveOperationId),
    env.DB.prepare(
      `DELETE FROM map_versions WHERE map_id = ?
        AND EXISTS (SELECT 1 FROM maps WHERE id = ? AND owner_steam_id64 = ?
                    AND status = 'archived' AND archive_operation_id = ?)`,
    ).bind(mapId, mapId, principal.steamId64, archiveOperationId),
  ]);
  if ((results[0]?.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "map_state_changed", "The map state changed before it could be archived.");
  }
  const keys = versions.results.flatMap((version) => version.thumbnail_key === null
    ? [version.bundle_key]
    : [version.bundle_key, version.thumbnail_key]);
  if (keys.length > 0) {
    try {
      await env.MAP_BUCKET.delete(keys);
    } catch (error) {
      console.error(JSON.stringify({
        event: "archived_object_cleanup_failed",
        mapId,
        name: error instanceof Error ? error.name : "unknown",
      }));
    }
  }
  purgeMapCache(ctx, config, mapId, versions.results.map((version) => version.revision));
  return emptyResponse(204, requestId);
}

export async function reportMap(
  request: Request,
  env: Env,
  principal: AuthPrincipal,
  mapId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "POST");
  if (!isCanonicalUuid(mapId)) throw new HttpError(404, "map_not_found", "The map was not found.");
  const body = await readJsonObject(request, 2048);
  if (!hasOnlyKeys(body, ["reason", "note"]) || typeof body.reason !== "string" ||
      !REPORT_REASONS.has(body.reason) ||
      (body.note !== undefined && (typeof body.note !== "string" || body.note.length > 512 ||
        /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(body.note)))) {
    throw new HttpError(400, "invalid_request", "The report reason or note is invalid.");
  }
  const map = await ownershipRow(env, mapId);
  if (map === null || map.status !== "published") {
    throw new HttpError(404, "map_not_found", "The map was not found.");
  }
  if (map.owner_steam_id64 === principal.steamId64 ||
      (map.owner_steam_id64 === null && creatorIds(map.creator_ids_json).includes(gamePlayerId(principal.steamId64)))) {
    throw new HttpError(409, "cannot_report_own_map", "A creator cannot report their own map.");
  }
  const dayStart = Date.parse(`${new Date(nowMs).toISOString().slice(0, 10)}T00:00:00.000Z`);
  const daily = await env.DB.prepare(
    `SELECT COUNT(*) AS count FROM map_reports WHERE reporter_steam_id64 = ? AND created_at >= ?`,
  ).bind(principal.steamId64, dayStart).first<{ count: number }>();
  if (daily === null || !Number.isSafeInteger(daily.count)) {
    throw new HttpError(503, "temporarily_unavailable", "The report quota could not be checked.");
  }
  if (daily.count >= MAX_REPORTS_PER_DAY) {
    throw new HttpError(429, "daily_report_limit_reached", "The daily report limit has been reached.", {
      "Retry-After": String(Math.max(60, Math.ceil((dayStart + 86_400_000 - nowMs) / 1000))),
    });
  }
  const reportId = crypto.randomUUID();
  await env.DB.prepare(
    `INSERT INTO map_reports
       (id, map_id, reporter_steam_id64, reason, note, status, created_at)
     VALUES (?, ?, ?, ?, ?, 'open', ?)
     ON CONFLICT(map_id, reporter_steam_id64) DO UPDATE SET
       reason = excluded.reason, note = excluded.note, status = 'open', created_at = excluded.created_at,
       resolved_at = NULL, resolved_by_steam_id64 = NULL, resolution = NULL`,
  ).bind(reportId, mapId, principal.steamId64, body.reason, body.note ?? "", nowMs).run();
  return jsonResponse({ requestId, mapId, status: "reported" }, 201, requestId, {
    "Cache-Control": "no-store",
  });
}

function requireMaintainer(config: RuntimeConfig, principal: AuthPrincipal): void {
  if (!principal.scopes.includes("maps:moderate") || !config.maintainerSteamIds.has(principal.steamId64)) {
    throw new HttpError(403, "maintainer_required", "Maintainer authorization is required.");
  }
}

export async function listOpenReports(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  principal: AuthPrincipal,
  requestId: string,
): Promise<Response> {
  requireMethod(request, "GET");
  requireMaintainer(config, principal);
  const url = new URL(request.url);
  if ([...url.searchParams.keys()].some((key) => key !== "limit")) {
    throw new HttpError(400, "invalid_request", "The report query contains an unsupported field.");
  }
  const limitText = url.searchParams.get("limit") ?? "50";
  if (!/^[1-9][0-9]*$/u.test(limitText) || Number(limitText) > 100) {
    throw new HttpError(400, "invalid_request", "limit must be between 1 and 100.");
  }
  const result = await env.DB.prepare(
    `SELECT r.id, r.map_id, m.title, r.reporter_steam_id64, r.reason, r.note, r.created_at
       FROM map_reports r JOIN maps m ON m.id = r.map_id
      WHERE r.status = 'open' ORDER BY r.created_at ASC LIMIT ?`,
  ).bind(Number(limitText)).all<ModerationReportRow>();
  return jsonResponse({ requestId, items: result.results.map((row) => ({
    id: row.id,
    mapId: row.map_id,
    title: row.title,
    reporterSteamId64: row.reporter_steam_id64,
    reason: row.reason,
    note: row.note,
    createdAt: new Date(row.created_at).toISOString(),
  })) }, 200, requestId, { "Cache-Control": "no-store" });
}

async function moderationAction(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  ctx: ExecutionContext,
  principal: AuthPrincipal,
  mapId: string,
  action: "takedown" | "restore",
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "POST");
  requireMaintainer(config, principal);
  if (!isCanonicalUuid(mapId)) throw new HttpError(404, "map_not_found", "The map was not found.");
  const body = await readJsonObject(request, 2048);
  const allowedKeys = action === "takedown" ? ["reason"] : ["reason"];
  if (!hasOnlyKeys(body, allowedKeys) || typeof body.reason !== "string" ||
      body.reason.trim().length < 1 || body.reason.length > 256 ||
      /[\u0000-\u001f\u007f-\u009f]/u.test(body.reason)) {
    throw new HttpError(400, "invalid_request", "A bounded moderation reason is required.");
  }
  const map = await ownershipRow(env, mapId);
  if (map === null) throw new HttpError(404, "map_not_found", "The map was not found.");
  if (action === "takedown" && map.status !== "published") {
    throw new HttpError(409, "map_not_published", "Only a currently published map can be taken down.");
  }
  // Restore undoes a maintainer takedown; it must not resurrect a map the owner themselves
  // unpublished or deleted, and it must not republish something no takedown ever removed.
  if (action === "restore") {
    if (map.archive_operation_id !== null) {
      throw new HttpError(409, "map_not_moderated", "This map is an owner archive, not a maintainer takedown.");
    }
    const moderated = await env.DB.prepare(
      `SELECT moderation_reason FROM maps WHERE id = ? AND moderation_reason IS NOT NULL`,
    ).bind(mapId).first<{ moderation_reason: string }>();
    if (moderated === null) {
      throw new HttpError(409, "map_not_moderated", "This map is not under a maintainer takedown.");
    }
  }
  const nextStatus = action === "takedown" ? "archived" : "published";
  const versionStatus = action === "takedown" ? "unpublished" : "published";
  const eventId = crypto.randomUUID();
  const results = await env.DB.batch([
    env.DB.prepare(
      `UPDATE maps SET status = ?, moderation_reason = ?,
                       archived_at_ms = CASE WHEN ? = 'archived' THEN ? ELSE NULL END,
                       unpublished_at_ms = CASE WHEN ? = 'archived' THEN COALESCE(unpublished_at_ms, ?) ELSE NULL END,
                       moderation_operation_id = ?
        WHERE id = ? AND ((? = 'archived' AND status = 'published')
                       OR (? = 'published' AND moderation_reason IS NOT NULL))`,
    ).bind(nextStatus, action === "takedown" ? body.reason.trim() : null,
      nextStatus, nowMs, nextStatus, nowMs, eventId, mapId, nextStatus, nextStatus),
    env.DB.prepare(
      `UPDATE map_versions SET status = ? WHERE map_id = ? AND revision = ?
        AND EXISTS (SELECT 1 FROM maps WHERE id = ? AND moderation_operation_id = ?)`,
    ).bind(versionStatus, mapId, map.current_revision, mapId, eventId),
    env.DB.prepare(
      `INSERT INTO moderation_events
         (id, map_id, maintainer_steam_id64, action, reason, created_at)
       SELECT ?, ?, ?, ?, ?, ?
        WHERE EXISTS (SELECT 1 FROM maps WHERE id = ? AND moderation_operation_id = ?)`,
    ).bind(eventId, mapId, principal.steamId64, action, body.reason.trim(), nowMs,
      mapId, eventId),
    env.DB.prepare(
      `UPDATE map_reports SET status = 'resolved', resolved_at = ?, resolved_by_steam_id64 = ?, resolution = ?
        WHERE map_id = ? AND status = 'open'
          AND EXISTS (SELECT 1 FROM maps WHERE id = ? AND moderation_operation_id = ?)`,
    ).bind(nowMs, principal.steamId64, `${action}: ${body.reason.trim()}`.slice(0, 512),
      mapId, mapId, eventId),
  ]);
  if ((results[0]?.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "map_state_changed", "The map state changed before moderation completed.");
  }
  purgeMapCache(ctx, config, mapId, [map.current_revision]);
  return jsonResponse({ requestId, mapId, status: nextStatus }, 200, requestId, {
    "Cache-Control": "no-store",
  });
}

export function takedownMap(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  ctx: ExecutionContext,
  principal: AuthPrincipal,
  mapId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  return moderationAction(request, env, config, ctx, principal, mapId, "takedown", requestId, nowMs);
}

export function restoreMap(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  ctx: ExecutionContext,
  principal: AuthPrincipal,
  mapId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  return moderationAction(request, env, config, ctx, principal, mapId, "restore", requestId, nowMs);
}
