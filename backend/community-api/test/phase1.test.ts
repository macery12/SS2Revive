import { env } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";
import { createDeviceSession, pollDeviceSession } from "../src/auth";
import { runtimeConfig, type RuntimeConfig } from "../src/config";
import { completeDownload, reserveDownload } from "../src/download-quota";
import { HttpError } from "../src/http";
import { cleanupExpiredState } from "../src/maintenance";
import { enforceRateLimit } from "../src/rate-limit";
import {
  confirmSteamLogin,
  startSteamLogin,
  steamCallback,
  verifySteamOpenIdAssertion,
} from "../src/steam-openid";

const LOCAL_ORIGIN = "http://127.0.0.1:8787";
const PUBLIC_ORIGIN = "https://community.m12labs.net";

function productionConfig(): RuntimeConfig {
  return runtimeConfig({
    ...env,
    ENVIRONMENT: "production",
    ALLOW_MOCK_AUTH: "false",
    PUBLIC_ORIGIN,
  });
}

async function json<T>(response: Response): Promise<T> {
  return response.json() as Promise<T>;
}

async function clearDatabase(): Promise<void> {
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
}

function steamAssertion(returnTo: string, state: string, nowMs: number): URLSearchParams {
  const steamId64 = "76561198145479980";
  const parameters = new URLSearchParams({
    state,
    "openid.ns": "http://specs.openid.net/auth/2.0",
    "openid.mode": "id_res",
    "openid.op_endpoint": "https://steamcommunity.com/openid/login",
    "openid.claimed_id": `https://steamcommunity.com/openid/id/${steamId64}`,
    "openid.identity": `https://steamcommunity.com/openid/id/${steamId64}`,
    "openid.return_to": returnTo,
    "openid.response_nonce": `${new Date(nowMs).toISOString().replace(/\.\d{3}Z$/u, "Z")}phase1-test`,
    "openid.assoc_handle": "1234567890",
    "openid.signed": "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
    "openid.sig": "test-signature",
  });
  return parameters;
}

const validSteamFetcher = (async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  expect(String(input)).toBe("https://steamcommunity.com/openid/login");
  expect(init?.method).toBe("POST");
  expect(String(init?.body)).toContain("openid.mode=check_authentication");
  return new Response("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n", { status: 200 });
}) as typeof fetch;

beforeEach(clearDatabase);

describe("Phase 1 production authentication", () => {
  it("verifies a Steam OpenID assertion through direct provider verification", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const returnTo = `${PUBLIC_ORIGIN}/v1/auth/steam/callback?state=test`;
    const result = await verifySteamOpenIdAssertion(
      steamAssertion(returnTo, "test", nowMs), returnTo, nowMs, validSteamFetcher,
    );
    expect(result.steamId64).toBe("76561198145479980");
    await expect(verifySteamOpenIdAssertion(
      steamAssertion(`${PUBLIC_ORIGIN}/wrong`, "test", nowMs), returnTo, nowMs, validSteamFetcher,
    )).rejects.toMatchObject({ code: "steam_assertion_invalid" });
  });

  it("links a pending device only after Steam verification and explicit confirmation", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const config = productionConfig();
    const created = await createDeviceSession(
      new Request(`${PUBLIC_ORIGIN}/v1/auth/device-sessions`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
      }), env, config, crypto.randomUUID(), nowMs,
    );
    const device = await json<{ deviceSessionId: string; deviceSecret: string; userCode: string }>(created);
    const start = await startSteamLogin(
      new Request(`${PUBLIC_ORIGIN}/v1/auth/steam/start`, {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({ user_code: device.userCode }),
      }), env, config, crypto.randomUUID(), nowMs,
    );
    expect(start.status).toBe(303);
    const providerUrl = new URL(start.headers.get("Location")!);
    const returnTo = providerUrl.searchParams.get("openid.return_to")!;
    const state = new URL(returnTo).searchParams.get("state")!;
    const assertion = steamAssertion(returnTo, state, nowMs);
    const callback = await steamCallback(
      new Request(`${PUBLIC_ORIGIN}/v1/auth/steam/callback?${assertion}`),
      env, config, crypto.randomUUID(), nowMs, validSteamFetcher,
    );
    expect(callback.status).toBe(200);
    const callbackHtml = await callback.text();
    const confirmToken = /name="confirm_token" value="([A-Za-z0-9_-]{43})"/u.exec(callbackHtml)?.[1];
    expect(confirmToken).toBeTruthy();
    const confirmation = await confirmSteamLogin(
      new Request(`${PUBLIC_ORIGIN}/v1/auth/steam/confirm`, {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({ state, confirm_token: confirmToken! }),
      }), env, config, crypto.randomUUID(), nowMs,
    );
    expect(confirmation.status).toBe(200);
    const token = await pollDeviceSession(
      new Request(`${PUBLIC_ORIGIN}/v1/auth/device-sessions/token`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceSessionId: device.deviceSessionId, deviceSecret: device.deviceSecret }),
      }), env, config, crypto.randomUUID(), nowMs + 1,
    );
    expect(token.status).toBe(200);
    expect((await json<{ tokenType: string; scope: string }>(token))).toMatchObject({
      tokenType: "Bearer",
      scope: "maps:read maps:download maps:upload maps:manage maps:report maps:moderate",
    });
    await expect(confirmSteamLogin(
      new Request(`${PUBLIC_ORIGIN}/v1/auth/steam/confirm`, {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({ state, confirm_token: confirmToken! }),
      }), env, config, crypto.randomUUID(), nowMs + 2,
    )).rejects.toMatchObject({ code: "invalid_request" });
  });

  it("enforces the server-provided device polling interval", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    const config = runtimeConfig(env);
    const created = await createDeviceSession(
      new Request(`${LOCAL_ORIGIN}/v1/auth/device-sessions`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
      }), env, config, crypto.randomUUID(), nowMs,
    );
    const device = await json<{ deviceSessionId: string; deviceSecret: string }>(created);
    const request = (): Request => new Request(`${LOCAL_ORIGIN}/v1/auth/device-sessions/token`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ deviceSessionId: device.deviceSessionId, deviceSecret: device.deviceSecret }),
    });
    expect((await pollDeviceSession(request(), env, config, crypto.randomUUID(), nowMs)).status).toBe(202);
    await expect(pollDeviceSession(request(), env, config, crypto.randomUUID(), nowMs + 1000))
      .rejects.toMatchObject({ code: "authorization_slow_down", status: 429 });
  });
});

