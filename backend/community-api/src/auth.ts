import { hasOnlyKeys, isSteamId64, type AuthPrincipal } from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { issueAccessToken, opaqueHash, randomToken, verifyAccessToken } from "./crypto";
import { emptyResponse, HttpError, jsonResponse, readBoundedBody, readJsonObject, responseHeaders } from "./http";

const DEVICE_SESSION_LIFETIME_MS = 10 * 60 * 1000;
const LOGIN_SESSION_LIFETIME_MS = 30 * 24 * 60 * 60 * 1000;
const USER_CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

interface DeviceSessionRow {
  id: string;
  steam_id64: string | null;
  status: "pending" | "approved" | "denied" | "consumed";
  expires_at: number;
  last_polled_at: number | null;
}

interface RefreshTokenRow {
  id: string;
  family_id: string;
  status: "active" | "rotated" | "revoked";
  expires_at: number;
  session_id: string;
  steam_id64: string;
  family_revoked_at: number | null;
  session_status: "active" | "revoked";
  user_status: "active" | "suspended" | "deleted";
}

function userCode(): string {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  const characters = [...bytes].map((value) => USER_CODE_ALPHABET[value % USER_CODE_ALPHABET.length]);
  return `${characters.slice(0, 4).join("")}-${characters.slice(4).join("")}`;
}

function tokenResponse(
  accessToken: string,
  accessExpiresAtMs: number,
  refreshToken: string,
  nowMs: number,
  scope: string,
): Record<string, unknown> {
  return {
    tokenType: "Bearer",
    accessToken,
    expiresInSeconds: Math.max(0, Math.floor((accessExpiresAtMs - nowMs) / 1000)),
    refreshToken,
    refreshExpiresInSeconds: Math.floor(LOGIN_SESSION_LIFETIME_MS / 1000),
    scope,
  };
}

export async function createDeviceSession(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const body = await readJsonObject(request, 1024);
  if (!hasOnlyKeys(body, []) || Object.keys(body).length !== 0) {
    throw new HttpError(400, "invalid_request", "The request body must be an empty object.");
  }
  const id = crypto.randomUUID();
  const deviceSecret = randomToken();
  const code = userCode();
  const expiresAt = nowMs + DEVICE_SESSION_LIFETIME_MS;
  await env.DB.prepare(
    `INSERT INTO device_auth_sessions
       (id, device_secret_hash, user_code_hash, status, created_at, expires_at)
     VALUES (?, ?, ?, 'pending', ?, ?)`,
  ).bind(
    id,
    await opaqueHash(config, "device-secret", deviceSecret),
    await opaqueHash(config, "user-code", code),
    nowMs,
    expiresAt,
  ).run();

  return jsonResponse({
    deviceSessionId: id,
    deviceSecret,
    userCode: code,
    activationPath: `/activate?user_code=${encodeURIComponent(code)}`,
    expiresAt: new Date(expiresAt).toISOString(),
    pollIntervalSeconds: 5,
  }, 201, requestId);
}

