import { env } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";
import { runtimeConfig } from "../src/config";
import { approveLocalDeviceByCode } from "../src/auth";
import worker from "../src/worker";

const ORIGIN = "http://127.0.0.1:8787";

async function fetchApi(path: string, init?: RequestInit): Promise<Response> {
  return worker.fetch(new Request(`${ORIGIN}${path}`, init), env);
}

async function json<T>(response: Response): Promise<T> {
  return response.json() as Promise<T>;
}

async function accessToken(): Promise<{ accessToken: string; refreshToken: string }> {
  const created = await fetchApi("/v1/auth/device-sessions", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: "{}",
  });
  expect(created.status).toBe(201);
  const session = await json<{ deviceSessionId: string; deviceSecret: string; userCode: string }>(created);
  expect(await approveLocalDeviceByCode(env, runtimeConfig(env), session.userCode, Date.now())).toBe(true);
  const token = await fetchApi("/v1/auth/device-sessions/token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ deviceSessionId: session.deviceSessionId, deviceSecret: session.deviceSecret }),
  });
  expect(token.status).toBe(200);
  return json<{ accessToken: string; refreshToken: string }>(token);
}

async function seed(): Promise<void> {
  const response = await fetchApi("/_local/seed", {
    method: "POST",
    headers: { "X-Local-Setup-Secret": env.LOCAL_AUTH_SECRET },
  });
  expect(response.status).toBe(200);
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM map_uploads"),
    env.DB.prepare("DELETE FROM download_leases"),
    env.DB.prepare("DELETE FROM download_usage_daily"),
    env.DB.prepare("DELETE FROM steam_openid_sessions"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM refresh_token_families"),
    env.DB.prepare("DELETE FROM auth_sessions"),
    env.DB.prepare("DELETE FROM device_auth_sessions"),
    env.DB.prepare("DELETE FROM users"),
    env.DB.prepare("DELETE FROM map_tags"),
    env.DB.prepare("DELETE FROM map_versions"),
    env.DB.prepare("DELETE FROM maps"),
  ]);
});

