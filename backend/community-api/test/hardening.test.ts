import { env } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";
import {
  inspectLevelBundle,
  MAX_STRUCTURED_METADATA_BYTES,
  sha256Hex,
  type AuthPrincipal,
} from "@ss2revive/community-contracts";
import { runtimeConfig } from "../src/config";
import { takedownMap } from "../src/community-controls";
import { buildLocalFixture } from "../src/local-fixture";
import { cleanupExpiredState } from "../src/maintenance";
import { catalogDocument, listMaps } from "../src/maps";
import { anonymousRateKey } from "../src/rate-limit";
import { putUploadBundle, reserveUpload, validateUploadMessage } from "../src/uploads";

const STEAM_ID = "76561198145479980";
const MAP_ID = "1a658233-92c5-4b63-87fc-4740c855730b";
const ORIGIN = "http://127.0.0.1:8787";
const NOW = Date.UTC(2026, 7, 8, 12, 0, 0);

const principal: AuthPrincipal = {
  steamId64: STEAM_ID,
  sessionId: "11111111-1111-1111-1111-111111111111",
  scopes: ["maps:read", "maps:download", "maps:upload", "maps:manage", "maps:report", "maps:moderate"],
};

const testContext = {
  waitUntil(promise: Promise<unknown>): void {
    void promise.catch(() => undefined);
  },
  passThroughOnException(): void {},
  props: {},
} as unknown as ExecutionContext;

async function clearState(): Promise<void> {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM maintenance_state"),
    env.DB.prepare("DELETE FROM moderation_events"),
    env.DB.prepare("DELETE FROM map_reports"),
    env.DB.prepare("DELETE FROM upload_usage_daily"),
    env.DB.prepare("DELETE FROM account_storage"),
    env.DB.prepare("DELETE FROM map_uploads"),
    env.DB.prepare("DELETE FROM map_tags"),
    env.DB.prepare("DELETE FROM map_versions"),
    env.DB.prepare("DELETE FROM maps"),
    env.DB.prepare("DELETE FROM steam_openid_sessions"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM refresh_token_families"),
    env.DB.prepare("DELETE FROM auth_sessions"),
    env.DB.prepare("DELETE FROM device_auth_sessions"),
    env.DB.prepare("DELETE FROM users"),
  ]);
  const objects = await env.MAP_BUCKET.list({ limit: 1000 });
  if (objects.objects.length > 0) await env.MAP_BUCKET.delete(objects.objects.map((object) => object.key));
  await env.DB.prepare(
    `INSERT INTO users (steam_id64, status, created_at, last_login_at) VALUES (?, 'active', ?, ?)`,
  ).bind(STEAM_ID, NOW, NOW).run();
}

async function publish(
  fixture: { bytes: Uint8Array; sha256: string },
  atMs: number,
): Promise<string> {
  const reserved = await reserveUpload(
    new Request(`${ORIGIN}/v1/uploads`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sizeBytes: fixture.bytes.length, sha256: fixture.sha256 }),
    }),
    env, runtimeConfig(env), principal, crypto.randomUUID(), atMs,
  );
  const { uploadId } = await reserved.json<{ uploadId: string }>();
  const body = new ArrayBuffer(fixture.bytes.length);
  new Uint8Array(body).set(fixture.bytes);
  await putUploadBundle(
    new Request(`${ORIGIN}/v1/uploads/${uploadId}/bundle`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/vnd.ss2revive.level",
        "Content-Length": String(fixture.bytes.length),
        "X-Content-SHA256": fixture.sha256,
      },
      body,
    }),
    env, runtimeConfig(env), principal, uploadId, crypto.randomUUID(), atMs + 1,
  );
  await validateUploadMessage(env, { kind: "map-upload", uploadId }, atMs + 2);
  return uploadId;
}

/** A configuration whose objectives are padded up to the per-field character limits. */
function fatConfigurations(): unknown[] {
  return [{
    id: "5f3ff27f-126f-43b3-999f-e55cd44f4a27",
    numberPlayers: 1,
    numberTeams: 1,
    teamMode: "COOP",
    levelTeamConfigurations: [{
      objectives: Array.from({ length: 32 }, (_, index) => `${index}`.padEnd(128, "一")),
      playersInTeam: [],
    }],
  }];
}

