import { isSteamId64 } from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { opaqueHash, randomToken } from "./crypto";
import { HttpError, readBoundedBody, responseHeaders } from "./http";

const STEAM_OPENID_ENDPOINT = "https://steamcommunity.com/openid/login";
const OPENID_NAMESPACE = "http://specs.openid.net/auth/2.0";
const IDENTIFIER_SELECT = "http://specs.openid.net/auth/2.0/identifier_select";
const LOGIN_LIFETIME_MS = 10 * 60 * 1000;

interface OpenIdSessionRow {
  id: string;
  device_session_id: string;
  status: "pending" | "verified" | "confirmed" | "failed";
  return_to: string;
  steam_id64: string | null;
  expires_at: number;
}

function escapeHtml(value: string): string {
  return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

function htmlResponse(html: string, status: number, requestId: string): Response {
  return new Response(html, {
    status,
    headers: responseHeaders(requestId, {
      "Content-Type": "text/html; charset=utf-8",
      "Content-Security-Policy": "default-src 'none'; form-action 'self' https://steamcommunity.com; frame-ancestors 'none'; base-uri 'none'",
    }),
  });
}

function exactParameter(parameters: URLSearchParams, name: string, maximum = 2048): string {
  const values = parameters.getAll(name);
  if (values.length !== 1 || values[0] === undefined || values[0].length < 1 || values[0].length > maximum) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }
  return values[0];
}

async function boundedText(response: Response, maximumBytes: number): Promise<string> {
  if (response.body === null) return "";
  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8", { fatal: true });
  let total = 0;
  let text = "";
  try {
    for (;;) {
      const result = await reader.read();
      if (result.done) break;
      total += result.value.byteLength;
      if (total > maximumBytes) {
        await reader.cancel("response limit exceeded");
        throw new HttpError(502, "steam_unavailable", "Steam authentication is temporarily unavailable.");
      }
      text += decoder.decode(result.value, { stream: true });
    }
    text += decoder.decode();
    return text;
  } catch (error) {
    if (error instanceof HttpError) throw error;
    throw new HttpError(502, "steam_unavailable", "Steam authentication is temporarily unavailable.");
  } finally {
    reader.releaseLock();
  }
}

export async function verifySteamOpenIdAssertion(
  parameters: URLSearchParams,
  expectedReturnTo: string,
  nowMs: number,
  fetcher: typeof fetch = fetch,
): Promise<{ steamId64: string; responseNonce: string }> {
  if ([...parameters.keys()].length > 24) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }
  const mode = exactParameter(parameters, "openid.mode", 64);
  const namespace = exactParameter(parameters, "openid.ns", 128);
  const endpoint = exactParameter(parameters, "openid.op_endpoint", 256);
  const claimedId = exactParameter(parameters, "openid.claimed_id", 256);
  const identity = exactParameter(parameters, "openid.identity", 256);
  const returnTo = exactParameter(parameters, "openid.return_to", 512);
  const responseNonce = exactParameter(parameters, "openid.response_nonce", 255);
  const signedText = exactParameter(parameters, "openid.signed", 512);
  exactParameter(parameters, "openid.assoc_handle", 255);
  exactParameter(parameters, "openid.sig", 1024);
  if (
    mode !== "id_res" || namespace !== OPENID_NAMESPACE || endpoint !== STEAM_OPENID_ENDPOINT ||
    claimedId !== identity || returnTo !== expectedReturnTo
  ) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }
  const match = /^https:\/\/steamcommunity\.com\/openid\/id\/([1-9][0-9]{16,19})$/u.exec(claimedId);
  const steamId64 = match?.[1] ?? "";
  if (!isSteamId64(steamId64)) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }
  const signed = new Set(signedText.split(","));
  for (const required of ["op_endpoint", "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle"]) {
    if (!signed.has(required)) {
      throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
    }
  }
  const nonceTimeText = responseNonce.slice(0, 20);
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/u.test(nonceTimeText)) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }
  const nonceTime = Date.parse(nonceTimeText);
  if (!Number.isFinite(nonceTime) || nonceTime < nowMs - LOGIN_LIFETIME_MS || nonceTime > nowMs + 2 * 60 * 1000) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }

  const verification = new URLSearchParams();
  for (const [key, value] of parameters) {
    if (key.startsWith("openid.")) verification.append(key, value);
  }
  verification.set("openid.mode", "check_authentication");
  let response: Response;
  try {
    response = await fetcher(STEAM_OPENID_ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded; charset=utf-8" },
      body: verification.toString(),
      redirect: "manual",
      signal: AbortSignal.timeout(5000),
    });
  } catch {
    throw new HttpError(502, "steam_unavailable", "Steam authentication is temporarily unavailable.", {
      "Retry-After": "30",
    });
  }
  if (response.status !== 200) {
    throw new HttpError(502, "steam_unavailable", "Steam authentication is temporarily unavailable.", {
      "Retry-After": "30",
    });
  }
  const responseText = await boundedText(response, 4096);
  const fields = new Map<string, string>();
  for (const line of responseText.split(/\r?\n/u)) {
    if (line === "") continue;
    const separator = line.indexOf(":");
    if (separator < 1) throw new HttpError(502, "steam_unavailable", "Steam returned an invalid response.");
    const key = line.slice(0, separator);
    if (fields.has(key)) throw new HttpError(502, "steam_unavailable", "Steam returned an invalid response.");
    fields.set(key, line.slice(separator + 1));
  }
  if (fields.get("is_valid") !== "true" || (fields.has("ns") && fields.get("ns") !== OPENID_NAMESPACE)) {
    throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  }
  return { steamId64, responseNonce };
}

