import {
  isCanonicalUuid,
  levelCodeFromId,
  isSemanticVersion,
  isSha256,
  MAX_BUNDLE_BYTES,
  MAX_DESCRIPTION_CHARACTERS,
  MAX_IMAGE_BYTES,
  MAX_PAGE_SIZE,
  MAX_TITLE_CHARACTERS,
  parseBoundedPositiveInteger,
  sha256Hex,
  type CommunityMap,
  type MapPage,
} from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { signedValue, verifySignedValue } from "./crypto";
import { HttpError, jsonResponse } from "./http";

type MapSort = "updated_desc" | "created_desc" | "title_asc";

interface MapRow {
  id: string;
  revision: number;
  title: string;
  title_sort: string;
  description: string;
  created_at_ms: number;
  updated_at_ms: number;
  code: string;
  creator_ids_json: string;
  tags_json: string;
  configurations_json: string;
  validations_json: string;
  client_version: number;
  map_format_version: number;
  minimum_revive_version: string;
  revive_version: string | null;
  size_bytes: number;
  sha256: string;
  bundle_key: string;
  thumbnail_size_bytes: number | null;
  thumbnail_sha256: string | null;
  thumbnail_key: string | null;
  version_created_at_ms: number;
}

const MAP_SELECT = `
  SELECT m.id, v.revision, m.title, m.title_sort, m.description,
         m.created_at_ms, m.updated_at_ms, v.code,
         v.creator_ids_json, v.tags_json, v.configurations_json, v.validations_json,
         v.client_version, v.map_format_version, v.minimum_revive_version, v.revive_version,
         v.size_bytes, v.sha256, v.bundle_key,
         v.thumbnail_size_bytes, v.thumbnail_sha256, v.thumbnail_key,
         v.created_at_ms AS version_created_at_ms
    FROM maps m
    JOIN map_versions v ON v.map_id = m.id`;

function parseArray(text: string, maximum: number, field: string): unknown[] {
  let value: unknown;
  try {
    value = JSON.parse(text) as unknown;
  } catch {
    throw new HttpError(503, "object_metadata_mismatch", `Stored ${field} metadata is invalid.`);
  }
  if (!Array.isArray(value) || value.length > maximum) {
    throw new HttpError(503, "object_metadata_mismatch", `Stored ${field} metadata is invalid.`);
  }
  return value;
}

function stringArray(text: string, maximum: number, field: string): string[] {
  const value = parseArray(text, maximum, field);
  if (!value.every((item) => typeof item === "string" && item.length >= 1 && item.length <= 128)) {
    throw new HttpError(503, "object_metadata_mismatch", `Stored ${field} metadata is invalid.`);
  }
  return value as string[];
}

