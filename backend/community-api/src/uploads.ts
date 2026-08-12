import {
  BundleValidationError,
  hasOnlyKeys,
  inspectLevelBundle,
  isCanonicalUuid,
  isPlainObject,
  isSemanticVersion,
  isSha256,
  MAX_BUNDLE_BYTES,
  MAX_TITLE_CHARACTERS,
  sha256Hex,
  type AuthPrincipal,
  type InspectedLevelBundle,
  type UploadStatus,
} from "@ss2revive/community-contracts";
import { runtimeConfig, type RuntimeConfig } from "./config";
import { emptyResponse, HttpError, jsonResponse, readJsonObject, requireMethod } from "./http";

const RESERVATION_LIFETIME_MS = 30 * 60 * 1000;
const MAX_ACTIVE_UPLOADS = 3;
const MINIMUM_REVIVE_VERSION = "1.1.0";
/** Current revision plus the two before it stay downloadable; older approved objects are released. */
const RETAINED_REVISIONS = 3;
/** How long a claimed validation lease stays valid before maintenance may reclaim it. */
const VALIDATION_LEASE_TTL_MS = 10 * 60 * 1000;

export interface UploadQueueMessage {
  kind: "map-upload";
  uploadId: string;
}

interface UploadRow {
  id: string;
  steam_id64: string;
  status: UploadStatus;
  expected_size_bytes: number;
  expected_sha256: string;
  quarantine_key: string;
  map_id: string | null;
  revision: number | null;
  error_code: string | null;
  error_message: string | null;
  expires_at: number;
  validation_lease: string | null;
  validation_started_at: number | null;
  quota_state: "none" | "reserved" | "published";
  quota_bytes_charged: number;
  quota_day_utc: string | null;
}

interface ExistingMapRow {
  current_revision: number;
  creator_ids_json: string | null;
  sha256: string | null;
  bundle_key: string | null;
  owner_steam_id64: string | null;
  status: "published" | "unpublished" | "archived";
  moderation_reason: string | null;
}

class UploadRejected extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "UploadRejected";
    this.code = code;
  }
}

function gamePlayerId(steamId64: string): string {
  return `STEAM-${steamId64}`.padEnd(36, "-");
}

function requirePublisher(principal: AuthPrincipal): void {
  if (!principal.scopes.includes("maps:upload")) {
    throw new HttpError(403, "scope_denied", "The token lacks the required publishing scope.");
  }
}

async function reserveDailyQuota(
  env: Env,
  config: RuntimeConfig,
  steamId64: string,
  nowMs: number,
): Promise<void> {
  const dayUtc = new Date(nowMs).toISOString().slice(0, 10);
  const row = await env.DB.prepare(
    `INSERT INTO upload_usage_daily (steam_id64, day_utc, reservations, updated_at)
     VALUES (?, ?, 1, ?)
     ON CONFLICT(steam_id64, day_utc) DO UPDATE SET
       reservations = upload_usage_daily.reservations + 1,
       updated_at = excluded.updated_at
     WHERE upload_usage_daily.reservations < ?
     RETURNING reservations`,
  ).bind(steamId64, dayUtc, nowMs, config.uploadDailyLimit).first<{ reservations: number }>();
  if (row === null) {
    const nextDayMs = Date.parse(`${dayUtc}T00:00:00.000Z`) + 24 * 60 * 60 * 1000;
    throw new HttpError(429, "daily_upload_limit_reached", "The daily upload limit has been reached.", {
      "Retry-After": String(Math.max(60, Math.ceil((nextDayMs - nowMs) / 1000))),
    });
  }
}

async function uploadRow(env: Env, uploadId: string, steamId64?: string): Promise<UploadRow | null> {
  const ownerClause = steamId64 === undefined ? "" : " AND steam_id64 = ?";
  const statement = env.DB.prepare(
    `SELECT id, steam_id64, status, expected_size_bytes, expected_sha256, quarantine_key,
            map_id, revision, error_code, error_message, expires_at,
            validation_lease, validation_started_at,
            quota_state, quota_bytes_charged, quota_day_utc
       FROM map_uploads WHERE id = ?${ownerClause}`,
  );
  return steamId64 === undefined
    ? statement.bind(uploadId).first<UploadRow>()
    : statement.bind(uploadId, steamId64).first<UploadRow>();
}

function statusBody(row: UploadRow, requestId: string): Record<string, unknown> {
  const body: Record<string, unknown> = {
    requestId,
    uploadId: row.id,
    status: row.status,
  };
  if (row.map_id !== null) body.mapId = row.map_id;
  if (row.revision !== null) body.revision = row.revision;
  if (row.error_code !== null) body.errorCode = row.error_code;
  if (row.error_message !== null) body.errorMessage = row.error_message;
  return body;
}