export function steamActivationPage(request: Request, requestId: string): Response {
  if (request.method !== "GET") {
    throw new HttpError(405, "method_not_allowed", "The method is not allowed.", { Allow: "GET" });
  }
  const code = new URL(request.url).searchParams.get("user_code")?.toUpperCase() ?? "";
  const value = /^[A-Z2-9]{4}-[A-Z2-9]{4}$/u.test(code) ? code : "";
  return htmlResponse(`<!doctype html><html><head><meta charset="utf-8"><title>Link SS2Revive to Steam</title></head>
<body><main><h1>Link SS2Revive to Steam</h1><p>Enter the code shown by SS2Revive, then sign in on Steam.</p>
<p>SS2Revive never receives your Steam password.</p>
<form method="post" action="/v1/auth/steam/start"><label>Device code
<input name="user_code" value="${escapeHtml(value)}" pattern="[A-Z2-9]{4}-[A-Z2-9]{4}" autocomplete="one-time-code" required></label>
<button type="submit">Continue to Steam</button></form></main></body></html>`, 200, requestId);
}

export async function startSteamLogin(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const contentType = request.headers.get("Content-Type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (contentType !== "application/x-www-form-urlencoded") {
    throw new HttpError(415, "invalid_request", "The Steam login form has an invalid content type.");
  }
  const form = new URLSearchParams(new TextDecoder().decode(await readBoundedBody(request, 4096)));
  const values = form.getAll("user_code");
  const code = values.length === 1 ? values[0]!.toUpperCase() : "";
  if (!/^[A-Z2-9]{4}-[A-Z2-9]{4}$/u.test(code)) {
    throw new HttpError(400, "invalid_request", "The device code is invalid or expired.");
  }
  const codeHash = await opaqueHash(config, "user-code", code);
  const device = await env.DB.prepare(
    `SELECT id FROM device_auth_sessions
      WHERE user_code_hash = ? AND status = 'pending' AND expires_at > ?`,
  ).bind(codeHash, nowMs).first<{ id: string }>();
  if (device === null) throw new HttpError(400, "invalid_request", "The device code is invalid or expired.");

  const state = randomToken();
  const stateHash = await opaqueHash(config, "steam-openid-state", state);
  const returnTo = `${config.publicOrigin}/v1/auth/steam/callback?state=${encodeURIComponent(state)}`;
  const id = crypto.randomUUID();
  const result = await env.DB.prepare(
    `INSERT INTO steam_openid_sessions
       (id, device_session_id, state_hash, status, return_to, created_at, expires_at)
     VALUES (?, ?, ?, 'pending', ?, ?, ?)
     ON CONFLICT(device_session_id) DO UPDATE SET
       id = excluded.id, state_hash = excluded.state_hash, status = 'pending',
       return_to = excluded.return_to, steam_id64 = NULL, response_nonce_hash = NULL,
       confirm_token_hash = NULL, created_at = excluded.created_at,
       expires_at = excluded.expires_at, verified_at = NULL, confirmed_at = NULL
     WHERE steam_openid_sessions.status IN ('pending', 'failed')
        OR steam_openid_sessions.expires_at <= ?`,
  ).bind(id, device.id, stateHash, returnTo, nowMs, nowMs + LOGIN_LIFETIME_MS, nowMs).run();
  if ((result.meta.changes ?? 0) !== 1) {
    throw new HttpError(409, "steam_login_in_progress", "This device already has a verified Steam login to confirm.");
  }

  const steamUrl = new URL(STEAM_OPENID_ENDPOINT);
  steamUrl.searchParams.set("openid.ns", OPENID_NAMESPACE);
  steamUrl.searchParams.set("openid.mode", "checkid_setup");
  steamUrl.searchParams.set("openid.return_to", returnTo);
  steamUrl.searchParams.set("openid.realm", `${config.publicOrigin}/`);
  steamUrl.searchParams.set("openid.identity", IDENTIFIER_SELECT);
  steamUrl.searchParams.set("openid.claimed_id", IDENTIFIER_SELECT);
  return new Response(null, {
    status: 303,
    headers: responseHeaders(requestId, { Location: steamUrl.toString() }),
  });
}

