import { env } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";
import { sha256Hex, type AuthPrincipal } from "@ss2revive/community-contracts";
import { runtimeConfig } from "../src/config";
import { issueAccessToken } from "../src/crypto";
import { buildLocalFixture } from "../src/local-fixture";
import {
  getUploadStatus,
  putUploadBundle,
  reserveUpload,
  validateUploadMessage,
} from "../src/uploads";

const STEAM_ID = "76561198145479980";
const ORIGIN = "http://127.0.0.1:8787";
const principal: AuthPrincipal = {
  steamId64: STEAM_ID,
  sessionId: "11111111-1111-1111-1111-111111111111",
  scopes: ["maps:read", "maps:download", "maps:upload", "maps:manage", "maps:report", "maps:moderate"],
};

async function clearState(): Promise<void> {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM moderation_events"),
    env.DB.prepare("DELETE FROM map_reports"),
    env.DB.prepare("DELETE FROM upload_usage_daily"),
    env.DB.prepare("DELETE FROM account_storage"),
    env.DB.prepare("DELETE FROM map_uploads"),
    env.DB.prepare("DELETE FROM map_tags"),
    env.DB.prepare("DELETE FROM map_versions"),
    env.DB.prepare("DELETE FROM maps"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM refresh_token_families"),
    env.DB.prepare("DELETE FROM auth_sessions"),
    env.DB.prepare("DELETE FROM device_auth_sessions"),
    env.DB.prepare("DELETE FROM steam_openid_sessions"),
    env.DB.prepare("DELETE FROM users"),
  ]);
  const objects = await env.MAP_BUCKET.list({ limit: 1000 });
  if (objects.objects.length > 0) await env.MAP_BUCKET.delete(objects.objects.map((object) => object.key));
  const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
  await env.DB.prepare(
    `INSERT INTO users (steam_id64, status, created_at, last_login_at) VALUES (?, 'active', ?, ?)`,
  ).bind(STEAM_ID, nowMs, nowMs).run();
}

async function reserve(bytes: Uint8Array, sha256: string, nowMs: number): Promise<string> {
  const response = await reserveUpload(
    new Request(`${ORIGIN}/v1/uploads`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sizeBytes: bytes.length, sha256 }),
    }),
    env,
    runtimeConfig(env),
    principal,
    crypto.randomUUID(),
    nowMs,
  );
  const body = await response.json<{ uploadId: string }>();
  return body.uploadId;
}

async function put(uploadId: string, bytes: Uint8Array, sha256: string, nowMs: number): Promise<Response> {
  const requestBody = new ArrayBuffer(bytes.length);
  new Uint8Array(requestBody).set(bytes);
  return putUploadBundle(
    new Request(`${ORIGIN}/v1/uploads/${uploadId}/bundle`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/vnd.ss2revive.level",
        "Content-Length": String(bytes.length),
        "X-Content-SHA256": sha256,
      },
      body: requestBody,
    }),
    env,
    runtimeConfig(env),
    principal,
    uploadId,
    crypto.randomUUID(),
    nowMs,
  );
}

beforeEach(clearState);