export async function reserveUpload(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  principal: AuthPrincipal,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "POST");
  requirePublisher(principal);
  const body = await readJsonObject(request, 4096);
  if (
    !hasOnlyKeys(body, ["sizeBytes", "sha256"]) ||
    !Number.isSafeInteger(body.sizeBytes) || (body.sizeBytes as number) < 1 ||
    (body.sizeBytes as number) > MAX_BUNDLE_BYTES || !isSha256(body.sha256)
  ) {
    throw new HttpError(400, "invalid_request", "The upload size or SHA-256 is invalid.");
  }
  const active = await env.DB.prepare(
    `SELECT COUNT(*) AS count FROM map_uploads
      WHERE steam_id64 = ? AND status IN ('reserved', 'uploaded', 'validating') AND expires_at > ?`,
  ).bind(principal.steamId64, nowMs).first<{ count: number }>();
  if (active === null || !Number.isSafeInteger(active.count)) {
    throw new HttpError(503, "temporarily_unavailable", "The upload quota could not be checked.");
  }
  if (active.count >= MAX_ACTIVE_UPLOADS) {
    throw new HttpError(429, "upload_limit_reached", "Finish or cancel an existing upload first.", {
      "Retry-After": "60",
    });
  }
  await reserveDailyQuota(env, config, principal.steamId64, nowMs);

  const uploadId = crypto.randomUUID();
  const expiresAt = nowMs + RESERVATION_LIFETIME_MS;
  const quarantineKey = `quarantine/uploads/${principal.steamId64}/${uploadId}/${body.sha256}.ss2level`;
  await env.DB.prepare(
    `INSERT INTO map_uploads
       (id, steam_id64, status, expected_size_bytes, expected_sha256, quarantine_key, created_at, expires_at)
     VALUES (?, ?, 'reserved', ?, ?, ?, ?, ?)`,
  ).bind(
    uploadId,
    principal.steamId64,
    body.sizeBytes,
    body.sha256,
    quarantineKey,
    nowMs,
    expiresAt,
  ).run();
  return jsonResponse({
    requestId,
    uploadId,
    status: "reserved",
    bundleUploadPath: `/v1/uploads/${uploadId}/bundle`,
    statusPath: `/v1/uploads/${uploadId}`,
    expiresAt: new Date(expiresAt).toISOString(),
  }, 201, requestId, { "Cache-Control": "no-store" });
}

async function enqueueValidation(env: Env, uploadId: string): Promise<void> {
  await env.VALIDATION_QUEUE.send({ kind: "map-upload", uploadId } satisfies UploadQueueMessage, {
    contentType: "json",
  });
}

export async function putUploadBundle(
  request: Request,
  env: Env,
  _config: RuntimeConfig,
  principal: AuthPrincipal,
  uploadId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "PUT");
  requirePublisher(principal);
  if (!isCanonicalUuid(uploadId)) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  const row = await uploadRow(env, uploadId, principal.steamId64);
  if (row === null) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  if (row.expires_at <= nowMs && row.status !== "published") {
    throw new HttpError(410, "upload_expired", "The upload reservation expired.");
  }
  if (row.status === "uploaded" || row.status === "validating") {
    await enqueueValidation(env, row.id);
    return jsonResponse(statusBody(row, requestId), 202, requestId, { "Retry-After": "2" });
  }
  if (row.status !== "reserved") {
    throw new HttpError(409, "upload_not_writable", "The upload is no longer writable.");
  }
  const contentType = request.headers.get("Content-Type")?.split(";", 1)[0]?.trim().toLowerCase();
  const lengthText = request.headers.get("Content-Length") ?? "";
  const suppliedSha256 = request.headers.get("X-Content-SHA256") ?? "";
  if (
    contentType !== "application/vnd.ss2revive.level" ||
    !/^[1-9][0-9]*$/u.test(lengthText) || Number(lengthText) !== row.expected_size_bytes ||
    suppliedSha256 !== row.expected_sha256 || request.body === null
  ) {
    throw new HttpError(400, "upload_metadata_mismatch", "The upload headers do not match the reservation.");
  }

  let object: R2Object | null;
  try {
    object = await env.MAP_BUCKET.put(row.quarantine_key, request.body, {
      onlyIf: { etagDoesNotMatch: "*" },
      sha256: row.expected_sha256,
      httpMetadata: { contentType: "application/vnd.ss2revive.level" },
      customMetadata: {
        sha256: row.expected_sha256,
        uploadId: row.id,
        owner: row.steam_id64,
        state: "quarantine",
      },
    });
  } catch {
    throw new HttpError(400, "upload_checksum_mismatch", "The uploaded bytes failed their SHA-256 check.");
  }
  if (object === null) object = await env.MAP_BUCKET.head(row.quarantine_key);
  if (
    object === null || object.size !== row.expected_size_bytes ||
    object.customMetadata?.sha256 !== row.expected_sha256 ||
    object.customMetadata.uploadId !== row.id || object.customMetadata.owner !== row.steam_id64
  ) {
    throw new HttpError(409, "upload_conflict", "The quarantine object does not match this reservation.");
  }
  const updated = await env.DB.prepare(
    `UPDATE map_uploads SET status = 'uploaded', uploaded_at = ?
      WHERE id = ? AND steam_id64 = ? AND status = 'reserved' AND expires_at > ?`,
  ).bind(nowMs, row.id, row.steam_id64, nowMs).run();
  if ((updated.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "upload_conflict", "The upload state changed while bytes were being received.");
  }
  try {
    await enqueueValidation(env, row.id);
  } catch {
    await env.DB.prepare(
      `UPDATE map_uploads SET status = 'cancelled', completed_at = ?
        WHERE id = ? AND steam_id64 = ? AND status = 'uploaded'`,
    ).bind(nowMs, row.id, row.steam_id64).run();
    await env.MAP_BUCKET.delete(row.quarantine_key);
    throw new HttpError(503, "validation_unavailable", "Validation could not be scheduled. Retry the upload.");
  }
  row.status = "uploaded";
  return jsonResponse(statusBody(row, requestId), 202, requestId, { "Retry-After": "2" });
}