describe("Phase 0 Worker", () => {
  it("keeps upload reservations behind bearer authentication", async () => {
    const response = await fetchApi("/v1/uploads", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sizeBytes: 100, sha256: "a".repeat(64) }),
    });
    expect(response.status).toBe(401);
    expect(await json(response)).toMatchObject({ error: { code: "auth_required" } });
  });

  it("returns only shallow health and security headers", async () => {
    const response = await fetchApi("/health");
    expect(response.status).toBe(200);
    expect(await json(response)).toEqual({ status: "ok" });
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(response.headers.get("Cache-Control")).toBe("no-store");
    expect(response.headers.get("X-Request-Id")).toMatch(/^[0-9a-f-]{36}$/);
  });

  it("serves published map metadata publicly while keeping account data authenticated", async () => {
    const response = await fetchApi("/v1/maps");
    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({ items: [], nextCursor: null });
    const me = await fetchApi("/v1/me");
    expect(me.status).toBe(401);
    expect(me.headers.get("WWW-Authenticate")).toContain("Bearer");
  });

  it("supports pending, approval, one-time consumption, and /me", async () => {
    const created = await fetchApi("/v1/auth/device-sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    const session = await json<{ deviceSessionId: string; deviceSecret: string; userCode: string }>(created);
    const pollBody = JSON.stringify({ deviceSessionId: session.deviceSessionId, deviceSecret: session.deviceSecret });
    const pending = await fetchApi("/v1/auth/device-sessions/token", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: pollBody,
    });
    expect(pending.status).toBe(202);
    expect(pending.headers.get("Retry-After")).toBe("5");
    expect(await approveLocalDeviceByCode(env, runtimeConfig(env), session.userCode, Date.now())).toBe(true);
    const approved = await fetchApi("/v1/auth/device-sessions/token", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: pollBody,
    });
    expect(approved.status).toBe(200);
    const tokens = await json<{ accessToken: string; refreshToken: string }>(approved);
    const consumed = await fetchApi("/v1/auth/device-sessions/token", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: pollBody,
    });
    expect(consumed.status).toBe(409);
    const me = await fetchApi("/v1/me", { headers: { Authorization: `Bearer ${tokens.accessToken}` } });
    expect(me.status).toBe(200);
    expect((await json<{ steamId64: string }>(me)).steamId64).toBe(env.MOCK_STEAM_ID64);
  });

  it("rotates refresh tokens and revokes the family on replay", async () => {
    const initial = await accessToken();
    const refresh = await fetchApi("/v1/auth/refresh", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: initial.refreshToken }),
    });
    expect(refresh.status).toBe(200);
    const rotated = await json<{ accessToken: string; refreshToken: string }>(refresh);
    expect(rotated.refreshToken).not.toBe(initial.refreshToken);
    const replay = await fetchApi("/v1/auth/refresh", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: initial.refreshToken }),
    });
    expect(replay.status).toBe(401);
    const familyRevoked = await fetchApi("/v1/auth/refresh", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: rotated.refreshToken }),
    });
    expect(familyRevoked.status).toBe(401);
    const oldAccessRevoked = await fetchApi("/v1/me", { headers: { Authorization: `Bearer ${rotated.accessToken}` } });
    expect(oldAccessRevoked.status).toBe(401);
  });

  it("lists, filters, details, and downloads the seeded bundle", async () => {
    await seed();
    const list = await fetchApi("/v1/maps?players=1&tag=TEAM_COOP&query=Phase&limit=1");
    expect(list.status).toBe(200);
    const page = await json<{ items: Array<{ id: string; thumbnail: { url: string }; downloadUrl: string }> }>(list);
    expect(page.items).toHaveLength(1);
    expect(page.items[0]?.id).toBe("1a658233-92c5-4b63-87fc-4740c855730b");
    expect(page.items[0]?.thumbnail.url).toContain("/versions/1/thumbnail");

    const detail = await fetchApi(`/v1/maps/${page.items[0]!.id}`);
    expect(detail.status).toBe(200);
    const catalog = await fetchApi("/v1/catalog");
    expect(catalog.status).toBe(200);
    expect(catalog.headers.get("Cache-Control")).toContain("public");
    const catalogBody = await json<{ schemaVersion: number; maps: Array<{ bundleKey: string }> }>(catalog);
    expect(catalogBody.schemaVersion).toBe(1);
    expect(catalogBody.maps[0]?.bundleKey).toBe(page.items[0]!.downloadUrl.slice(4));
    const downloadPath = page.items[0]!.downloadUrl;
    const full = await fetchApi(downloadPath);
    expect(full.status).toBe(200);
    const bytes = new Uint8Array(await full.arrayBuffer());
    expect(bytes.length).toBe(Number(full.headers.get("Content-Length")));
    expect(new TextDecoder().decode(bytes.slice(0, 16))).toBe("SS2REVIVE LEVEL\n");
    const etag = full.headers.get("ETag")!;

    const head = await fetchApi(downloadPath, { method: "HEAD" });
    expect(head.status).toBe(200);
    expect((await head.arrayBuffer()).byteLength).toBe(0);
    expect(head.headers.get("ETag")).toBe(etag);

    const partial = await fetchApi(downloadPath, { headers: { Range: "bytes=0-15", "If-Range": etag } });
    expect(partial.status).toBe(206);
    expect(partial.headers.get("Content-Range")).toBe(`bytes 0-15/${bytes.length}`);
    expect(new Uint8Array(await partial.arrayBuffer())).toEqual(bytes.slice(0, 16));

    const notModified = await fetchApi(downloadPath, { headers: { "If-None-Match": etag } });
    expect(notModified.status).toBe(304);
    const rangedNotModified = await fetchApi(downloadPath, {
      headers: { Range: "bytes=0-15", "If-None-Match": etag },
    });
    expect(rangedNotModified.status).toBe(304);
    const invalidRange = await fetchApi(downloadPath, { headers: { Range: "bytes=0-1,4-5" } });
    expect(invalidRange.status).toBe(416);
  });

  it("rejects cursor tampering and ambiguous paths", async () => {
    await seed();
    const secondId = "2a658233-92c5-4b63-87fc-4740c855730b";
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO maps (id, status, current_revision, title, title_sort, description, created_at_ms, updated_at_ms)
         SELECT ?, status, current_revision, 'Second room', 'second room', description, created_at_ms, updated_at_ms - 1
           FROM maps WHERE id = ?`,
      ).bind(secondId, "1a658233-92c5-4b63-87fc-4740c855730b"),
      env.DB.prepare(
        `INSERT INTO map_versions
           (map_id, revision, status, code, creator_ids_json, tags_json, configurations_json,
            validations_json, player_counts_csv, client_version, map_format_version,
            minimum_revive_version, revive_version, size_bytes, sha256, bundle_key,
            thumbnail_size_bytes, thumbnail_sha256, thumbnail_key, created_at_ms)
         SELECT ?, revision, status, 'M4JlKsWSY0uH_EdAyFVzCw', creator_ids_json, tags_json,
                 configurations_json, validations_json, player_counts_csv, client_version,
                 map_format_version, minimum_revive_version, revive_version, size_bytes, sha256,
                 replace(bundle_key,
                   '1a658233-92c5-4b63-87fc-4740c855730b',
                   '2a658233-92c5-4b63-87fc-4740c855730b'),
                 NULL, NULL, NULL, created_at_ms
           FROM map_versions WHERE map_id = ? AND revision = 1`,
      ).bind(secondId, "1a658233-92c5-4b63-87fc-4740c855730b"),
    ]);
    const first = await fetchApi("/v1/maps?limit=1");
    const page = await json<{ items: unknown[]; nextCursor: string }>(first);
    expect(page.items).toHaveLength(1);
    expect(page.nextCursor).toBeTruthy();
    const next = await fetchApi(`/v1/maps?limit=1&cursor=${encodeURIComponent(page.nextCursor)}`);
    expect(next.status).toBe(200);
    const tamperedCursor = `${page.nextCursor.slice(0, -1)}${page.nextCursor.endsWith("A") ? "B" : "A"}`;
    const tampered = await fetchApi(`/v1/maps?limit=1&cursor=${encodeURIComponent(tamperedCursor)}`);
    expect(tampered.status).toBe(400);
    const ambiguous = await fetchApi("/v1//maps");
    expect(ambiguous.status).toBe(400);
    const encoded = await fetchApi("/v1/%6daps");
    expect(encoded.status).toBe(400);
  });

  it("refuses D1 object-key metadata that could cross an R2 authorization boundary", async () => {
    await seed();
    await env.DB.prepare(
      "UPDATE map_versions SET bundle_key = 'approved/maps/another-object.ss2level' WHERE map_id = ? AND revision = 1",
    ).bind("1a658233-92c5-4b63-87fc-4740c855730b").run();
    const response = await fetchApi(
      "/v1/maps/1a658233-92c5-4b63-87fc-4740c855730b/versions/1/download",
    );
    expect(response.status).toBe(503);
    expect((await json<{ error: { code: string } }>(response)).error.code).toBe("object_metadata_mismatch");
  });

  it("uses Steam activation and fails closed for local setup routes in production", async () => {
    const productionEnv: Env = {
      ...env,
      ENVIRONMENT: "production",
      ALLOW_MOCK_AUTH: "true",
      PUBLIC_ORIGIN: "https://community.m12labs.net",
    };
    const activation = await worker.fetch(
      new Request("https://community.m12labs.net/activate"), productionEnv,
    );
    expect(activation.status).toBe(200);
    expect(await activation.text()).toContain("Continue to Steam");
    const seeded = await worker.fetch(new Request("https://community.m12labs.net/_local/seed", {
      method: "POST", headers: { "X-Local-Setup-Secret": env.LOCAL_AUTH_SECRET },
    }), productionEnv);
    expect(seeded.status).toBe(404);
  });

  it("never exposes mock routes on a public origin even with local flags", async () => {
    const activation = await worker.fetch(new Request("https://community.m12labs.net/activate"), env);
    expect(activation.status).toBe(421);
    const seeded = await worker.fetch(new Request("https://community.m12labs.net/_local/seed", {
      method: "POST", headers: { "X-Local-Setup-Secret": env.LOCAL_AUTH_SECRET },
    }), env);
    expect(seeded.status).toBe(421);
  });

  it("fails closed when security-sensitive environment values are invalid", async () => {
    const invalidEnv: Env = { ...env, LOCAL_AUTH_SECRET: "short" };
    const response = await worker.fetch(new Request(`${ORIGIN}/health`), invalidEnv);
    expect(response.status).toBe(503);
    expect((await json<{ error: { code: string } }>(response)).error.code).toBe("temporarily_unavailable");
  });
});