beforeEach(clearState);

describe("catalogue availability", () => {
  it("refuses a manifest whose echoed metadata exceeds the per-map budget", async () => {
    await expect(buildLocalFixture({ configurations: fatConfigurations() }))
      .rejects.toMatchObject({ code: "manifest_metadata_oversized" });
  });

  it("keeps the metadata budget generous enough for a fully populated real manifest", async () => {
    const fixture = await buildLocalFixture({
      validations: Array.from({ length: 32 }, (_, index) => ({
        id: `f9dab629-2372-4eb3-b24a-063f382f60${index.toString(16).padStart(2, "0")}`,
        description: `Objective ${index} completed as expected`,
        validated: true,
      })),
    });
    const parsed = await inspectLevelBundle(fixture.bytes, { nowMs: NOW });
    expect(parsed.manifest.validations).toHaveLength(32);
    expect(new TextEncoder().encode(JSON.stringify({
      tags: parsed.manifest.tags,
      configurations: parsed.manifest.configurations,
      validations: parsed.manifest.validations,
    })).length).toBeLessThanOrEqual(MAX_STRUCTURED_METADATA_BYTES);
  });

  it("degrades the catalogue instead of failing when the response budget is reached", async () => {
    // One oversized stored row stands in for the many a publisher would previously have needed.
    const bulky = JSON.stringify(Array.from({ length: 8 }, (_, index) => ({
      id: `5f3ff27f-126f-43b3-999f-e55cd44f4a${index.toString(16).padStart(2, "0")}`,
      note: "一".repeat(60_000),
    })));
    for (let index = 0; index < 60; index += 1) {
      const id = `1a658233-92c5-4b63-87fc-4740c85573${index.toString(16).padStart(2, "0")}`;
      await env.DB.batch([
        env.DB.prepare(
          `INSERT INTO maps (id, status, current_revision, title, title_sort, description,
                             created_at_ms, updated_at_ms, owner_steam_id64)
           VALUES (?, 'published', 1, ?, ?, '', ?, ?, ?)`,
        ).bind(id, `Map ${index}`, `map ${index}`, NOW, NOW + index, STEAM_ID),
        env.DB.prepare(
          `INSERT INTO map_versions (map_id, revision, status, code, creator_ids_json, tags_json,
             configurations_json, validations_json, player_counts_csv, client_version,
             map_format_version, minimum_revive_version, revive_version, size_bytes, sha256,
             bundle_key, created_at_ms)
           VALUES (?, 1, 'published', ?, '[]', '[]', ?, '[]', ',1,', 29, 29, '1.1.0', '1.1.0', 1, ?, ?, ?)`,
        ).bind(
          id,
          (await import("@ss2revive/community-contracts")).levelCodeFromId(id),
          bulky,
          "a".repeat(64),
          `approved/maps/${id}/r1/${"a".repeat(64)}.ss2level`,
          NOW,
        ),
      ]);
    }

    const response = await catalogDocument(env, crypto.randomUUID(), NOW);
    expect(response.status).toBe(200);
    const document = await response.json<{ truncated: boolean; maps: unknown[] }>();
    expect(document.truncated).toBe(true);
    expect(document.maps.length).toBeGreaterThan(0);
    expect(document.maps.length).toBeLessThan(60);

    const listed = await listMaps(
      new Request(`${ORIGIN}/v1/maps?limit=50`), env, runtimeConfig(env), crypto.randomUUID(), NOW,
    );
    expect(listed.status).toBe(200);
    const page = await listed.json<{ items: unknown[]; nextCursor: string | null }>();
    expect(page.items.length).toBeGreaterThan(0);
    expect(page.nextCursor).not.toBeNull();
  });
});