describe("authenticated map uploads", () => {
  it("quarantines, validates, publishes, and exposes a current-format bundle", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const fixture = await buildLocalFixture();
    const uploadId = await reserve(fixture.bytes, fixture.sha256, nowMs);
    expect((await put(uploadId, fixture.bytes, fixture.sha256, nowMs + 1)).status).toBe(202);

    await validateUploadMessage(env, { kind: "map-upload", uploadId }, nowMs + 2);
    const status = await getUploadStatus(
      new Request(`${ORIGIN}/v1/uploads/${uploadId}`),
      env,
      runtimeConfig(env),
      principal,
      uploadId,
      crypto.randomUUID(),
    );
    expect(await status.json()).toMatchObject({
      uploadId,
      status: "published",
      mapId: "1a658233-92c5-4b63-87fc-4740c855730b",
      revision: 1,
    });
    expect(await env.DB.prepare(
      "SELECT status FROM map_versions WHERE map_id = ? AND revision = 1",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").first()).toMatchObject({ status: "published" });
    expect(await env.MAP_BUCKET.head(fixture.bundleKey)).not.toBeNull();
    expect(await env.MAP_BUCKET.head(fixture.thumbnailKey)).not.toBeNull();
  });

  it("rejects a bundle whose creator does not match the authenticated Steam account", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const fixture = await buildLocalFixture();
    const bytes = fixture.bytes.slice();
    const source = new TextEncoder().encode("76561198145479980");
    const replacement = new TextEncoder().encode("76561198145479981");
    const index = bytes.findIndex((_, offset) => source.every((value, inner) => bytes[offset + inner] === value));
    expect(index).toBeGreaterThan(0);
    bytes.set(replacement, index);
    const sha256 = await sha256Hex(bytes);
    const uploadId = await reserve(bytes, sha256, nowMs);
    expect((await put(uploadId, bytes, sha256, nowMs + 1)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId }, nowMs + 2);
    expect(await env.DB.prepare(
      "SELECT status, error_code FROM map_uploads WHERE id = ?",
    ).bind(uploadId).first()).toMatchObject({ status: "rejected", error_code: "creator_mismatch" });
    expect(await env.DB.prepare("SELECT id FROM maps").first()).toBeNull();
  });

  it("publishes exactly once when the queue delivers the same upload concurrently", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const fixture = await buildLocalFixture();
    const uploadId = await reserve(fixture.bytes, fixture.sha256, nowMs);
    expect((await put(uploadId, fixture.bytes, fixture.sha256, nowMs + 1)).status).toBe(202);

    await Promise.all([
      validateUploadMessage(env, { kind: "map-upload", uploadId }, nowMs + 2),
      validateUploadMessage(env, { kind: "map-upload", uploadId }, nowMs + 2),
    ]);

    expect(await env.DB.prepare(
      "SELECT status FROM map_uploads WHERE id = ?",
    ).bind(uploadId).first()).toMatchObject({ status: "published" });
    expect(await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM map_versions WHERE map_id = ? AND revision = 1",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").first()).toMatchObject({ count: 1 });
    expect(await env.MAP_BUCKET.head(fixture.bundleKey)).not.toBeNull();
    expect(await env.MAP_BUCKET.head(fixture.thumbnailKey)).not.toBeNull();
  });

  it("accepts an unchanged revision again when only export time and validation state changed", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const fixture = await buildLocalFixture();
    const firstId = await reserve(fixture.bytes, fixture.sha256, nowMs);
    expect((await put(firstId, fixture.bytes, fixture.sha256, nowMs + 1)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId: firstId }, nowMs + 2);

    const repeated = await buildLocalFixture({
      exportedAtMs: Date.UTC(2026, 0, 1, 0, 0, 3),
      validations: [{
        id: "f9dab629-2372-4eb3-b24a-063f382f6043",
        description: "Local deterministic fixture",
        validated: true,
      }],
    });
    expect(repeated.sha256).not.toBe(fixture.sha256);
    const repeatedId = await reserve(repeated.bytes, repeated.sha256, nowMs + 3);
    expect((await put(repeatedId, repeated.bytes, repeated.sha256, nowMs + 4)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId: repeatedId }, nowMs + 5);

    expect(await env.DB.prepare(
      "SELECT status, map_id, revision FROM map_uploads WHERE id = ?",
    ).bind(repeatedId).first()).toMatchObject({
      status: "published",
      map_id: "1a658233-92c5-4b63-87fc-4740c855730b",
      revision: 1,
    });
    expect(await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM map_versions WHERE map_id = ?",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").first()).toMatchObject({ count: 1 });
  });

  it("replaces the current community revision after the same owner saves a newer revision", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const first = await buildLocalFixture();
    const firstId = await reserve(first.bytes, first.sha256, nowMs);
    expect((await put(firstId, first.bytes, first.sha256, nowMs + 1)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId: firstId }, nowMs + 2);

    const second = await buildLocalFixture({
      contentVersion: 2,
      exportedAtMs: Date.UTC(2026, 0, 1, 0, 0, 4),
    });
    const secondId = await reserve(second.bytes, second.sha256, nowMs + 3);
    expect((await put(secondId, second.bytes, second.sha256, nowMs + 4)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId: secondId }, nowMs + 5);

    expect(await env.DB.prepare(
      "SELECT status, current_revision FROM maps WHERE id = ?",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").first()).toMatchObject({
      status: "published",
      current_revision: 2,
    });
    expect(await env.DB.prepare(
      "SELECT status FROM map_versions WHERE map_id = ? AND revision = 2",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").first()).toMatchObject({ status: "published" });
    expect(await env.MAP_BUCKET.head(second.bundleKey)).not.toBeNull();
  });

  it("does not let an owner bypass a maintainer takedown by uploading again", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const first = await buildLocalFixture();
    const firstId = await reserve(first.bytes, first.sha256, nowMs);
    expect((await put(firstId, first.bytes, first.sha256, nowMs + 1)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId: firstId }, nowMs + 2);
    await env.DB.prepare(
      `UPDATE maps SET status = 'archived', moderation_reason = 'Maintainer review'
        WHERE id = ?`,
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").run();

    const second = await buildLocalFixture({
      contentVersion: 2,
      exportedAtMs: Date.UTC(2026, 0, 1, 0, 0, 4),
    });
    const secondId = await reserve(second.bytes, second.sha256, nowMs + 3);
    expect((await put(secondId, second.bytes, second.sha256, nowMs + 4)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId: secondId }, nowMs + 5);

    expect(await env.DB.prepare(
      "SELECT status, error_code FROM map_uploads WHERE id = ?",
    ).bind(secondId).first()).toMatchObject({
      status: "rejected",
      error_code: "map_under_moderation",
    });
    expect(await env.DB.prepare(
      "SELECT status, current_revision, moderation_reason FROM maps WHERE id = ?",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").first()).toMatchObject({
      status: "archived",
      current_revision: 1,
      moderation_reason: "Maintainer review",
    });
  });

  it("issues publishing scope to every Steam identity and moderation only to maintainers", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const config = runtimeConfig(env);
    expect((await issueAccessToken(config, STEAM_ID, crypto.randomUUID(), nowMs)).scope)
      .toContain("maps:upload");
    expect((await issueAccessToken(config, "76561198145479981", crypto.randomUUID(), nowMs)).scope)
      .toContain("maps:upload");
    expect((await issueAccessToken(config, STEAM_ID, crypto.randomUUID(), nowMs)).scope)
      .toContain("maps:moderate");
    expect((await issueAccessToken(config, "76561198145479981", crypto.randomUUID(), nowMs)).scope)
      .not.toContain("maps:moderate");
  });

  it("enforces the atomic UTC-day reservation quota", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const fixture = await buildLocalFixture();
    const config = { ...runtimeConfig(env), uploadDailyLimit: 1 };
    const request = () => new Request(`${ORIGIN}/v1/uploads`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sizeBytes: fixture.bytes.length, sha256: fixture.sha256 }),
    });
    expect((await reserveUpload(request(), env, config, principal, crypto.randomUUID(), nowMs)).status).toBe(201);
    await expect(reserveUpload(request(), env, config, principal, crypto.randomUUID(), nowMs + 1))
      .rejects.toMatchObject({ code: "daily_upload_limit_reached", status: 429 });
  });

  it("rejects a new map after the lifetime per-account map quota is reached", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const statements: D1PreparedStatement[] = [];
    for (let index = 0; index < 25; index += 1) {
      const id = crypto.randomUUID();
      statements.push(env.DB.prepare(
        `INSERT INTO maps
           (id, status, current_revision, title, title_sort, description,
            created_at_ms, updated_at_ms, owner_steam_id64)
         VALUES (?, 'archived', 1, ?, ?, '', ?, ?, ?)`,
      ).bind(id, `Archived ${index}`, `archived ${index}`, nowMs, nowMs, STEAM_ID));
    }
    await env.DB.batch(statements);
    const fixture = await buildLocalFixture();
    const uploadId = await reserve(fixture.bytes, fixture.sha256, nowMs + 1);
    expect((await put(uploadId, fixture.bytes, fixture.sha256, nowMs + 2)).status).toBe(202);
    await validateUploadMessage(env, { kind: "map-upload", uploadId }, nowMs + 3);
    expect(await env.DB.prepare(
      "SELECT status, error_code FROM map_uploads WHERE id = ?",
    ).bind(uploadId).first()).toMatchObject({ status: "rejected", error_code: "map_quota_reached" });
  });
});
