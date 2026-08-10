import {
  BundleValidationError,
  hasOnlyKeys,
  inspectLevelBundle,
  isCanonicalUuid,
  isPlainObject,
  isSemanticVersion,
  isSha256,
  MAX_BUNDLE_BYTES,
  sha256Hex,
  type AuthPrincipal,
  type InspectedLevelBundle,
  type UploadStatus,
} from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { emptyResponse, HttpError, jsonResponse, readJsonObject, requireMethod } from "./http";

const RESERVATION_LIFETIME_MS = 30 * 60 * 1000;
const MAX_ACTIVE_UPLOADS = 3;
const MINIMUM_REVIVE_VERSION = "1.1.0";

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
}

interface ExistingMapRow {
  current_revision: number;
  creator_ids_json: string;
  sha256: string;
  bundle_key: string;
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

function requirePublisher(config: RuntimeConfig, principal: AuthPrincipal): void {
  if (!config.publisherSteamIds.has(principal.steamId64) || !principal.scopes.includes("maps:upload")) {
    throw new HttpError(403, "publisher_required", "This Steam account is not allowed to publish maps.");
  }
}

async function uploadRow(env: Env, uploadId: string, steamId64?: string): Promise<UploadRow | null> {
  const ownerClause = steamId64 === undefined ? "" : " AND steam_id64 = ?";
  const statement = env.DB.prepare(
    `SELECT id, steam_id64, status, expected_size_bytes, expected_sha256, quarantine_key,
            map_id, revision, error_code, error_message, expires_at,
            validation_lease, validation_started_at
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
  requirePublisher(config, principal);
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
  config: RuntimeConfig,
  principal: AuthPrincipal,
  uploadId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "PUT");
  requirePublisher(config, principal);
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
  config: RuntimeConfig,
  principal: AuthPrincipal,
  uploadId: string,
  requestId: string,
): Promise<Response> {
  requireMethod(request, "GET");
  requirePublisher(config, principal);
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
  config: RuntimeConfig,
  principal: AuthPrincipal,
  uploadId: string,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  requireMethod(request, "DELETE");
  requirePublisher(config, principal);
  if (!isCanonicalUuid(uploadId)) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  const row = await uploadRow(env, uploadId, principal.steamId64);
  if (row === null) throw new HttpError(404, "upload_not_found", "The upload was not found.");
  if (row.status !== "reserved" && row.status !== "uploaded") {
    throw new HttpError(409, "upload_not_cancellable", "The upload can no longer be cancelled.");
  }
  const changed = await env.DB.prepare(
    `UPDATE map_uploads SET status = 'cancelled', completed_at = ?
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

async function rejectUpload(env: Env, row: UploadRow, code: string, message: string, nowMs: number): Promise<void> {
  await env.MAP_BUCKET.delete(row.quarantine_key);
  await env.DB.prepare(
    `UPDATE map_uploads
        SET status = 'rejected', error_code = ?, error_message = ?, completed_at = ?
      WHERE id = ? AND status IN ('uploaded', 'validating')`,
  ).bind(code.slice(0, 64), message.slice(0, 512), nowMs, row.id).run();
}

async function publishValidatedUpload(
  env: Env,
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
  const existing = await env.DB.prepare(
    `SELECT m.current_revision, v.creator_ids_json, v.sha256, v.bundle_key
       FROM maps m JOIN map_versions v ON v.map_id = m.id AND v.revision = m.current_revision
      WHERE m.id = ?`,
  ).bind(inspected.manifest.id).first<ExistingMapRow>();
  if (existing !== null) {
    if (!creatorArray(existing.creator_ids_json).includes(uploaderPlayerId)) {
      throw new UploadRejected("map_owner_mismatch", "This Steam user does not own the existing community map.");
    }
    if (revision <= existing.current_revision) {
      if (revision === existing.current_revision &&
          await isIdempotentRevision(env, existing, inspected, nowMs)) {
        await env.DB.prepare(
          `UPDATE map_uploads SET status = 'published', map_id = ?, revision = ?, completed_at = ?
            WHERE id = ? AND status = 'validating' AND validation_lease = ?`,
        ).bind(inspected.manifest.id, revision, nowMs, row.id, validationLease).run();
        await env.MAP_BUCKET.delete(row.quarantine_key);
        return;
      }
      throw new UploadRejected("revision_not_newer", "Save the level again before uploading a new revision.");
    }
  }

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
  const statements: D1PreparedStatement[] = [
    env.DB.prepare(
      `INSERT INTO maps
         (id, status, current_revision, title, title_sort, description, created_at_ms, updated_at_ms)
       VALUES (?, 'published', ?, ?, ?, ?, ?, ?)
       ON CONFLICT(id) DO UPDATE SET
         status = 'published', current_revision = excluded.current_revision,
         title = excluded.title, title_sort = excluded.title_sort,
         description = excluded.description, updated_at_ms = excluded.updated_at_ms
       WHERE maps.current_revision < excluded.current_revision`,
    ).bind(
      manifest.id,
      revision,
      manifest.title,
      manifest.title.toLocaleLowerCase("en-US"),
      manifest.description,
      manifest.createdAt,
      manifest.exportedAt,
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
      manifest.exportedAt,
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
    `UPDATE map_uploads SET status = 'published', map_id = ?, revision = ?, completed_at = ?
      WHERE id = ? AND status = 'validating' AND validation_lease = ?
        AND EXISTS (SELECT 1 FROM map_versions v WHERE v.map_id = ? AND v.revision = ?
                     AND v.status = 'published')`,
  ).bind(manifest.id, revision, nowMs, row.id, validationLease, manifest.id, revision));

  try {
    const results = await env.DB.batch(statements);
    if ((results.at(-1)?.meta.changes ?? 0) !== 1) {
      throw new Error("publication state did not advance");
    }
  } catch (error) {
    // Approved keys are immutable and content-addressed. Leaving an unreferenced object for
    // later garbage collection is safer than deleting an object another idempotent attempt won.
    throw error;
  }
  await env.MAP_BUCKET.delete(row.quarantine_key);
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
      `UPDATE map_uploads SET status = 'expired', completed_at = ?
        WHERE id = ? AND status IN ('uploaded', 'validating')`,
    ).bind(nowMs, row.id).run();
    return;
  }
  const validationLease = crypto.randomUUID();
  const claimed = await env.DB.prepare(
    `UPDATE map_uploads
        SET status = 'validating', validation_lease = ?, validation_started_at = ?
      WHERE id = ? AND status = 'uploaded'`,
  ).bind(validationLease, nowMs, row.id).run();
  if ((claimed.meta.changes ?? 0) !== 1) return;
  row.status = "validating";
  try {
    await publishValidatedUpload(env, row, validationLease, nowMs);
  } catch (error) {
    if (error instanceof BundleValidationError) {
      await rejectUpload(env, row, error.code, error.message, nowMs);
      return;
    }
    if (error instanceof UploadRejected) {
      await rejectUpload(env, row, error.code, error.message, nowMs);
      return;
    }
    await env.DB.prepare(
      `UPDATE map_uploads
          SET status = 'uploaded', validation_lease = NULL, validation_started_at = NULL
        WHERE id = ? AND status = 'validating' AND validation_lease = ?`,
    ).bind(row.id, validationLease).run();
    throw error;
  }
}
