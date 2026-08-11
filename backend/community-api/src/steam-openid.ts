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
  user_code: string | null;
}

function escapeHtml(value: string): string {
  return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

const PAGE_STYLES = `
:root{color-scheme:dark;font-family:Inter,ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif;background:#0c151c;color:#eaf1f5}
*{box-sizing:border-box}
body{margin:0;min-height:100vh;background:#0c151c;display:grid;place-items:center;padding:32px 18px}
.shell{width:min(100%,680px)}
.brand{display:flex;align-items:baseline;gap:10px;margin:0 0 16px 4px;letter-spacing:.08em;text-transform:uppercase}
.brand strong{font-family:Georgia,"Times New Roman",serif;font-size:24px;letter-spacing:.04em;color:#fff}
.brand span{font-size:12px;font-weight:750;color:#8ea6b5}
.card{background:#14222c;border:1px solid #31444f;border-top:4px solid #e7a84c;box-shadow:0 18px 55px rgba(0,0,0,.28);padding:clamp(26px,6vw,48px)}
.eyebrow{margin:0 0 8px;color:#e7a84c;font-size:12px;font-weight:800;letter-spacing:.14em;text-transform:uppercase}
h1{font-family:Georgia,"Times New Roman",serif;font-size:clamp(31px,7vw,46px);line-height:1.04;letter-spacing:-.02em;margin:0 0 18px;color:#fff}
p{font-size:16px;line-height:1.65;margin:0 0 18px;color:#c6d3da}
.lede{font-size:18px;color:#e4ebef}
.notice{border-left:3px solid #e7a84c;background:#101c24;padding:14px 16px;margin:24px 0;color:#dbe5ea}
.notice strong{color:#fff}
form{margin-top:26px}
label{display:block;margin-bottom:9px;font-size:14px;font-weight:750;color:#f3f6f8}
.hint{display:block;margin:8px 0 0;font-size:13px;line-height:1.5;color:#93a8b5}
input[type=text]{width:100%;height:60px;border:1px solid #5f7582;background:#0a1218;color:#fff;border-radius:2px;padding:0 16px;font:700 24px/1 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:.18em;text-transform:uppercase}
input:focus-visible,button:focus-visible,a:focus-visible{outline:3px solid #8fd4e8;outline-offset:3px}
.facts{display:grid;grid-template-columns:minmax(110px,.55fr) 1fr;border-top:1px solid #31444f;margin:24px 0}
.facts dt,.facts dd{margin:0;padding:13px 0;border-bottom:1px solid #31444f}
.facts dt{color:#8ea6b5;font-size:13px;font-weight:750;text-transform:uppercase;letter-spacing:.06em}
.facts dd{color:#fff;font-weight:700;overflow-wrap:anywhere}
.device-code{font:800 22px/1 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:.12em}
.actions{display:flex;align-items:center;gap:18px;flex-wrap:wrap;margin-top:26px}
button,.button{appearance:none;border:0;border-radius:2px;background:#e7a84c;color:#172028;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;min-height:52px;padding:0 22px;font:800 15px/1 ui-sans-serif,system-ui,sans-serif;text-decoration:none}
button:hover,.button:hover{background:#f3ba68}
.text-link{color:#a8d8e6;font-weight:700;text-underline-offset:3px}
.site{margin:15px 4px 0;color:#708895;font-size:12px;letter-spacing:.04em}
.request{font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace;color:#78909d;overflow-wrap:anywhere}
@media(max-width:520px){body{padding:0}.shell{width:100%}.brand{margin:18px}.card{border-left:0;border-right:0;padding:28px 20px}.facts{grid-template-columns:1fr}.facts dt{border-bottom:0;padding-bottom:3px}.facts dd{padding-top:3px}.actions button,.actions .button{width:100%}}
`;

function htmlResponse(
  title: string,
  content: string,
  status: number,
  requestId: string,
  extraHeaders?: HeadersInit,
): Response {
  const nonce = randomToken();
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark">
<title>${escapeHtml(title)}</title><style nonce="${nonce}">${PAGE_STYLES}</style></head>
<body><main class="shell"><header class="brand"><strong>SS2Revive</strong><span>Community maps</span></header>
<section class="card">${content}</section><p class="site">community.m12labs.net</p></main></body></html>`;
  const headers = responseHeaders(requestId, extraHeaders);
  headers.set("Content-Type", "text/html; charset=utf-8");
  headers.set(
    "Content-Security-Policy",
    `default-src 'none'; style-src 'nonce-${nonce}'; form-action 'self' https://steamcommunity.com; frame-ancestors 'none'; base-uri 'none'`,
  );
  return new Response(html, { status, headers });
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
  const suppliedCodes = new URL(request.url).searchParams.getAll("user_code");
  const suppliedCode = suppliedCodes.length === 1 ? suppliedCodes[0]!.toUpperCase() : "";
  const code = /^[A-Z2-9]{4}-[A-Z2-9]{4}$/u.test(suppliedCode) ? suppliedCode : "";
  return htmlResponse("Connect SS2Revive to Steam", `
<p class="eyebrow">Steam account link</p><h1>Connect SS2Revive</h1>
<p class="lede">Confirm the code from the game, then continue to Steam in the next step.</p>
<div class="notice"><strong>Only continue if SS2Revive opened this page for you.</strong>
Never approve a code sent by another person. SS2Revive does not receive or store your Steam password.</div>
<form method="post" action="/v1/auth/steam/start">
<label for="user-code">Device code</label>
<input id="user-code" name="user_code" value="${escapeHtml(code)}" maxlength="9"
 pattern="[A-Za-z2-9]{4}-[A-Za-z2-9]{4}" autocomplete="one-time-code" autocapitalize="characters"
 spellcheck="false" aria-describedby="code-hint" required autofocus>
<span class="hint" id="code-hint">The same nine-character code must be visible inside SS2Revive.</span>
<div class="actions"><button type="submit">Continue to Steam</button></div></form>`, 200, requestId);
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
       (id, device_session_id, state_hash, status, return_to, user_code, created_at, expires_at)
     VALUES (?, ?, ?, 'pending', ?, ?, ?, ?)
     ON CONFLICT(device_session_id) DO UPDATE SET
       id = excluded.id, state_hash = excluded.state_hash, status = 'pending',
       return_to = excluded.return_to, user_code = excluded.user_code, steam_id64 = NULL,
       response_nonce_hash = NULL,
       confirm_token_hash = NULL, created_at = excluded.created_at,
       expires_at = excluded.expires_at, verified_at = NULL, confirmed_at = NULL
     WHERE steam_openid_sessions.status IN ('pending', 'failed')
        OR steam_openid_sessions.expires_at <= ?`,
  ).bind(id, device.id, stateHash, returnTo, code, nowMs, nowMs + LOGIN_LIFETIME_MS, nowMs).run();
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
    `SELECT id, device_session_id, status, return_to, steam_id64, expires_at, user_code
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
  return htmlResponse("Confirm SS2Revive link", `
<p class="eyebrow">Steam verified</p><h1>Confirm this device</h1>
<p class="lede">Steam accepted the account. Check the details against the game before approving it.</p>
<dl class="facts"><dt>Steam ID</dt><dd>${escapeHtml(verified.steamId64)}</dd>
<dt>Device code</dt><dd class="device-code">${escapeHtml(login.user_code ?? "unknown")}</dd></dl>
<div class="notice"><strong>The code must exactly match the one visible in SS2Revive.</strong>
If it differs&mdash;or you did not start this login&mdash;close this page without approving.</div>
<form method="post" action="/v1/auth/steam/confirm">
<input type="hidden" name="state" value="${escapeHtml(state)}">
<input type="hidden" name="confirm_token" value="${escapeHtml(confirmToken)}">
<div class="actions"><button type="submit">Approve this device</button>
<a class="text-link" href="/activate">Cancel and start over</a></div></form>`, 200, requestId);
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
  return htmlResponse("SS2Revive linked", `
<p class="eyebrow">Device approved</p><h1>You're connected</h1>
<p class="lede">Return to SS2Revive. The game will finish signing in automatically.</p>
<div class="notice">You can close this browser tab. Manage or end this session with the
<strong>Log out</strong> button in Creation Mode.</div>`, 200, requestId);
}

export function steamAuthErrorPage(error: HttpError, requestId: string): Response {
  return htmlResponse("SS2Revive sign-in problem", `
<p class="eyebrow">Sign-in stopped</p><h1>We couldn't continue</h1>
<p class="lede">${escapeHtml(error.message)}</p>
<div class="notice">Return to SS2Revive and start the sign-in again. Device codes expire and can
only be approved once.</div>
<div class="actions"><a class="button" href="/activate">Try another code</a></div>
<p class="request">Request ${escapeHtml(requestId)}</p>`, error.status, requestId, error.headers);
}