function mapDto(row: MapRow): CommunityMap {
  if (
    !isCanonicalUuid(row.id) || row.revision < 1 || !Number.isSafeInteger(row.revision) ||
    row.title.length < 1 || row.title.length > MAX_TITLE_CHARACTERS ||
    row.description.length > MAX_DESCRIPTION_CHARACTERS ||
    row.code !== levelCodeFromId(row.id) ||
    !Number.isSafeInteger(row.created_at_ms) || !Number.isSafeInteger(row.updated_at_ms) ||
    row.created_at_ms < 0 || row.updated_at_ms < row.created_at_ms ||
    row.client_version !== 29 || row.map_format_version !== 29 ||
    !isSemanticVersion(row.minimum_revive_version) ||
    (row.revive_version !== null && !isSemanticVersion(row.revive_version)) ||
    row.size_bytes < 1 || row.size_bytes > MAX_BUNDLE_BYTES || !isSha256(row.sha256)
  ) {
    throw new HttpError(503, "object_metadata_mismatch", "Stored map metadata is invalid.");
  }
  const expectedBundleKey = `approved/maps/${row.id}/r${row.revision}/${row.sha256}.ss2level`;
  if (row.bundle_key !== expectedBundleKey) {
    throw new HttpError(503, "object_metadata_mismatch", "Stored bundle metadata is invalid.");
  }
  const map: CommunityMap = {
    id: row.id,
    code: row.code,
    revision: row.revision,
    title: row.title,
    description: row.description,
    creatorIds: stringArray(row.creator_ids_json, 4, "creator"),
    tags: stringArray(row.tags_json, 32, "tag"),
    createdAtMs: row.created_at_ms,
    updatedAtMs: row.updated_at_ms,
    clientVersion: 29,
    mapFormatVersion: 29,
    minimumReviveVersion: row.minimum_revive_version,
    sizeBytes: row.size_bytes,
    sha256: row.sha256,
    configurations: parseArray(row.configurations_json, 8, "configuration"),
    validations: parseArray(row.validations_json, 32, "validation"),
    downloadUrl: `/v1/maps/${row.id}/versions/${row.revision}/download`,
  };
  if (row.revive_version !== null) map.reviveVersion = row.revive_version;
  const thumbnailCount = Number(row.thumbnail_key !== null) + Number(row.thumbnail_sha256 !== null) +
    Number(row.thumbnail_size_bytes !== null);
  if (thumbnailCount !== 0 && thumbnailCount !== 3) {
    throw new HttpError(503, "object_metadata_mismatch", "Stored thumbnail metadata is incomplete.");
  }
  if (row.thumbnail_key !== null && row.thumbnail_sha256 !== null && row.thumbnail_size_bytes !== null) {
    const expectedThumbnailKey = `approved/thumbnails/${row.id}/r${row.revision}/${row.thumbnail_sha256}.bin`;
    if (
      !isSha256(row.thumbnail_sha256) || row.thumbnail_size_bytes < 1 ||
      row.thumbnail_size_bytes > MAX_IMAGE_BYTES || row.thumbnail_key !== expectedThumbnailKey
    ) {
      throw new HttpError(503, "object_metadata_mismatch", "Stored thumbnail metadata is invalid.");
    }
    map.thumbnail = {
      url: `/v1/maps/${row.id}/versions/${row.revision}/thumbnail`,
      sizeBytes: row.thumbnail_size_bytes,
      sha256: row.thumbnail_sha256,
    };
  }
  return map;
}

function boundedSearch(value: string | null, name: string, maximum: number): string {
  if (value === null) return "";
  if (value.length > maximum || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) {
    throw new HttpError(400, "invalid_request", `${name} is invalid.`);
  }
  return value.trim();
}

function escapeLike(value: string): string {
  return value.replaceAll("\\", "\\\\").replaceAll("%", "\\%").replaceAll("_", "\\_");
}

async function filterHash(query: string, tag: string, players: number | null, sort: MapSort): Promise<string> {
  return sha256Hex(new TextEncoder().encode(JSON.stringify({ query, tag, players, sort })));
}

