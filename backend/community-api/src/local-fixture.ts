import { inspectLevelBundle, sha256Hex } from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { HttpError } from "./http";

const FIXTURE_ID = "1a658233-92c5-4b63-87fc-4740c855730b";
const FIXTURE_CREATED_AT = Date.UTC(2026, 0, 1, 0, 0, 0);
const encoder = new TextEncoder();
const FIXTURE_CONFIGURATIONS = [{
  id: "5f3ff27f-126f-43b3-999f-e55cd44f4a27",
  numberPlayers: 1,
  numberTeams: 1,
  teamMode: "COOP",
  levelTeamConfigurations: [{ objectives: [""], playersInTeam: [] }],
}];
const FIXTURE_VALIDATIONS = [{
  id: "f9dab629-2372-4eb3-b24a-063f382f6043",
  description: "Local deterministic fixture",
  validated: false,
}];

function writeUint16(output: number[], value: number): void {
  output.push(value & 0xff, (value >>> 8) & 0xff);
}

function writeInt32(output: number[], value: number): void {
  output.push(value & 0xff, (value >>> 8) & 0xff, (value >>> 16) & 0xff, (value >>> 24) & 0xff);
}

function writeEntry(output: number[], name: string, payload: Uint8Array): void {
  const nameBytes = encoder.encode(name);
  writeUint16(output, nameBytes.length);
  output.push(...nameBytes);
  writeInt32(output, payload.length);
  output.push(...payload);
}

function writeBits(bytes: Uint8Array, start: number, value: number, count: number): number {
  for (let bit = 0; bit < count; bit += 1) {
    if ((Math.floor(value / 2 ** bit) & 1) !== 0) {
      const absolute = start + bit;
      bytes[Math.floor(absolute / 8)]! |= 1 << (absolute % 8);
    }
  }
  return start + count;
}

function writeGameString(bytes: Uint8Array, start: number, value: string): number {
  let bit = Math.ceil(start / 8) * 8;
  bit = writeBits(bytes, bit, value.length, 8);
  for (let index = 0; index < value.length; index += 1) {
    bit = writeBits(bytes, bit, value.charCodeAt(index), 16);
  }
  return bit;
}

function fixtureLevelContent(): Uint8Array {
  const bytes = new Uint8Array(256);
  bytes.set(encoder.encode("Surgeons"), 0);
  bytes[8] = 29;
  let bit = 80;
  bit = writeGameString(bytes, bit, FIXTURE_ID);
  bit = writeBits(bytes, bit, 0, 1); // uncompressed format-29 body
  bit = Math.ceil(bit / 8) * 8;
  bit = writeGameString(bytes, bit, "Fixture");
  for (const dimension of [1, 1, 1]) bit = writeBits(bytes, bit, dimension + 0x80000000, 32);
  bit = writeBits(bytes, bit, 0, 1); // one empty voxel
  bit = writeBits(bytes, bit, 0x80000000, 32); // textured voxel value 0
  bit = writeBits(bytes, bit, 0x80000000, 32); // zero prop definitions
  bit = writeBits(bytes, bit, 0x80000000, 32); // zero prop instances
  return bytes.slice(0, Math.ceil(bit / 8));
}

function fixtureThumbnail(): Uint8Array {
  const bytes = new Uint8Array(17);
  let bit = 0;
  bit = writeBits(bytes, bit, 1, 31);
  bit = writeBits(bytes, bit, 1, 31);
  bit = writeBits(bytes, bit, 4, 4);
  writeBits(bytes, bit, 4, 31);
  bytes.set([0x33, 0x66, 0x99, 0xff], 13);
  return bytes;
}

export async function buildLocalFixture(options: {
  configurations?: unknown[];
  validations?: unknown[];
  contentVersion?: number;
  exportedAtMs?: number;
  title?: string;
  tags?: string[];
  /** Test hook: container entries the real writer never emits, used to pin rejection behaviour. */
  extraEntries?: ReadonlyArray<{ name: string; payload: Uint8Array }>;
  /** Test hook: replaces the generated thumbnail payload verbatim. */
  thumbnail?: Uint8Array;
  /** Test hook: the server time the manifest is validated against. */
  nowMs?: number;
} = {}): Promise<{
  bytes: Uint8Array;
  thumbnail: Uint8Array;
  bundleKey: string;
  thumbnailKey: string;
  sha256: string;
  thumbnailSha256: string;
}> {
  const content = fixtureLevelContent();
  const thumbnail = options.thumbnail ?? fixtureThumbnail();
  const contentSha256 = await sha256Hex(content);
  const manifest = encoder.encode(JSON.stringify({
    bundleVersion: 2,
    id: FIXTURE_ID,
    title: options.title ?? "Phase 0 Local Test Room",
    description: "A deterministic local-only backend fixture.",
    createdAt: FIXTURE_CREATED_AT,
    exportedAt: options.exportedAtMs ?? FIXTURE_CREATED_AT + 1000,
    clientVersion: 29,
    contentVersion: options.contentVersion ?? 1,
    reviveVersion: "0.1.0",
    contentSha256,
    creators: ["STEAM-76561198145479980-------------"],
    tags: options.tags ?? ["TEAM_COOP", "LOCAL_TEST"],
    configurations: options.configurations ?? FIXTURE_CONFIGURATIONS,
    validations: options.validations ?? FIXTURE_VALIDATIONS,
  }));
  const output = [...encoder.encode("SS2REVIVE LEVEL\n")];
  writeUint16(output, 2);
  writeEntry(output, "asset.json", manifest);
  writeEntry(output, "level.bin", content);
  writeEntry(output, "thumb.png", thumbnail);
  for (const entry of options.extraEntries ?? []) writeEntry(output, entry.name, entry.payload);
  writeUint16(output, 0);
  const bytes = new Uint8Array(output);
  const inspected = await inspectLevelBundle(bytes, { nowMs: options.nowMs ?? Date.UTC(2026, 7, 8) });
  const thumbnailSha256 = await sha256Hex(thumbnail);
  return {
    bytes,
    thumbnail,
    sha256: inspected.bundleSha256,
    thumbnailSha256,
    bundleKey: `approved/maps/${FIXTURE_ID}/r${inspected.manifest.contentVersion}/${inspected.bundleSha256}.ss2level`,
    thumbnailKey: `approved/thumbnails/${FIXTURE_ID}/r${inspected.manifest.contentVersion}/${thumbnailSha256}.bin`,
  };
}