export async function getUploadStatus(
  request: Request,
  env: Env,
  _config: RuntimeConfig,
  principal: AuthPrincipal,
  uploadId: string,
  requestId: string,
): Promise<Response> {
  requireMethod(request, "GET");
  requirePublisher(principal);
  if (!isCanonicalUuid(uploadId)) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  const row = await uploadRow(env, uploadId, principal.steamId64);
  if (row === null) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  return jsonResponse(statusBody(row, requestId), 200, requestId, {
    "Cache-Control": "no-store",
    ...(row.status === "uploaded" || row.status === "validating" ? { "Retry-After": "2" } : {}),
  });
}

export async function cancelUpload(
  request: Request,
  env: Env,
  _config: RuntimeConfig,
  principal: AuthPrincipal,
  uploadId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "DELETE");
  requirePublisher(principal);
  if (!isCanonicalUuid(uploadId)) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  const row = await uploadRow(env, uploadId, principal.steamId64);
  if (row === null) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  if (row.status !== "reserved" && row.status !== "uploaded") {
    throw new HttpError(409, "upload_not_cancellable", "The upload can no longer be cancelled.");
  }
  const changed = await env.DB.prepare(
    `UPDATE map_uploads SET status = 'cancelled', completed_at = ?,
                            quota_state = CASE WHEN quota_state = 'reserved' THEN 'none' ELSE quota_state END
      WHERE id = ? AND steam_id64 = ? AND status IN ('reserved', 'uploaded')`,
  ).bind(nowMs, row.id, row.steam_id64).run();
  if ((changed.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "upload_not_cancellable", "The upload can no longer be cancelled.");
  }
  await env.MAP_BUCKET.delete(row.quarantine_key);
  return emptyResponse(204, requestId);
}