export async function listMaps(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const url = new URL(request.url);
  const limit = parseBoundedPositiveInteger(url.searchParams.get("limit"), 20, MAX_PAGE_SIZE);
  if (limit === null) throw new HttpError(400, "invalid_request", "limit must be between 1 and 50.");
  const query = boundedSearch(url.searchParams.get("query"), "query", 128);
  const tag = boundedSearch(url.searchParams.get("tag"), "tag", 128);
  if (tag !== "" && !/^[A-Za-z0-9_.-]+$/.test(tag)) throw new HttpError(400, "invalid_request", "tag is invalid.");
  const playersText = url.searchParams.get("players");
  const players = playersText === null ? null : parseBoundedPositiveInteger(playersText, 1, 4);
  if (playersText !== null && players === null) throw new HttpError(400, "invalid_request", "players must be between 1 and 4.");
  const sortText = url.searchParams.get("sort") ?? "updated_desc";
  if (sortText !== "updated_desc" && sortText !== "created_desc" && sortText !== "title_asc") {
    throw new HttpError(400, "unsupported_sort", "sort must be updated_desc, created_desc, or title_asc.");
  }
  const sort: MapSort = sortText;
  const expectedFilterHash = await filterHash(query, tag, players, sort);

  const clauses = [
    "m.status = 'published'",
    "v.status = 'published'",
    "v.revision = m.current_revision",
    "v.client_version = 29",
    "v.map_format_version = 29",
  ];
  const values: unknown[] = [];
  if (query !== "") {
    clauses.push("(v.code = ? OR m.title_sort LIKE ? ESCAPE '\\')");
    values.push(query, `${escapeLike(query.toLocaleLowerCase("en-US"))}%`);
  }
  if (tag !== "") {
    clauses.push("EXISTS (SELECT 1 FROM map_tags mt WHERE mt.map_id = m.id AND mt.revision = v.revision AND mt.tag = ?)");
    values.push(tag);
  }
  if (players !== null) {
    clauses.push("instr(v.player_counts_csv, ?) > 0");
    values.push(`,${players},`);
  }

  const cursorText = url.searchParams.get("cursor");
  if (cursorText !== null) {
    if (cursorText.length > 2048) throw new HttpError(400, "invalid_cursor", "The cursor is invalid.");
    const cursor = await verifySignedValue(config, "map-cursor", cursorText);
    if (
      cursor === null || cursor.v !== 1 || cursor.sort !== sort || cursor.filterHash !== expectedFilterHash ||
      typeof cursor.id !== "string" || !isCanonicalUuid(cursor.id) ||
      typeof cursor.iat !== "number" || !Number.isSafeInteger(cursor.iat) ||
      cursor.iat < nowMs - 60 * 60 * 1000 || cursor.iat > nowMs + 60_000
    ) {
      throw new HttpError(400, "invalid_cursor", "The cursor is invalid or does not match these filters.");
    }
    if (sort === "title_asc") {
      if (typeof cursor.value !== "string" || cursor.value.length > MAX_TITLE_CHARACTERS) {
        throw new HttpError(400, "invalid_cursor", "The cursor is invalid.");
      }
      clauses.push("(m.title_sort > ? OR (m.title_sort = ? AND m.id > ?))");
      values.push(cursor.value, cursor.value, cursor.id);
    } else {
      if (typeof cursor.value !== "number" || !Number.isSafeInteger(cursor.value)) {
        throw new HttpError(400, "invalid_cursor", "The cursor is invalid.");
      }
      const column = sort === "updated_desc" ? "m.updated_at_ms" : "m.created_at_ms";
      clauses.push(`(${column} < ? OR (${column} = ? AND m.id > ?))`);
      values.push(cursor.value, cursor.value, cursor.id);
    }
  }

  const order = sort === "updated_desc" ? "m.updated_at_ms DESC, m.id ASC"
    : sort === "created_desc" ? "m.created_at_ms DESC, m.id ASC"
    : "m.title_sort ASC, m.id ASC";
  const result = await env.DB.prepare(
    `${MAP_SELECT} WHERE ${clauses.join(" AND ")} ORDER BY ${order} LIMIT ?`,
  ).bind(...values, limit + 1).all<MapRow>();
  const rows = result.results;
  const hasMore = rows.length > limit;
  const selected = rows.slice(0, limit);
  let nextCursor: string | null = null;
  const last = selected.at(-1);
  if (hasMore && last !== undefined) {
    const value = sort === "updated_desc" ? last.updated_at_ms
      : sort === "created_desc" ? last.created_at_ms
      : last.title_sort;
    nextCursor = await signedValue(config, "map-cursor", {
      v: 1,
      sort,
      filterHash: expectedFilterHash,
      value,
      id: last.id,
      iat: nowMs,
    });
  }
  const page: MapPage & { requestId: string } = { requestId, items: selected.map(mapDto), nextCursor };
  const serialized = JSON.stringify(page);
  if (new TextEncoder().encode(serialized).length > 1024 * 1024) {
    throw new HttpError(503, "temporarily_unavailable", "The bounded map response could not be produced.");
  }
  return jsonResponse(page, 200, requestId);
}

/**
 * Bounded compatibility document consumed by the current in-game catalogue client.
 * Object locators are API paths relative to /v1/catalog, never private R2 keys.
 */
