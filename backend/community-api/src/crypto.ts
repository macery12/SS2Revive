import type { AuthPrincipal } from "@ss2revive/community-contracts";
import { isPlainObject, isSteamId64 } from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { HttpError } from "./http";

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

function base64UrlEncode(bytes: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, Math.min(bytes.length, offset + 0x8000)));
  }
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function base64UrlDecode(value: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]+$/.test(value)) return null;
  const padding = "=".repeat((4 - (value.length % 4)) % 4);
  try {
    const binary = atob(value.replaceAll("-", "+").replaceAll("_", "/") + padding);
    const decoded = Uint8Array.from(binary, (character) => character.charCodeAt(0));
    return base64UrlEncode(decoded) === value ? decoded : null;
  } catch {
    return null;
  }
}

async function hmac(secret: string, value: string): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, encoder.encode(value)));
}

function constantTimeEqual(left: Uint8Array, right: Uint8Array): boolean {
  let difference = left.length ^ right.length;
  const maximum = Math.max(left.length, right.length);
  for (let index = 0; index < maximum; index += 1) {
    difference |= (left[index] ?? 0) ^ (right[index] ?? 0);
  }
  return difference === 0;
}

export function randomToken(bytes = 32): string {
  const value = new Uint8Array(bytes);
  crypto.getRandomValues(value);
  return base64UrlEncode(value);
}

export async function opaqueHash(config: RuntimeConfig, context: string, value: string): Promise<string> {
  return base64UrlEncode(await hmac(config.authSecret, `${context}\u0000${value}`));
}

interface AccessClaims {
  iss: string;
  aud: string;
  sub: string;
  sid: string;
  scope: string;
  iat: number;
  exp: number;
}

export async function issueAccessToken(
  config: RuntimeConfig,
  steamId64: string,
  sessionId: string,
  nowMs: number,
): Promise<{ token: string; expiresAtMs: number }> {
  const header = base64UrlEncode(encoder.encode(JSON.stringify({ alg: "HS256", typ: "JWT" })));
  const expiresAtMs = nowMs + 15 * 60 * 1000;
  const claims: AccessClaims = {
    iss: config.issuer,
    aud: config.audience,
    sub: steamId64,
    sid: sessionId,
    scope: "maps:read maps:download",
    iat: Math.floor(nowMs / 1000),
    exp: Math.floor(expiresAtMs / 1000),
  };
  const payload = base64UrlEncode(encoder.encode(JSON.stringify(claims)));
  const signature = base64UrlEncode(await hmac(config.authSecret, `${header}.${payload}`));
  return { token: `${header}.${payload}.${signature}`, expiresAtMs };
}

export async function verifyAccessToken(
  config: RuntimeConfig,
  token: string,
  requiredScope: string,
  nowMs: number,
): Promise<AuthPrincipal> {
  const parts = token.split(".");
  if (parts.length !== 3) throw new HttpError(401, "token_invalid", "The access token is invalid.");
  const [headerText, payloadText, signatureText] = parts as [string, string, string];
  const signature = base64UrlDecode(signatureText);
  const expected = await hmac(config.authSecret, `${headerText}.${payloadText}`);
  if (signature === null || !constantTimeEqual(signature, expected)) {
    throw new HttpError(401, "token_invalid", "The access token is invalid.");
  }
  const headerBytes = base64UrlDecode(headerText);
  const payloadBytes = base64UrlDecode(payloadText);
  if (headerBytes === null || payloadBytes === null) {
    throw new HttpError(401, "token_invalid", "The access token is invalid.");
  }
  let header: unknown;
  let claims: unknown;
  try {
    header = JSON.parse(decoder.decode(headerBytes)) as unknown;
    claims = JSON.parse(decoder.decode(payloadBytes)) as unknown;
  } catch {
    throw new HttpError(401, "token_invalid", "The access token is invalid.");
  }
  if (!isPlainObject(header) || header.alg !== "HS256" || header.typ !== "JWT" || !isPlainObject(claims)) {
    throw new HttpError(401, "token_invalid", "The access token is invalid.");
  }
  if (
    claims.iss !== config.issuer || claims.aud !== config.audience || !isSteamId64(claims.sub) ||
    typeof claims.sid !== "string" || !/^[0-9a-f-]{36}$/.test(claims.sid) ||
    typeof claims.scope !== "string" || !Number.isSafeInteger(claims.iat) || !Number.isSafeInteger(claims.exp) ||
    (claims.exp as number) <= Math.floor(nowMs / 1000) || (claims.iat as number) > Math.floor(nowMs / 1000) + 60
  ) {
    throw new HttpError(401, "token_invalid", "The access token is invalid or expired.");
  }
  const scopes = claims.scope.split(" ").filter(Boolean);
  if (!scopes.includes(requiredScope)) throw new HttpError(403, "scope_denied", "The token lacks the required scope.");
  return { steamId64: claims.sub, sessionId: claims.sid, scopes };
}

export async function signedValue(config: RuntimeConfig, context: string, payload: unknown): Promise<string> {
  const body = base64UrlEncode(encoder.encode(JSON.stringify(payload)));
  const signature = base64UrlEncode(await hmac(config.authSecret, `${context}\u0000${body}`));
  return `${body}.${signature}`;
}

export async function verifySignedValue(
  config: RuntimeConfig,
  context: string,
  value: string,
): Promise<Record<string, unknown> | null> {
  const separator = value.lastIndexOf(".");
  if (separator < 1) return null;
  const body = value.slice(0, separator);
  const supplied = base64UrlDecode(value.slice(separator + 1));
  const expected = await hmac(config.authSecret, `${context}\u0000${body}`);
  const decoded = base64UrlDecode(body);
  if (supplied === null || decoded === null || !constantTimeEqual(supplied, expected)) return null;
  try {
    const result = JSON.parse(decoder.decode(decoded)) as unknown;
    return isPlainObject(result) ? result : null;
  } catch {
    return null;
  }
}