function checksumHex(value: ArrayBuffer | undefined): string | null {
  if (value === undefined) return null;
  return [...new Uint8Array(value)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function creatorArray(text: string): string[] {
  try {
    const value = JSON.parse(text) as unknown;
    return Array.isArray(value) && value.every((item) => typeof item === "string") ? value : [];
  } catch {
    return [];
  }
}

function playerCounts(configurations: unknown[]): string {
  const values = new Set<number>();
  for (const item of configurations) {
    if (item !== null && typeof item === "object" && !Array.isArray(item)) {
      const value = (item as Record<string, unknown>).numberPlayers;
      if (Number.isSafeInteger(value) && (value as number) >= 1 && (value as number) <= 4) values.add(value as number);
    }
  }
  if (values.size === 0) values.add(1);
  return `,${[...values].sort((left, right) => left - right).join(",")},`;
}

function equalBytes(left: Uint8Array | undefined, right: Uint8Array | undefined): boolean {
  if (left === undefined || right === undefined) return left === right;
  if (left.length !== right.length) return false;
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false;
  }
  return true;
}

function validationIdentity(validations: unknown[]): string {
  return JSON.stringify(validations.map((validation) => {
    if (!isPlainObject(validation)) return validation;
    return { id: validation.id, description: validation.description };
  }));
}

function sameLogicalRevision(left: InspectedLevelBundle, right: InspectedLevelBundle): boolean {
  const leftManifest = left.manifest;
  const rightManifest = right.manifest;
  return leftManifest.id === rightManifest.id && leftManifest.title === rightManifest.title &&
    leftManifest.description === rightManifest.description && leftManifest.createdAt === rightManifest.createdAt &&
    leftManifest.clientVersion === rightManifest.clientVersion &&
    leftManifest.contentVersion === rightManifest.contentVersion &&
    leftManifest.contentSha256 === rightManifest.contentSha256 &&
    JSON.stringify(leftManifest.creators) === JSON.stringify(rightManifest.creators) &&
    JSON.stringify(leftManifest.tags) === JSON.stringify(rightManifest.tags) &&
    JSON.stringify(leftManifest.configurations) === JSON.stringify(rightManifest.configurations) &&
    validationIdentity(leftManifest.validations) === validationIdentity(rightManifest.validations) &&
    equalBytes(left.contentImage, right.contentImage) && equalBytes(left.thumbnail, right.thumbnail);
}

async function isIdempotentRevision(
  env: Env,
  existing: ExistingMapRow,
  incoming: InspectedLevelBundle,
  nowMs: number,
): Promise<boolean> {
  if (incoming.bundleSha256 === existing.sha256) return true;
  if (existing.sha256 === null || existing.bundle_key === null) {
    throw new Error("the current approved bundle metadata is missing");
  }
  const expectedKey = `approved/maps/${incoming.manifest.id}/r${existing.current_revision}/${existing.sha256}.ss2level`;
  if (existing.bundle_key !== expectedKey) throw new Error("the current approved bundle key is inconsistent");
  const object = await env.MAP_BUCKET.get(existing.bundle_key);
  if (object === null || object.size < 1 || object.size > MAX_BUNDLE_BYTES) {
    throw new Error("the current approved bundle is missing or invalid");
  }
  const approved = await inspectLevelBundle(new Uint8Array(await object.arrayBuffer()), { nowMs });
  if (approved.bundleSha256 !== existing.sha256) throw new Error("the current approved bundle checksum is invalid");
  return sameLogicalRevision(approved, incoming);
}

/**
 * Charge an accepted revision against the account's daily byte allowance and its total retained
 * footprint. The lifetime map-count quota only gates new map identifiers, so without these an
 * account could republish revisions of one map and consume storage without bound.
 */
async function reserveStorageQuota(
  env: Env,
  config: RuntimeConfig,
  uploadId: string,
  steamId64: string,
  bytes: number,
  nowMs: number,
): Promise<void> {
  const dayUtc = new Date(nowMs).toISOString().slice(0, 10);
  try {
    const charged = await env.DB.prepare(
      `UPDATE map_uploads
          SET quota_state = 'reserved', quota_bytes_charged = ?, quota_day_utc = ?,
              quota_daily_limit = ?, quota_retained_limit = ?, quota_charged_at = ?
        WHERE id = ? AND steam_id64 = ? AND status = 'validating' AND quota_state = 'none'
        RETURNING id`,
    ).bind(
      bytes,
      dayUtc,
      config.uploadDailyBytes,
      config.retainedBytesPerAccount,
      nowMs,
      uploadId,
      steamId64,
    ).first<{ id: string }>();
    if (charged !== null) return;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (message.includes("daily_upload_bytes_reached")) {
      throw new UploadRejected(
        "daily_upload_bytes_reached",
        "This Steam account has published its daily storage allowance. Try again tomorrow.",
      );
    }
    if (message.includes("storage_quota_reached")) {
      throw new UploadRejected(
        "storage_quota_reached",
        "This Steam account has reached its retained storage limit. Archive an old map to publish more.",
      );
    }
    throw error;
  }

  const existing = await env.DB.prepare(
    `SELECT quota_state, quota_bytes_charged, quota_day_utc
       FROM map_uploads WHERE id = ? AND steam_id64 = ?`,
  ).bind(uploadId, steamId64).first<{
    quota_state: "none" | "reserved" | "published";
    quota_bytes_charged: number;
    quota_day_utc: string | null;
  }>();
  // The persisted reservation remains authoritative if a retry crosses UTC midnight. Requiring
  // today's date here would strand an otherwise valid upload while leaving its original charge
  // in place; the trigger already ensured that charge was admitted on its recorded day.
  if (existing?.quota_state === "reserved" && existing.quota_bytes_charged === bytes) return;
  throw new Error("the upload storage charge could not be reserved consistently");
}

/** Return released bytes to the account's retained footprint after revisions are collected. */
async function releaseStorageQuota(
  env: Env,
  steamId64: string | null,
  bytes: number,
  nowMs: number,
): Promise<void> {
  if (steamId64 === null || bytes <= 0) return;
  await env.DB.prepare(
    `UPDATE account_storage SET retained_bytes = MAX(0, retained_bytes - ?), updated_at = ?
      WHERE steam_id64 = ?`,
  ).bind(bytes, nowMs, steamId64).run();
}

/**
 * Approved bundles and thumbnails used to be retained for every revision ever published, so a
 * single account could grow R2 without bound by republishing the same map. Keep the current
 * revision plus a short rollback window and release the rest.
 *
 * D1 rows are removed before their objects: an unreferenced object is collected by the scheduled
 * reconciler, whereas a row pointing at a deleted object would break downloads.
 */
async function retireSupersededRevisions(
  env: Env,
  mapId: string,
  currentRevision: number,
  ownerSteamId64: string,
  nowMs: number,
): Promise<void> {
  const oldestRetained = currentRevision - (RETAINED_REVISIONS - 1);
  if (oldestRetained < 2) return;
  const stale = await env.DB.prepare(
    `SELECT revision, bundle_key, thumbnail_key, size_bytes, thumbnail_size_bytes
       FROM map_versions
      WHERE map_id = ? AND revision < ? ORDER BY revision ASC LIMIT 100`,
  ).bind(mapId, oldestRetained).all<{
    revision: number;
    bundle_key: string;
    thumbnail_key: string | null;
    size_bytes: number;
    thumbnail_size_bytes: number | null;
  }>();
  if (stale.results.length === 0) return;

  const statements: D1PreparedStatement[] = [];
  const keys: string[] = [];
  let released = 0;
  for (const version of stale.results) {
    statements.push(env.DB.prepare(
      `DELETE FROM map_tags WHERE map_id = ? AND revision = ?`,
    ).bind(mapId, version.revision));
    statements.push(env.DB.prepare(
      `DELETE FROM map_versions WHERE map_id = ? AND revision = ? AND revision < ?`,
    ).bind(mapId, version.revision, currentRevision));
    keys.push(version.bundle_key);
    released += version.size_bytes;
    if (version.thumbnail_key !== null) {
      keys.push(version.thumbnail_key);
      released += version.thumbnail_size_bytes ?? 0;
    }
  }
  await env.DB.batch(statements);
  await env.MAP_BUCKET.delete(keys);
  await releaseStorageQuota(env, ownerSteamId64, released, nowMs);
}

async function rejectUpload(
  env: Env,
  row: UploadRow,
  validationLease: string,
  code: string,
  message: string,
  nowMs: number,
): Promise<void> {
  const rejected = await env.DB.prepare(
    `UPDATE map_uploads
        SET status = 'rejected', error_code = ?, error_message = ?, completed_at = ?,
            quota_state = CASE WHEN quota_state = 'reserved' THEN 'none' ELSE quota_state END,
            validation_lease = NULL, validation_started_at = NULL, validation_lease_expires_at = NULL
      WHERE id = ? AND status = 'validating' AND validation_lease = ?`,
  ).bind(code.slice(0, 64), message.slice(0, 512), nowMs, row.id, validationLease).run();
  // Only the validator that still owns the lease may remove the shared quarantine object. A stale
  // validator can reach this function after a lease has been reclaimed and handed to a retry.
  if ((rejected.meta.changes ?? 0) === 1) {
    await env.MAP_BUCKET.delete(row.quarantine_key);
  }
}

async function publishValidatedUpload(
  env: Env,
  config: RuntimeConfig,
  row: UploadRow,
  validationLease: string,
  nowMs: number,
): Promise<void> {
  const object = await env.MAP_BUCKET.get(row.quarantine_key);
  if (
    object === null || object.size !== row.expected_size_bytes ||
    object.customMetadata?.sha256 !== row.expected_sha256 ||
    checksumHex(object.checksums.sha256) !== row.expected_sha256
  ) {
    throw new UploadRejected("quarantine_mismatch", "The quarantined object failed its metadata check.");
  }
  const bytes = new Uint8Array(await object.arrayBuffer());
  if (bytes.length !== row.expected_size_bytes) {
    throw new UploadRejected("quarantine_mismatch", "The quarantined object size changed during validation.");
  }
  const inspected = await inspectLevelBundle(bytes, { nowMs });
  if (inspected.bundleSha256 !== row.expected_sha256) {
    throw new UploadRejected("bundle_checksum_invalid", "The bundle SHA-256 does not match its reservation.");
  }
  const uploaderPlayerId = gamePlayerId(row.steam_id64);
  if (!inspected.manifest.creators.includes(uploaderPlayerId)) {
    throw new UploadRejected("creator_mismatch", "The authenticated Steam user is not a creator of this map.");
  }
  if (!isSemanticVersion(inspected.manifest.reviveVersion)) {
    throw new UploadRejected("revive_version_invalid", "The bundle does not declare a supported SS2Revive version.");
  }

  const revision = inspected.manifest.contentVersion;
  // updated_at_ms is the default catalogue sort key and comes from a publisher-declared timestamp.
  // Clamp it to server time so a manifest cannot rank itself above every legitimate map.
  const publishedAtMs = Math.min(inspected.manifest.exportedAt, nowMs);
  const existing = await env.DB.prepare(
    `SELECT m.current_revision, m.owner_steam_id64, m.status, m.moderation_reason,
            v.creator_ids_json, v.sha256, v.bundle_key
       FROM maps m LEFT JOIN map_versions v ON v.map_id = m.id AND v.revision = m.current_revision
      WHERE m.id = ?`,
  ).bind(inspected.manifest.id).first<ExistingMapRow>();
  if (existing !== null) {
    if ((existing.owner_steam_id64 !== null && existing.owner_steam_id64 !== row.steam_id64) ||
        (existing.owner_steam_id64 === null && !creatorArray(existing.creator_ids_json ?? "[]").includes(uploaderPlayerId))) {
      throw new UploadRejected("map_owner_mismatch", "This Steam user does not own the existing community map.");
    }
    if (existing.moderation_reason !== null) {
      throw new UploadRejected(
        "map_under_moderation",
        "This map was removed by a maintainer and cannot be republished until it is restored.",
      );
    }
    if (revision <= existing.current_revision) {
      if (existing.status !== "archived" && existing.sha256 !== null && existing.bundle_key !== null &&
          revision === existing.current_revision &&
          await isIdempotentRevision(env, existing, inspected, nowMs)) {
        // The moderation check above is a read; a maintainer can take the map down between that
        // read and this write. Re-assert it as a compare-and-swap so a takedown always wins, and
        // never clear moderation_reason as a side effect of an ordinary publication.
        const results = await env.DB.batch([
          env.DB.prepare(
            `UPDATE maps SET status = 'published', owner_steam_id64 = COALESCE(owner_steam_id64, ?),
                             unpublished_at_ms = NULL, archived_at_ms = NULL,
                             updated_at_ms = MAX(updated_at_ms, ?)
              WHERE id = ? AND (owner_steam_id64 = ? OR owner_steam_id64 IS NULL)
                AND moderation_reason IS NULL`,
          ).bind(row.steam_id64, publishedAtMs, inspected.manifest.id, row.steam_id64),
          env.DB.prepare(
            `UPDATE map_versions SET status = 'published'
              WHERE map_id = ? AND revision = ?
                AND EXISTS (SELECT 1 FROM maps m WHERE m.id = map_versions.map_id
                             AND m.moderation_reason IS NULL)`,
          ).bind(inspected.manifest.id, revision),
          env.DB.prepare(
            `UPDATE map_uploads SET status = 'published', map_id = ?, revision = ?, completed_at = ?
              WHERE id = ? AND status = 'validating' AND validation_lease = ?`,
          ).bind(inspected.manifest.id, revision, nowMs, row.id, validationLease),
        ]);
        if ((results[0]?.meta.changes ?? 0) !== 1) {
          throw new UploadRejected(
            "map_under_moderation",
            "This map was removed by a maintainer and cannot be republished until it is restored.",
          );
        }
        if ((results.at(-1)?.meta.changes ?? 0) !== 1) {
          throw new Error("idempotent publication state did not advance");
        }
        await env.MAP_BUCKET.delete(row.quarantine_key);
        return;
      }
      throw new UploadRejected("revision_not_newer", "Save the level again before uploading a new revision.");
    }
  }
  if (existing === null || existing.status === "archived") {
    const owned = await env.DB.prepare(
      `SELECT COUNT(*) AS count FROM maps WHERE owner_steam_id64 = ? AND status != 'archived'`,
    ).bind(row.steam_id64).first<{ count: number }>();
    if (owned === null || !Number.isSafeInteger(owned.count)) {
      throw new Error("the map ownership quota could not be checked");
    }
    if (owned.count >= config.mapsPerAccountLimit) {
      throw new UploadRejected("map_quota_reached", "This Steam account has reached its map limit.");
    }
  }

  const thumbnailBytes = inspected.thumbnail?.length ?? 0;
  await reserveStorageQuota(env, config, row.id, row.steam_id64, bytes.length + thumbnailBytes, nowMs);

  const bundleKey = `approved/maps/${inspected.manifest.id}/r${revision}/${inspected.bundleSha256}.ss2level`;
  const bundleObject = await env.MAP_BUCKET.put(bundleKey, bytes, {
    onlyIf: { etagDoesNotMatch: "*" },
    sha256: inspected.bundleSha256,
    httpMetadata: { contentType: "application/vnd.ss2revive.level" },
    customMetadata: { sha256: inspected.bundleSha256, state: "approved" },
  });
  if (bundleObject === null) {
    const existingBundle = await env.MAP_BUCKET.head(bundleKey);
    if (
      existingBundle === null || existingBundle.size !== bytes.length ||
      checksumHex(existingBundle.checksums.sha256) !== inspected.bundleSha256 ||
      existingBundle.customMetadata?.sha256 !== inspected.bundleSha256 ||
      existingBundle.customMetadata.state !== "approved"
    ) {
      throw new Error("an existing approved bundle failed its integrity check");
    }
  }
  let thumbnailKey: string | null = null;
  let thumbnailSha256: string | null = null;
  let thumbnailSize: number | null = null;
  let thumbnailObject: R2Object | null = null;
  if (inspected.thumbnail !== undefined) {
    thumbnailSha256 = await sha256Hex(inspected.thumbnail);
    thumbnailSize = inspected.thumbnail.length;
    thumbnailKey = `approved/thumbnails/${inspected.manifest.id}/r${revision}/${thumbnailSha256}.bin`;
    thumbnailObject = await env.MAP_BUCKET.put(thumbnailKey, inspected.thumbnail, {
      onlyIf: { etagDoesNotMatch: "*" },
      sha256: thumbnailSha256,
      httpMetadata: { contentType: "application/octet-stream" },
      customMetadata: { sha256: thumbnailSha256, state: "approved" },
    });
    if (thumbnailObject === null) {
      const existingThumbnail = await env.MAP_BUCKET.head(thumbnailKey);
      if (
        existingThumbnail === null || existingThumbnail.size !== thumbnailSize ||
        checksumHex(existingThumbnail.checksums.sha256) !== thumbnailSha256 ||
        existingThumbnail.customMetadata?.sha256 !== thumbnailSha256 ||
        existingThumbnail.customMetadata.state !== "approved"
      ) {
        throw new Error("an existing approved thumbnail failed its integrity check");
      }
    }
  }

  const manifest = inspected.manifest;
  // Lowercasing can lengthen a string (U+0130 becomes two code units under en-US), and the
  // title_asc cursor rejects any sort value longer than a title. Refuse the map rather than
  // issue a pagination cursor the listing will later reject.
  const titleSort = manifest.title.toLocaleLowerCase("en-US");
  if (titleSort.length > MAX_TITLE_CHARACTERS) {
    throw new UploadRejected("title_invalid", "The level title cannot be sorted within its length limit.");
  }
  const statements: D1PreparedStatement[] = [
    env.DB.prepare(
      `INSERT INTO maps
         (id, status, current_revision, title, title_sort, description, created_at_ms, updated_at_ms,
          owner_steam_id64, unpublished_at_ms, archived_at_ms, moderation_reason)
       SELECT ?, 'published', ?, ?, ?, ?, ?, ?, ?, NULL, NULL, NULL
        WHERE EXISTS (SELECT 1 FROM maps WHERE id = ? AND status != 'archived')
           OR (SELECT COUNT(*) FROM maps WHERE owner_steam_id64 = ? AND status != 'archived') < ?
       ON CONFLICT(id) DO UPDATE SET
         status = 'published', current_revision = excluded.current_revision,
         title = excluded.title, title_sort = excluded.title_sort,
         description = excluded.description, updated_at_ms = excluded.updated_at_ms
         , owner_steam_id64 = COALESCE(maps.owner_steam_id64, excluded.owner_steam_id64)
         , unpublished_at_ms = NULL, archived_at_ms = NULL, archive_operation_id = NULL
       WHERE maps.current_revision < excluded.current_revision
         AND (maps.owner_steam_id64 = excluded.owner_steam_id64 OR maps.owner_steam_id64 IS NULL)
         AND maps.moderation_reason IS NULL`,
    ).bind(
      manifest.id,
      revision,
      manifest.title,
      titleSort,
      manifest.description,
      manifest.createdAt,
      publishedAtMs,
      row.steam_id64,
      manifest.id,
      row.steam_id64,
      config.mapsPerAccountLimit,
    ),
    env.DB.prepare(
      `INSERT INTO map_versions
         (map_id, revision, status, code, creator_ids_json, tags_json,
          configurations_json, validations_json, player_counts_csv,
          client_version, map_format_version, minimum_revive_version, revive_version,
          size_bytes, sha256, bundle_key,
          thumbnail_size_bytes, thumbnail_sha256, thumbnail_key, created_at_ms)
       VALUES (?, ?, 'unpublished', ?, ?, ?, ?, ?, ?, 29, 29, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(
      manifest.id,
      revision,
      inspected.code,
      JSON.stringify(manifest.creators),
      JSON.stringify(manifest.tags),
      JSON.stringify(manifest.configurations),
      JSON.stringify(manifest.validations),
      playerCounts(manifest.configurations),
      MINIMUM_REVIVE_VERSION,
      manifest.reviveVersion,
      bytes.length,
      inspected.bundleSha256,
      bundleKey,
      thumbnailSize,
      thumbnailSha256,
      thumbnailKey,
      publishedAtMs,
    ),
  ];
  for (const tag of manifest.tags) {
    statements.push(env.DB.prepare(
      `INSERT INTO map_tags (map_id, revision, tag) VALUES (?, ?, ?)`,
    ).bind(manifest.id, revision, tag));
  }
  statements.push(env.DB.prepare(
    `UPDATE map_versions SET status = 'published'
      WHERE map_id = ? AND revision = ?
        AND EXISTS (SELECT 1 FROM maps m WHERE m.id = map_versions.map_id
                     AND m.current_revision = map_versions.revision)`,
  ).bind(manifest.id, revision));
  statements.push(env.DB.prepare(
    `UPDATE map_uploads SET status = 'published', map_id = ?, revision = ?, completed_at = ?,
                            quota_state = CASE WHEN quota_state = 'reserved' THEN 'published' ELSE quota_state END
      WHERE id = ? AND status = 'validating' AND validation_lease = ?
        AND EXISTS (SELECT 1 FROM map_versions v WHERE v.map_id = ? AND v.revision = ?
                     AND v.status = 'published')`,
  ).bind(manifest.id, revision, nowMs, row.id, validationLease, manifest.id, revision));

  try {
    const results = await env.DB.batch(statements);
    if ((results.at(-1)?.meta.changes ?? 0) !== 1) {
      // The maps upsert is now guarded on moderation_reason IS NULL, so a takedown that landed
      // during validation blocks promotion here. That is a terminal outcome for this upload, not a
      // transient fault: report it as a rejection so the queue stops retrying into the dead letter.
      const blocked = await env.DB.prepare(
        `SELECT moderation_reason FROM maps WHERE id = ? AND moderation_reason IS NOT NULL`,
      ).bind(manifest.id).first<{ moderation_reason: string }>();
      if (blocked !== null) {
        throw new UploadRejected(
          "map_under_moderation",
          "This map was removed by a maintainer and cannot be republished until it is restored.",
        );
      }
      throw new Error("publication state did not advance");
    }
  } catch (error) {
    // Approved keys are immutable and content-addressed. Leaving an unreferenced object for
    // later garbage collection is safer than deleting an object another idempotent attempt won;
    // the scheduled reconciler removes anything D1 does not reference.
    if (error instanceof UploadRejected) throw error;
    if (existing === null) {
      const [map, owned] = await Promise.all([
        env.DB.prepare(`SELECT id FROM maps WHERE id = ?`).bind(manifest.id).first<{ id: string }>(),
        env.DB.prepare(`SELECT COUNT(*) AS count FROM maps WHERE owner_steam_id64 = ? AND status != 'archived'`)
          .bind(row.steam_id64).first<{ count: number }>(),
      ]);
      if (map === null && owned !== null && owned.count >= config.mapsPerAccountLimit) {
        throw new UploadRejected("map_quota_reached", "This Steam account has reached its map limit.");
      }
    }
    throw error;
  }
  await env.MAP_BUCKET.delete(row.quarantine_key);
  await retireSupersededRevisions(env, manifest.id, revision, row.steam_id64, nowMs);
}

export async function validateUploadMessage(env: Env, value: unknown, nowMs = Date.now()): Promise<void> {
  if (!isPlainObject(value) || !hasOnlyKeys(value, ["kind", "uploadId"]) ||
      value.kind !== "map-upload" || !isCanonicalUuid(value.uploadId)) return;
  const message: UploadQueueMessage = { kind: "map-upload", uploadId: value.uploadId };
  const row = await uploadRow(env, message.uploadId);
  if (row === null || row.status === "published" || row.status === "rejected" ||
      row.status === "cancelled" || row.status === "expired" || row.status === "reserved") return;
  if (row.expires_at <= nowMs) {
    await env.MAP_BUCKET.delete(row.quarantine_key);
    await env.DB.prepare(
      `UPDATE map_uploads SET status = 'expired', completed_at = ?,
                              quota_state = CASE WHEN quota_state = 'reserved' THEN 'none' ELSE quota_state END,
                              validation_lease = NULL, validation_started_at = NULL,
                              validation_lease_expires_at = NULL
        WHERE id = ? AND status IN ('uploaded', 'validating')`,
    ).bind(nowMs, row.id).run();
    return;
  }
  const validationLease = crypto.randomUUID();
  const claimed = await env.DB.prepare(
    `UPDATE map_uploads
        SET status = 'validating', validation_lease = ?, validation_started_at = ?,
            validation_lease_expires_at = ?
      WHERE id = ? AND status = 'uploaded'`,
  ).bind(validationLease, nowMs, nowMs + VALIDATION_LEASE_TTL_MS, row.id).run();
  if ((claimed.meta.changes ?? 0) !== 1) return;
  row.status = "validating";
  try {
    await publishValidatedUpload(env, runtimeConfig(env), row, validationLease, nowMs);
  } catch (error) {
    if (error instanceof BundleValidationError) {
      await rejectUpload(env, row, validationLease, error.code, error.message, nowMs);
      return;
    }
    if (error instanceof UploadRejected) {
      await rejectUpload(env, row, validationLease, error.code, error.message, nowMs);
      return;
    }
    await env.DB.prepare(
      `UPDATE map_uploads
          SET status = 'uploaded', validation_lease = NULL, validation_started_at = NULL,
              validation_lease_expires_at = NULL
        WHERE id = ? AND status = 'validating' AND validation_lease = ?`,
    ).bind(row.id, validationLease).run();
    throw error;
  }
}