describe("bundle container strictness", () => {
  it("rejects an entry this level format does not define", async () => {
    await expect(buildLocalFixture({
      extraEntries: [{ name: "payload.bin", payload: new Uint8Array(1024) }],
    })).rejects.toMatchObject({ code: "bundle_unknown_entry" });
  });

  it("rejects an image whose payload is longer than its pixels require", async () => {
    // 1x1 RGBA32 needs exactly 4 bytes; the header used to permit an arbitrarily larger payload.
    const padded = new Uint8Array(13 + 4096);
    padded.set([0x02, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00], 0);
    await expect(buildLocalFixture({ thumbnail: padded }))
      .rejects.toMatchObject({ code: "image_invalid" });
  });
});

describe("moderation integrity", () => {
  it("lets a maintainer takedown win over a validation that completes afterwards", async () => {
    const first = await buildLocalFixture();
    await publish(first, NOW);

    // The validator reads moderation state, then the maintainer removes the map, then the
    // validator's publication lands. The takedown must survive.
    const second = await buildLocalFixture({ contentVersion: 2, exportedAtMs: NOW - 1000, nowMs: NOW });
    const reserved = await reserveUpload(
      new Request(`${ORIGIN}/v1/uploads`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sizeBytes: second.bytes.length, sha256: second.sha256 }),
      }),
      env, runtimeConfig(env), principal, crypto.randomUUID(), NOW + 10,
    );
    const { uploadId } = await reserved.json<{ uploadId: string }>();
    const body = new ArrayBuffer(second.bytes.length);
    new Uint8Array(body).set(second.bytes);
    await putUploadBundle(
      new Request(`${ORIGIN}/v1/uploads/${uploadId}/bundle`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/vnd.ss2revive.level",
          "Content-Length": String(second.bytes.length),
          "X-Content-SHA256": second.sha256,
        },
        body,
      }),
      env, runtimeConfig(env), principal, uploadId, crypto.randomUUID(), NOW + 11,
    );

    await takedownMap(
      new Request(`${ORIGIN}/v1/moderation/maps/${MAP_ID}/takedown`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason: "Removed pending review" }),
      }),
      env, runtimeConfig(env), testContext, principal, MAP_ID, crypto.randomUUID(), NOW + 12,
    );
    await validateUploadMessage(env, { kind: "map-upload", uploadId }, NOW + 13);

    expect(await env.DB.prepare(
      "SELECT status, moderation_reason, current_revision FROM maps WHERE id = ?",
    ).bind(MAP_ID).first()).toMatchObject({
      status: "archived",
      moderation_reason: "Removed pending review",
      current_revision: 1,
    });
    expect(await env.DB.prepare("SELECT status, error_code FROM map_uploads WHERE id = ?")
      .bind(uploadId).first()).toMatchObject({ status: "rejected", error_code: "map_under_moderation" });
  });
});

describe("storage retention", () => {
  it("releases approved objects outside the rollback window and refunds the account", async () => {
    for (let revision = 1; revision <= 4; revision += 1) {
      const fixture = await buildLocalFixture({
        contentVersion: revision,
        exportedAtMs: NOW - 5000 + revision,
        nowMs: NOW,
      });
      await publish(fixture, NOW + revision * 100);
    }

    const retained = await env.DB.prepare(
      "SELECT revision FROM map_versions WHERE map_id = ? ORDER BY revision ASC",
    ).bind(MAP_ID).all<{ revision: number }>();
    expect(retained.results.map((row) => row.revision)).toEqual([2, 3, 4]);

    const objects = await env.MAP_BUCKET.list({ prefix: `approved/maps/${MAP_ID}/`, limit: 100 });
    expect(objects.objects.some((object) => object.key.includes("/r1/"))).toBe(false);
    expect(objects.objects.some((object) => object.key.includes("/r4/"))).toBe(true);

    const storage = await env.DB.prepare(
      "SELECT retained_bytes FROM account_storage WHERE steam_id64 = ?",
    ).bind(STEAM_ID).first<{ retained_bytes: number }>();
    expect(storage).not.toBeNull();
    expect(storage!.retained_bytes).toBeGreaterThan(0);
  });

  it("refuses a revision once the account's retained storage limit is spent", async () => {
    const fixture = await buildLocalFixture();
    await env.DB.prepare(
      `INSERT INTO account_storage (steam_id64, retained_bytes, updated_at) VALUES (?, ?, ?)`,
    ).bind(STEAM_ID, 5 * 1024 * 1024 * 1024, NOW).run();
    const uploadId = await publish(fixture, NOW);
    expect(await env.DB.prepare("SELECT status, error_code FROM map_uploads WHERE id = ?")
      .bind(uploadId).first()).toMatchObject({ status: "rejected", error_code: "storage_quota_reached" });
  });
});

