import { isSteamId64 } from "@ss2revive/community-contracts";
import { HttpError } from "./http";

export interface RuntimeConfig {
  environment: "local" | "staging" | "production";
  allowMockAuth: boolean;
  issuer: string;
  audience: string;
  authSecret: string;
  mockSteamId64: string;
  publicOrigin: string;
  downloadDailyBytes: number;
  downloadConcurrency: number;
  publisherSteamIds: ReadonlySet<string>;
}

function isBase64Secret(value: unknown): value is string {
  if (typeof value !== "string" || !/^[A-Za-z0-9+/]{43}=$/u.test(value)) return false;
  try {
    return atob(value).length === 32;
  } catch {
    return false;
  }
}

function boundedInteger(value: unknown, minimum: number, maximum: number): number | null {
  if (typeof value !== "string" || !/^[1-9][0-9]*$/u.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= minimum && parsed <= maximum ? parsed : null;
}

function validPublicOrigin(value: unknown, environment: RuntimeConfig["environment"]): value is string {
  if (typeof value !== "string" || value.length > 256) return false;
  try {
    const url = new URL(value);
    if (url.origin !== value || url.username !== "" || url.password !== "") return false;
    if (environment === "local") {
      return url.protocol === "http:" &&
        (url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]");
    }
    return url.protocol === "https:";
  } catch {
    return false;
  }
}

export function runtimeConfig(env: Env): RuntimeConfig {
  if (env.ENVIRONMENT !== "local" && env.ENVIRONMENT !== "staging" && env.ENVIRONMENT !== "production") {
    throw new HttpError(503, "temporarily_unavailable", "The service configuration is invalid.");
  }
  const authSecret = env.ENVIRONMENT === "local" ? env.LOCAL_AUTH_SECRET : env.AUTH_SIGNING_SECRET;
  if (!isBase64Secret(authSecret)) {
    throw new HttpError(503, "temporarily_unavailable", "The authentication signing secret is not configured.");
  }
  if (
    typeof env.AUTH_ISSUER !== "string" || typeof env.AUTH_AUDIENCE !== "string" ||
    env.AUTH_ISSUER.length < 1 || env.AUTH_ISSUER.length > 256 ||
    env.AUTH_AUDIENCE.length < 1 || env.AUTH_AUDIENCE.length > 128 ||
    /[\u0000-\u001f\u007f-\u009f]/u.test(env.AUTH_ISSUER) ||
    /[\u0000-\u001f\u007f-\u009f]/u.test(env.AUTH_AUDIENCE)
  ) {
    throw new HttpError(503, "temporarily_unavailable", "The authentication token configuration is invalid.");
  }
  const allowMockAuth = env.ENVIRONMENT === "local" && env.ALLOW_MOCK_AUTH === "true";
  if (allowMockAuth && !isSteamId64(env.MOCK_STEAM_ID64)) {
    throw new HttpError(503, "temporarily_unavailable", "The local Steam identity is invalid.");
  }
  if (!validPublicOrigin(env.PUBLIC_ORIGIN, env.ENVIRONMENT)) {
    throw new HttpError(503, "temporarily_unavailable", "The public origin configuration is invalid.");
  }
  const downloadDailyBytes = boundedInteger(env.DOWNLOAD_DAILY_BYTES, 1024 * 1024, 100 * 1024 * 1024 * 1024);
  const downloadConcurrency = boundedInteger(env.DOWNLOAD_CONCURRENCY, 1, 10);
  if (downloadDailyBytes === null || downloadConcurrency === null) {
    throw new HttpError(503, "temporarily_unavailable", "The download quota configuration is invalid.");
  }
  if (typeof env.PUBLISHER_STEAM_IDS !== "string" || env.PUBLISHER_STEAM_IDS.length > 1024) {
    throw new HttpError(503, "temporarily_unavailable", "The publisher configuration is invalid.");
  }
  const publisherSteamIds = new Set<string>();
  for (const value of env.PUBLISHER_STEAM_IDS.split(",")) {
    const steamId64 = value.trim();
    if (steamId64 === "") continue;
    if (!isSteamId64(steamId64)) {
      throw new HttpError(503, "temporarily_unavailable", "The publisher configuration is invalid.");
    }
    publisherSteamIds.add(steamId64);
  }
  if (publisherSteamIds.size === 0) {
    throw new HttpError(503, "temporarily_unavailable", "No map publisher is configured.");
  }
  return {
    environment: env.ENVIRONMENT,
    allowMockAuth,
    issuer: env.AUTH_ISSUER,
    audience: env.AUTH_AUDIENCE,
    authSecret,
    mockSteamId64: env.MOCK_STEAM_ID64,
    publicOrigin: env.PUBLIC_ORIGIN,
    downloadDailyBytes,
    downloadConcurrency,
    publisherSteamIds,
  };
}