export async function steamCallback(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
  fetcher: typeof fetch = fetch,
): Promise<Response> {
  const parameters = new URL(request.url).searchParams;
  const state = exactParameter(parameters, "state", 64);
  if (state.length !== 43) throw new HttpError(400, "steam_assertion_invalid", "Steam authentication could not be verified.");
  const stateHash = await opaqueHash(config, "steam-openid-state", state);
  const login = await env.DB.prepare(
    `SELECT id, device_session_id, status, return_to, steam_id64, expires_at
       FROM steam_openid_sessions WHERE state_hash = ?`,
  ).bind(stateHash).first<OpenIdSessionRow>();
  if (login === null || login.status !== "pending" || login.expires_at <= nowMs) {
    throw new HttpError(400, "steam_assertion_invalid", "The Steam login is invalid, expired, or already used.");
  }
  const verified = await verifySteamOpenIdAssertion(parameters, login.return_to, nowMs, fetcher);
  const nonceHash = await opaqueHash(config, "steam-openid-nonce", verified.responseNonce);
  const confirmToken = randomToken();
  const confirmHash = await opaqueHash(config, "steam-confirm-token", confirmToken);
  let updated: { id: string } | null;
  try {
    updated = await env.DB.prepare(
      `UPDATE steam_openid_sessions
          SET status = 'verified', steam_id64 = ?, response_nonce_hash = ?,
              confirm_token_hash = ?, verified_at = ?
        WHERE id = ? AND status = 'pending' AND expires_at > ?
        RETURNING id`,
    ).bind(verified.steamId64, nonceHash, confirmHash, nowMs, login.id, nowMs).first<{ id: string }>();
  } catch {
    throw new HttpError(400, "steam_assertion_invalid", "The Steam assertion was already used.");
  }
  if (updated === null) throw new HttpError(409, "steam_login_consumed", "The Steam login was already consumed.");
  return htmlResponse(`<!doctype html><html><head><meta charset="utf-8"><title>Confirm SS2Revive link</title></head>
<body><main><h1>Confirm device link</h1><p>Steam account ${escapeHtml(verified.steamId64)} was verified.</p>
<p>Approve linking this Steam account to the SS2Revive device code you entered?</p>
<form method="post" action="/v1/auth/steam/confirm">
<input type="hidden" name="state" value="${escapeHtml(state)}">
<input type="hidden" name="confirm_token" value="${escapeHtml(confirmToken)}">
<button type="submit">Approve this device</button></form></main></body></html>`, 200, requestId);
}

export async function confirmSteamLogin(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  requestId: string,
  nowMs: number,
): Promise<Response> {
  const contentType = request.headers.get("Content-Type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (contentType !== "application/x-www-form-urlencoded") {
    throw new HttpError(415, "invalid_request", "The confirmation form has an invalid content type.");
  }
  const form = new URLSearchParams(new TextDecoder().decode(await readBoundedBody(request, 4096)));
  const states = form.getAll("state");
  const tokens = form.getAll("confirm_token");
  const state = states.length === 1 ? states[0]! : "";
  const token = tokens.length === 1 ? tokens[0]! : "";
  if (state.length !== 43 || token.length !== 43) {
    throw new HttpError(400, "invalid_request", "The confirmation is invalid or expired.");
  }
  const login = await env.DB.prepare(
    `SELECT id, device_session_id, status, return_to, steam_id64, expires_at
       FROM steam_openid_sessions
      WHERE state_hash = ? AND confirm_token_hash = ?`,
  ).bind(
    await opaqueHash(config, "steam-openid-state", state),
    await opaqueHash(config, "steam-confirm-token", token),
  ).first<OpenIdSessionRow>();
  if (login === null || login.status !== "verified" || login.expires_at <= nowMs || !isSteamId64(login.steam_id64)) {
    throw new HttpError(400, "invalid_request", "The confirmation is invalid or expired.");
  }
  const results = await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO users (steam_id64, status, created_at, last_login_at)
       VALUES (?, 'active', ?, ?)
       ON CONFLICT(steam_id64) DO UPDATE SET last_login_at = excluded.last_login_at`,
    ).bind(login.steam_id64, nowMs, nowMs),
    env.DB.prepare(
      `UPDATE device_auth_sessions SET status = 'approved', steam_id64 = ?, approved_at = ?
        WHERE id = ? AND status = 'pending' AND expires_at > ?`,
    ).bind(login.steam_id64, nowMs, login.device_session_id, nowMs),
    env.DB.prepare(
      `UPDATE steam_openid_sessions SET status = 'confirmed', confirmed_at = ?, confirm_token_hash = NULL
        WHERE id = ? AND status = 'verified'
          AND EXISTS (SELECT 1 FROM device_auth_sessions d
                       WHERE d.id = steam_openid_sessions.device_session_id
                         AND d.status = 'approved' AND d.steam_id64 = ?)
        RETURNING id`,
    ).bind(nowMs, login.id, login.steam_id64),
  ]);
  const confirmed = results[2]?.results as Array<{ id?: unknown }> | undefined;
  if (confirmed?.[0]?.id !== login.id) {
    throw new HttpError(409, "steam_login_consumed", "The Steam login could not be confirmed.");
  }
  return htmlResponse(`<!doctype html><html><head><meta charset="utf-8"><title>SS2Revive linked</title></head>
<body><main><h1>Device approved</h1><p>Return to SS2Revive to finish signing in.</p></main></body></html>`, 200, requestId);
}