describe("validation lease recovery", () => {
  it("reclaims a validation whose worker died so the reservation is not stuck forever", async () => {
    const fixture = await buildLocalFixture();
    const reserved = await reserveUpload(
      new Request(`${ORIGIN}/v1/uploads`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sizeBytes: fixture.bytes.length, sha256: fixture.sha256 }),
      }),
      env, runtimeConfig(env), principal, crypto.randomUUID(), NOW,
    );
    const { uploadId } = await reserved.json<{ uploadId: string }>();
    const body = new ArrayBuffer(fixture.bytes.length);
    new Uint8Array(body).set(fixture.bytes);
    await putUploadBundle(
      new Request(`${ORIGIN}/v1/uploads/${uploadId}/bundle`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/vnd.ss2revive.level",
          "Content-Length": String(fixture.bytes.length),
          "X-Content-SHA256": fixture.sha256,
        },
        body,
      }),
      env, runtimeConfig(env), principal, uploadId, crypto.randomUUID(), NOW + 1,
    );
    // Simulate an isolate killed between claiming the lease and completing.
    await env.DB.prepare(
      `UPDATE map_uploads SET status = 'validating', validation_lease = ?,
                              validation_started_at = ?, validation_lease_expires_at = ?
        WHERE id = ?`,
    ).bind(crypto.randomUUID(), NOW + 2, NOW + 2 + 600_000, uploadId).run();

    await cleanupExpiredState(env, NOW + 20 * 60 * 1000);

    expect(await env.DB.prepare(
      "SELECT status, validation_lease FROM map_uploads WHERE id = ?",
    ).bind(uploadId).first()).toMatchObject({ status: "uploaded", validation_lease: null });

    // Once reclaimed, a redelivery can complete it normally.
    await validateUploadMessage(env, { kind: "map-upload", uploadId }, NOW + 20 * 60 * 1000 + 1);
    expect(await env.DB.prepare("SELECT status FROM map_uploads WHERE id = ?")
      .bind(uploadId).first()).toMatchObject({ status: "published" });
  });
});

describe("orphan reconciliation", () => {
  it("persists an R2 cursor so bounded sweeps eventually reach later pages", async () => {
    const liveMapId = "2a658233-92c5-4b63-87fc-4740c855730b";
    const liveKeys = Array.from(
      { length: 200 },
      (_, index) => `approved/000-live/${index.toString().padStart(3, "0")}.ss2level`,
    );
    const orphanKey = "approved/zzz-orphan.ss2level";
    await env.DB.prepare(
      `INSERT INTO maps (id, status, current_revision, title, title_sort, description,
                         created_at_ms, updated_at_ms, owner_steam_id64)
       VALUES (?, 'published', 200, 'Live map', 'live map', '', ?, ?, ?)`,
    ).bind(liveMapId, NOW, NOW, STEAM_ID).run();

    for (let offset = 0; offset < liveKeys.length; offset += 40) {
      const keys = liveKeys.slice(offset, offset + 40);
      await env.DB.batch(keys.map((key, inner) => {
        const revision = offset + inner + 1;
        return env.DB.prepare(
          `INSERT INTO map_versions
             (map_id, revision, status, code, creator_ids_json, tags_json,
              configurations_json, validations_json, player_counts_csv, client_version,
              map_format_version, minimum_revive_version, revive_version, size_bytes, sha256,
              bundle_key, created_at_ms)
           VALUES (?, ?, 'published', 'orphan-page-test', '[]', '[]', '[]', '[]', ',1,',
                   29, 29, '1.1.0', '1.1.0', 1, ?, ?, ?)`,
        ).bind(liveMapId, revision, revision.toString(16).padStart(64, "0"), key, NOW);
      }));
      await Promise.all(keys.map((key) => env.MAP_BUCKET.put(key, new Uint8Array([1]))));
    }
    await env.MAP_BUCKET.put(orphanKey, new Uint8Array([1]));

    const sweepNow = Date.now() + 2 * 60 * 60 * 1000;
    await cleanupExpiredState(env, sweepNow);
    expect(await env.MAP_BUCKET.head(orphanKey)).not.toBeNull();
    expect(await env.DB.prepare(
      "SELECT value FROM maintenance_state WHERE key = 'approved_object_cursor'",
    ).first()).not.toBeNull();

    await cleanupExpiredState(env, sweepNow + 1);
    expect(await env.MAP_BUCKET.head(orphanKey)).toBeNull();
    expect(await env.DB.prepare(
      "SELECT value FROM maintenance_state WHERE key = 'approved_object_cursor'",
    ).first()).toBeNull();
  });
});