export async function seedLocalFixture(env: Env, config: RuntimeConfig): Promise<{ id: string; revision: number }> {
  if (!config.allowMockAuth) throw new HttpError(404, "not_found", "The route was not found.");
  const fixture = await buildLocalFixture();
  await env.MAP_BUCKET.put(fixture.bundleKey, fixture.bytes, {
    httpMetadata: { contentType: "application/vnd.ss2revive.level" },
    customMetadata: { sha256: fixture.sha256 },
  });
  await env.MAP_BUCKET.put(fixture.thumbnailKey, fixture.thumbnail, {
    httpMetadata: { contentType: "application/octet-stream" },
    customMetadata: { sha256: fixture.thumbnailSha256 },
  });

  await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO users (steam_id64, status, created_at, last_login_at)
       VALUES (?, 'active', ?, ?)
       ON CONFLICT(steam_id64) DO UPDATE SET last_login_at = excluded.last_login_at`,
    ).bind(config.mockSteamId64, FIXTURE_CREATED_AT, FIXTURE_CREATED_AT),
    env.DB.prepare(
      `INSERT INTO maps
         (id, status, current_revision, title, title_sort, description, created_at_ms, updated_at_ms,
          owner_steam_id64)
       VALUES (?, 'published', 1, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(id) DO UPDATE SET
         status = excluded.status, current_revision = excluded.current_revision,
         title = excluded.title, title_sort = excluded.title_sort, description = excluded.description,
         created_at_ms = excluded.created_at_ms, updated_at_ms = excluded.updated_at_ms,
         owner_steam_id64 = excluded.owner_steam_id64`,
    ).bind(
      FIXTURE_ID,
      "Phase 0 Local Test Room",
      "phase 0 local test room",
      "A deterministic local-only backend fixture.",
      FIXTURE_CREATED_AT,
      FIXTURE_CREATED_AT + 1000,
      config.mockSteamId64,
    ),
    env.DB.prepare(`DELETE FROM map_tags WHERE map_id = ? AND revision = 1`).bind(FIXTURE_ID),
    env.DB.prepare(`DELETE FROM map_versions WHERE map_id = ? AND revision = 1`).bind(FIXTURE_ID),
    env.DB.prepare(
      `INSERT INTO map_versions
         (map_id, revision, status, code, creator_ids_json, tags_json,
          configurations_json, validations_json, player_counts_csv,
          client_version, map_format_version, minimum_revive_version, revive_version,
          size_bytes, sha256, bundle_key,
          thumbnail_size_bytes, thumbnail_sha256, thumbnail_key, created_at_ms)
       VALUES (?, 1, 'published', ?, ?, ?, ?, ?, ?, 29, 29, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(
      FIXTURE_ID,
      "M4JlGsWSY0uH_EdAyFVzCw",
      JSON.stringify(["STEAM-76561198145479980-------------"]),
      JSON.stringify(["TEAM_COOP", "LOCAL_TEST"]),
      JSON.stringify(FIXTURE_CONFIGURATIONS),
      JSON.stringify(FIXTURE_VALIDATIONS),
      ",1,",
      "0.1.0",
      "0.1.0",
      fixture.bytes.length,
      fixture.sha256,
      fixture.bundleKey,
      fixture.thumbnail.length,
      fixture.thumbnailSha256,
      fixture.thumbnailKey,
      FIXTURE_CREATED_AT + 1000,
    ),
    env.DB.prepare(`INSERT INTO map_tags (map_id, revision, tag) VALUES (?, 1, ?)`).bind(FIXTURE_ID, "TEAM_COOP"),
    env.DB.prepare(`INSERT INTO map_tags (map_id, revision, tag) VALUES (?, 1, ?)`).bind(FIXTURE_ID, "LOCAL_TEST"),
  ]);
  return { id: FIXTURE_ID, revision: 1 };
}