export async function catalogDocument(
  env: Env,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const result = await env.DB.prepare(
    `${MAP_SELECT}
      WHERE m.status = 'published' AND v.status = 'published'
        AND v.revision = m.current_revision
        AND v.client_version = 29 AND v.map_format_version = 29
      ORDER BY m.updated_at_ms DESC, m.id ASC
      LIMIT 2001`,
  ).all<MapRow>();
  if (result.results.length > 2000) {
    throw new HttpError(503, "temporarily_unavailable", "The bounded catalogue could not be produced.");
  }
  const maps = result.results.map((row) => {
    const map = mapDto(row);
    const bundleKey = map.downloadUrl.startsWith("/v1/") ? map.downloadUrl.slice(4) : "";
    if (bundleKey === "") {
      throw new HttpError(503, "object_metadata_mismatch", "Stored bundle metadata is invalid.");
    }
    const entry: Record<string, unknown> = {
      id: map.id,
      code: map.code,
      revision: map.revision,
      title: map.title,
      description: map.description,
      creatorIds: map.creatorIds,
      tags: map.tags,
      createdAtMs: map.createdAtMs,
      updatedAtMs: map.updatedAtMs,
      clientVersion: map.clientVersion,
      mapFormatVersion: map.mapFormatVersion,
      minimumReviveVersion: map.minimumReviveVersion,
      sizeBytes: map.sizeBytes,
      sha256: map.sha256,
      bundleKey,
      configurations: map.configurations,
      validations: map.validations,
    };
    if (map.reviveVersion !== undefined) entry.reviveVersion = map.reviveVersion;
    if (map.thumbnail !== undefined) {
      const thumbnailKey = map.thumbnail.url.startsWith("/v1/") ? map.thumbnail.url.slice(4) : "";
      if (thumbnailKey === "") {
        throw new HttpError(503, "object_metadata_mismatch", "Stored thumbnail metadata is invalid.");
      }
      entry.thumbnailKey = thumbnailKey;
      entry.thumbnailSizeBytes = map.thumbnail.sizeBytes;
      entry.thumbnailSha256 = map.thumbnail.sha256;
    }
    return entry;
  });
  const document = {
    schemaVersion: 1,
    generatedAtUtc: new Date(nowMs).toISOString(),
    maps,
  };
  const serialized = JSON.stringify(document);
  if (new TextEncoder().encode(serialized).length > 2 * 1024 * 1024) {
    throw new HttpError(503, "temporarily_unavailable", "The bounded catalogue could not be produced.");
  }
  return jsonResponse(document, 200, requestId, {
    "Cache-Control": "public, max-age=60, stale-while-revalidate=300",
  });
}

async function publishedMapRow(env: Env, mapId: string, revision?: number): Promise<MapRow | null> {
  const revisionClause = revision === undefined ? "v.revision = m.current_revision" : "v.revision = ?";
  const statement = env.DB.prepare(
    `${MAP_SELECT}
      WHERE m.id = ? AND m.status = 'published' AND v.status = 'published'
        AND ${revisionClause} AND v.client_version = 29 AND v.map_format_version = 29`,
  );
  return revision === undefined
    ? statement.bind(mapId).first<MapRow>()
    : statement.bind(mapId, revision).first<MapRow>();
}

export async function mapDetail(env: Env, mapId: string, requestId: string): Promise<Response> {
  if (!isCanonicalUuid(mapId)) throw new HttpError(404, "map_not_found", "The map was not found.");
  const row = await publishedMapRow(env, mapId);
  if (row === null) throw new HttpError(404, "map_not_found", "The map was not found.");
  return jsonResponse({ requestId, map: mapDto(row) }, 200, requestId);
}

export async function mapVersions(env: Env, mapId: string, requestId: string): Promise<Response> {
  if (!isCanonicalUuid(mapId)) throw new HttpError(404, "map_not_found", "The map was not found.");
  const visible = await env.DB.prepare(`SELECT 1 FROM maps WHERE id = ? AND status = 'published'`).bind(mapId).first();
  if (visible === null) throw new HttpError(404, "map_not_found", "The map was not found.");
  const result = await env.DB.prepare(
    `${MAP_SELECT}
      WHERE m.id = ? AND m.status = 'published' AND v.status = 'published'
        AND v.client_version = 29 AND v.map_format_version = 29
      ORDER BY v.revision DESC LIMIT 50`,
  ).bind(mapId).all<MapRow>();
  return jsonResponse({ requestId, items: result.results.map(mapDto), nextCursor: null }, 200, requestId);
}

export async function downloadableVersion(env: Env, mapId: string, revision: number): Promise<MapRow> {
  if (!isCanonicalUuid(mapId) || !Number.isSafeInteger(revision) || revision < 1) {
    throw new HttpError(404, "revision_not_found", "The map revision was not found.");
  }
  const row = await publishedMapRow(env, mapId, revision);
  if (row === null) throw new HttpError(404, "revision_not_found", "The map revision was not found.");
  mapDto(row);
  return row;
}

export type { MapRow };