export async function pollDeviceSession(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const body = await readJsonObject(request, 4096);
  if (!hasOnlyKeys(body, ["deviceSessionId", "deviceSecret"]) ||
      typeof body.deviceSessionId !== "string" || typeof body.deviceSecret !== "string" ||
      !/^[0-9a-f-]{36}$/.test(body.deviceSessionId) || body.deviceSecret.length !== 43) {
    throw new HttpError(400, "invalid_request", "The device-session credentials are malformed.");
  }
  const secretHash = await opaqueHash(config, "device-secret", body.deviceSecret);
  const row = await env.DB.prepare(
    `SELECT id, steam_id64, status, expires_at, last_polled_at
       FROM device_auth_sessions WHERE id = ? AND device_secret_hash = ?`,
  ).bind(body.deviceSessionId, secretHash).first<DeviceSessionRow>();
  if (row === null) throw new HttpError(400, "invalid_request", "The device-session credentials are invalid.");
  if (row.expires_at <= nowMs) throw new HttpError(410, "device_session_expired", "The device session expired.");
  if (row.status === "denied") throw new HttpError(403, "device_session_denied", "The device session was denied.");
  if (row.status === "consumed") throw new HttpError(409, "device_session_consumed", "The device session was already consumed.");
  if (row.status === "pending") {
    const allowed = await env.DB.prepare(
      `UPDATE device_auth_sessions SET last_polled_at = ?
        WHERE id = ? AND status = 'pending'
          AND (last_polled_at IS NULL OR last_polled_at <= ?)
        RETURNING id`,
    ).bind(nowMs, row.id, nowMs - 5000).first<{ id: string }>();
    if (allowed === null) {
      const retryAfter = Math.max(1, Math.ceil(((row.last_polled_at ?? nowMs) + 5000 - nowMs) / 1000));
      throw new HttpError(429, "authorization_slow_down", "Wait before polling this device session again.", {
        "Retry-After": String(retryAfter),
      });
    }
    return jsonResponse(
      { status: "pending", expiresAt: new Date(row.expires_at).toISOString() },
      202,
      requestId,
      { "Retry-After": "5" },
    );
  }

  if (!isSteamId64(row.steam_id64)) {
    throw new HttpError(409, "device_session_consumed", "The device session is no longer available.");
  }

  const sessionId = crypto.randomUUID();
  const familyId = crypto.randomUUID();
  const refreshId = crypto.randomUUID();
  const refreshToken = randomToken();
  const refreshHash = await opaqueHash(config, "refresh-token", refreshToken);
  const sessionExpiresAt = nowMs + LOGIN_SESSION_LIFETIME_MS;
  try {
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO users (steam_id64, status, created_at, last_login_at)
         VALUES (?, 'active', ?, ?)
         ON CONFLICT(steam_id64) DO UPDATE SET last_login_at = excluded.last_login_at`,
      ).bind(row.steam_id64, nowMs, nowMs),
      env.DB.prepare(
        `INSERT INTO auth_sessions
           (id, steam_id64, status, created_at, expires_at, source_device_session_id)
         VALUES (?, ?, 'active', ?, ?, ?)`,
      ).bind(sessionId, row.steam_id64, nowMs, sessionExpiresAt, row.id),
      env.DB.prepare(
        `INSERT INTO refresh_token_families (id, session_id, steam_id64, created_at, expires_at)
         VALUES (?, ?, ?, ?, ?)`,
      ).bind(familyId, sessionId, row.steam_id64, nowMs, sessionExpiresAt),
      env.DB.prepare(
        `INSERT INTO refresh_tokens (id, family_id, token_hash, status, created_at, expires_at)
         VALUES (?, ?, ?, 'active', ?, ?)`,
      ).bind(refreshId, familyId, refreshHash, nowMs, sessionExpiresAt),
      env.DB.prepare(
        `UPDATE device_auth_sessions SET status = 'consumed', consumed_at = ?
          WHERE id = ? AND device_secret_hash = ? AND status = 'approved' AND expires_at > ?`,
      ).bind(nowMs, row.id, secretHash, nowMs),
    ]);
  } catch {
    throw new HttpError(409, "device_session_consumed", "The device session is no longer available.");
  }
  const access = await issueAccessToken(config, row.steam_id64, sessionId, nowMs);
  return jsonResponse(tokenResponse(access.token, access.expiresAtMs, refreshToken, nowMs, access.scope), 200, requestId);
}

async function refreshRow(env: Env, config: RuntimeConfig, refreshToken: string): Promise<RefreshTokenRow | null> {
  const hash = await opaqueHash(config, "refresh-token", refreshToken);
  return env.DB.prepare(
    `SELECT t.id, t.family_id, t.status, t.expires_at,
            f.session_id, f.steam_id64, f.revoked_at AS family_revoked_at,
            s.status AS session_status, u.status AS user_status
       FROM refresh_tokens t
       JOIN refresh_token_families f ON f.id = t.family_id
       JOIN auth_sessions s ON s.id = f.session_id
       JOIN users u ON u.steam_id64 = f.steam_id64
      WHERE t.token_hash = ?`,
  ).bind(hash).first<RefreshTokenRow>();
}

async function revokeFamily(env: Env, row: RefreshTokenRow, nowMs: number, reason: string): Promise<void> {
  await env.DB.batch([
    env.DB.prepare(
      `UPDATE refresh_token_families SET revoked_at = COALESCE(revoked_at, ?), revoke_reason = ? WHERE id = ?`,
    ).bind(nowMs, reason, row.family_id),
    env.DB.prepare(`UPDATE refresh_tokens SET status = 'revoked' WHERE family_id = ?`).bind(row.family_id),
    env.DB.prepare(
      `UPDATE auth_sessions SET status = 'revoked', revoked_at = COALESCE(revoked_at, ?) WHERE id = ?`,
    ).bind(nowMs, row.session_id),
  ]);
}

export async function refreshSession(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const body = await readJsonObject(request, 4096);
  if (!hasOnlyKeys(body, ["refreshToken"]) || typeof body.refreshToken !== "string" || body.refreshToken.length !== 43) {
    throw new HttpError(400, "invalid_request", "The refresh token is malformed.");
  }
  const row = await refreshRow(env, config, body.refreshToken);
  if (row === null) throw new HttpError(401, "token_invalid", "The refresh token is invalid.");
  if (row.status !== "active") {
    await revokeFamily(env, row, nowMs, "refresh_token_reuse");
    throw new HttpError(401, "token_invalid", "The refresh-token family was revoked.");
  }
  if (row.expires_at <= nowMs || row.family_revoked_at !== null || row.session_status !== "active") {
    throw new HttpError(401, "token_invalid", "The refresh token is expired or revoked.");
  }
  if (row.user_status !== "active") throw new HttpError(403, "account_suspended", "The account is not active.");

  const nextId = crypto.randomUUID();
  const nextToken = randomToken();
  const nextHash = await opaqueHash(config, "refresh-token", nextToken);
  let rotationResults: D1Result<unknown>[];
  try {
    rotationResults = await env.DB.batch([
      env.DB.prepare(
        `UPDATE refresh_tokens SET status = 'rotated', used_at = ?, rotated_to_id = ?
          WHERE id = ? AND status = 'active'`,
      ).bind(nowMs, nextId, row.id),
      env.DB.prepare(
        `INSERT INTO refresh_tokens (id, family_id, token_hash, status, created_at, expires_at)
         VALUES (?, ?, ?, 'active', ?, ?)`,
      ).bind(nextId, row.family_id, nextHash, nowMs, row.expires_at),
    ]);
  } catch {
    const current = await refreshRow(env, config, body.refreshToken);
    if (current !== null && current.status !== "active") {
      await revokeFamily(env, current, nowMs, "concurrent_refresh_reuse");
      throw new HttpError(401, "token_invalid", "The refresh-token family was revoked.");
    }
    throw new HttpError(503, "temporarily_unavailable", "The session could not be refreshed.", { "Retry-After": "5" });
  }
  if ((rotationResults[0]?.meta.changes ?? 0) !== 1) {
    await revokeFamily(env, row, nowMs, "concurrent_refresh_reuse");
    throw new HttpError(401, "token_invalid", "The refresh-token family was revoked.");
  }
  const access = await issueAccessToken(config, row.steam_id64, row.session_id, nowMs);
  return jsonResponse(tokenResponse(access.token, access.expiresAtMs, nextToken, nowMs, access.scope), 200, requestId);
}

export async function logoutSession(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const body = await readJsonObject(request, 4096);
  if (!hasOnlyKeys(body, ["refreshToken"]) || typeof body.refreshToken !== "string") {
    throw new HttpError(400, "invalid_request", "The refresh token is malformed.");
  }
  const row = body.refreshToken.length === 43 ? await refreshRow(env, config, body.refreshToken) : null;
  if (row !== null) await revokeFamily(env, row, nowMs, "logout");
  return emptyResponse(204, requestId);
}

export async function authenticate(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requiredScope: "maps:read" | "maps:download" | "maps:upload",
  nowMs: number,
): Promise<AuthPrincipal> {
  const authorization = request.headers.get("Authorization");
  if (authorization === null || !authorization.startsWith("Bearer ")) {
    throw new HttpError(401, "auth_required", "A bearer access token is required.", {
      "WWW-Authenticate": 'Bearer realm="ss2revive-community"',
    });
  }
  const token = authorization.slice(7);
  if (token.length < 32 || token.length > 4096) {
    throw new HttpError(401, "token_invalid", "The access token is invalid.");
  }
  const principal = await verifyAccessToken(config, token, requiredScope, nowMs);
  const row = await env.DB.prepare(
    `SELECT s.status AS session_status, s.expires_at, u.status AS user_status
       FROM auth_sessions s JOIN users u ON u.steam_id64 = s.steam_id64
      WHERE s.id = ? AND s.steam_id64 = ?`,
  ).bind(principal.sessionId, principal.steamId64).first<{
    session_status: "active" | "revoked";
    expires_at: number;
    user_status: "active" | "suspended" | "deleted";
  }>();
  if (row === null || row.session_status !== "active" || row.expires_at <= nowMs) {
    throw new HttpError(401, "token_invalid", "The access session is expired or revoked.");
  }
  if (row.user_status !== "active") throw new HttpError(403, "account_suspended", "The account is not active.");
  return principal;
}

export async function approveLocalDeviceByCode(
  env: Env,
  config: RuntimeConfig,
  code: string,
  nowMs: number,
): Promise<boolean> {
  if (!config.allowMockAuth || !isSteamId64(config.mockSteamId64) || !/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(code)) {
    return false;
  }
  const codeHash = await opaqueHash(config, "user-code", code);
  await env.DB.prepare(
    `INSERT INTO users (steam_id64, status, created_at, last_login_at)
     VALUES (?, 'active', ?, ?)
     ON CONFLICT(steam_id64) DO NOTHING`,
  ).bind(config.mockSteamId64, nowMs, nowMs).run();
  const updated = await env.DB.prepare(
    `UPDATE device_auth_sessions
        SET status = 'approved', steam_id64 = ?, approved_at = ?
      WHERE user_code_hash = ? AND status = 'pending' AND expires_at > ?
      RETURNING id`,
  ).bind(config.mockSteamId64, nowMs, codeHash, nowMs).first<{ id: string }>();
  return updated !== null;
}

function escapeHtml(value: string): string {
  return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");
}

export async function localActivationPage(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  if (!config.allowMockAuth) throw new HttpError(404, "not_found", "The route was not found.");
  let code = new URL(request.url).searchParams.get("user_code")?.toUpperCase() ?? "";
  let message = "This page is available only in local development.";
  if (request.method === "POST") {
    const contentType = request.headers.get("Content-Type")?.split(";", 1)[0]?.trim().toLowerCase();
    if (contentType !== "application/x-www-form-urlencoded") {
      throw new HttpError(415, "invalid_request", "The activation form has an invalid content type.");
    }
    const body = await readBoundedBody(request, 4096);
    code = new URLSearchParams(new TextDecoder().decode(body)).get("user_code")?.toUpperCase() ?? "";
    message = await approveLocalDeviceByCode(env, config, code, nowMs)
      ? `Approved local Steam user ${config.mockSteamId64}. Return to the client.`
      : "The code is invalid, expired, or already used.";
  } else if (request.method !== "GET") {
    throw new HttpError(405, "method_not_allowed", "The method is not allowed.", { Allow: "GET, POST" });
  }
  const html = `<!doctype html><html><head><meta charset="utf-8"><title>SS2Revive local activation</title></head>
<body><main><h1>SS2Revive local activation</h1><p>${escapeHtml(message)}</p>
<form method="post" action="/activate"><label>Code <input name="user_code" value="${escapeHtml(code)}" pattern="[A-Z2-9]{4}-[A-Z2-9]{4}" required></label>
<button type="submit">Approve local test device</button></form></main></body></html>`;
  const headers = responseHeaders(requestId, {
    "Content-Type": "text/html; charset=utf-8",
    "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'",
  });
  return new Response(html, { status: 200, headers });
}