describe("Phase 1 abuse controls", () => {
  it("returns a stable 429 when the edge limiter denies a request", async () => {
    const limiter = { limit: async () => ({ success: false }) } as unknown as RateLimit;
    await expect(enforceRateLimit(limiter, "actor-key"))
      .rejects.toMatchObject({ code: "rate_limited", status: 429 });
  });

  it("enforces exact per-user concurrency and daily byte reservations", async () => {
    const steamId64 = "76561198145479980";
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    await env.DB.prepare(
      `INSERT INTO users (steam_id64, status, created_at, last_login_at) VALUES (?, 'active', ?, ?)`,
    ).bind(steamId64, nowMs, nowMs).run();
    const base = runtimeConfig(env);
    const concurrencyConfig = { ...base, downloadDailyBytes: 10_000, downloadConcurrency: 2 };
    const first = await reserveDownload(env, concurrencyConfig, steamId64, 100, nowMs);
    const second = await reserveDownload(env, concurrencyConfig, steamId64, 100, nowMs);
    await expect(reserveDownload(env, concurrencyConfig, steamId64, 100, nowMs))
      .rejects.toMatchObject({ code: "download_concurrency_exceeded" });
    await completeDownload(env, first);
    await completeDownload(env, second);

    const quotaConfig = { ...base, downloadDailyBytes: 1000, downloadConcurrency: 3 };
    const third = await reserveDownload(env, quotaConfig, steamId64, 700, nowMs + 1);
    await completeDownload(env, third);
    await expect(reserveDownload(env, quotaConfig, steamId64, 200, nowMs + 2))
      .rejects.toMatchObject({ code: "quota_exceeded" });
  });

  it("uses typed HTTP errors for unavailable limiter bindings", async () => {
    await expect(enforceRateLimit(undefined, "actor-key"))
      .rejects.toBeInstanceOf(HttpError);
  });

  it("cleans expired download leases and old usage counters", async () => {
    const steamId64 = "76561198145479980";
    const nowMs = Date.UTC(2026, 7, 8, 12, 0, 0);
    await env.DB.prepare(
      `INSERT INTO users (steam_id64, status, created_at, last_login_at) VALUES (?, 'active', ?, ?)`,
    ).bind(steamId64, nowMs, nowMs).run();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO download_usage_daily
           (steam_id64, day_utc, bytes_reserved, download_starts, updated_at)
         VALUES (?, '2026-07-01', 100, 1, ?)`,
      ).bind(steamId64, nowMs),
      env.DB.prepare(
        `INSERT INTO download_leases
           (id, steam_id64, day_utc, bytes_reserved, created_at, expires_at)
         VALUES (?, ?, '2026-08-08', 100, ?, ?)`,
      ).bind(crypto.randomUUID(), steamId64, nowMs - 1000, nowMs - 1),
    ]);

    await cleanupExpiredState(env, nowMs);

    expect(await env.DB.prepare("SELECT id FROM download_leases").first()).toBeNull();
    expect(await env.DB.prepare("SELECT day_utc FROM download_usage_daily").first()).toBeNull();
  });
});