describe("anonymous rate-limit keying", () => {
  it("collapses an IPv6 allocation to one bucket instead of 2^64", async () => {
    const config = runtimeConfig(env);
    const key = (address: string) => anonymousRateKey(
      new Request(`${ORIGIN}/v1/catalog`, { headers: { "CF-Connecting-IP": address } }),
      config,
      "catalog",
    );
    // Different addresses inside one /64 must share a bucket.
    expect(await key("2001:db8:1234:5678::1")).toBe(await key("2001:db8:1234:5678:aaaa:bbbb:cccc:dddd"));
    // A different /64 must not.
    expect(await key("2001:db8:1234:5678::1")).not.toBe(await key("2001:db8:1234:9999::1"));
    // IPv4 keying is unchanged.
    expect(await key("203.0.113.7")).not.toBe(await key("203.0.113.8"));
  });
});

describe("publisher-declared timestamps", () => {
  it("clamps a future export time so it cannot pin a map above the catalogue", async () => {
    await expect(buildLocalFixture({ exportedAtMs: NOW + 12 * 60 * 60 * 1000, nowMs: NOW }))
      .rejects.toMatchObject({ code: "manifest_metadata_invalid" });

    // Within the skew tolerance the manifest is accepted, but the stored sort key is clamped to
    // the server clock at publication rather than the publisher's declared future timestamp.
    const fixture = await buildLocalFixture({ exportedAtMs: NOW + 60_000, nowMs: NOW });
    await publish(fixture, NOW);
    const row = await env.DB.prepare("SELECT updated_at_ms FROM maps WHERE id = ?")
      .bind(MAP_ID).first<{ updated_at_ms: number }>();
    expect(row).not.toBeNull();
    expect(row!.updated_at_ms).toBeLessThan(NOW + 60_000);
    expect(row!.updated_at_ms).toBeLessThanOrEqual(NOW + 2);
  });
});

describe("download authorization after moderation", () => {
  it("stops serving a taken-down revision", async () => {
    const fixture = await buildLocalFixture();
    await publish(fixture, NOW);
    const sha = await sha256Hex(fixture.bytes);
    expect(sha).toBe(fixture.sha256);

    await takedownMap(
      new Request(`${ORIGIN}/v1/moderation/maps/${MAP_ID}/takedown`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason: "Reported content" }),
      }),
      env, runtimeConfig(env), testContext, principal, MAP_ID, crypto.randomUUID(), NOW + 100,
    );

    const { downloadObject } = await import("../src/downloads");
    await expect(downloadObject(
      new Request(`${ORIGIN}/v1/maps/${MAP_ID}/versions/1/download`),
      env, testContext, MAP_ID, 1, "bundle", crypto.randomUUID(),
    )).rejects.toMatchObject({ status: 404 });
  });
});
