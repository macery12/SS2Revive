import type { RuntimeConfig } from "./config";
import { opaqueHash } from "./crypto";
import { HttpError } from "./http";

function clientAddress(request: Request): string {
  const value = request.headers.get("CF-Connecting-IP") ?? "unknown";
  if (value.length > 64 || /[\u0000-\u0020\u007f-\u009f]/u.test(value)) return "invalid";
  return value;
}

export async function anonymousRateKey(
  request: Request,
  config: RuntimeConfig,
  routeClass: string,
): Promise<string> {
  return opaqueHash(config, "rate-limit", `${routeClass}\u0000${clientAddress(request)}`);
}

export async function actorRateKey(
  config: RuntimeConfig,
  routeClass: string,
  steamId64: string,
): Promise<string> {
  return opaqueHash(config, "rate-limit", `${routeClass}\u0000steam\u0000${steamId64}`);
}

export async function enforceRateLimit(
  limiter: RateLimit | undefined,
  key: string,
  retryAfterSeconds = 60,
): Promise<void> {
  if (limiter === undefined || typeof limiter.limit !== "function") {
    throw new HttpError(503, "temporarily_unavailable", "The request limiter is unavailable.", {
      "Retry-After": "30",
    });
  }
  const result = await limiter.limit({ key });
  if (!result.success) {
    throw new HttpError(429, "rate_limited", "Try again later.", {
      "Retry-After": String(retryAfterSeconds),
    });
  }
}
