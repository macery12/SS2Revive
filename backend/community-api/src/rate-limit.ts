import type { RuntimeConfig } from "./config";
import { opaqueHash } from "./crypto";
import { HttpError } from "./http";

/**
 * Anonymous limits key on the caller's address. Keying on a full IPv6 address makes those limits
 * meaningless, because a routed /64 - the standard residential and VPS allocation - hands the
 * caller 2^64 distinct buckets. Collapse IPv6 to its /64 prefix so one allocation counts once.
 */
function clientAddress(request: Request): string {
  const value = request.headers.get("CF-Connecting-IP") ?? "unknown";
  if (value.length > 64 || /[\u0000-\u0020\u007f-\u009f]/u.test(value)) return "invalid";
  if (!value.includes(":")) return value;
  const groups = expandIpv6(value.toLowerCase());
  return groups === null ? value : `${groups.slice(0, 4).join(":")}::/64`;
}

/** Expand an IPv6 literal to its eight groups, or null when it is not a plain IPv6 address. */
function expandIpv6(value: string): string[] | null {
  if (!/^[0-9a-f:]+$/u.test(value)) return null;
  const halves = value.split("::");
  if (halves.length > 2) return null;
  const head = halves[0] === "" ? [] : halves[0]!.split(":");
  const tail = halves.length === 2 ? (halves[1] === "" ? [] : halves[1]!.split(":")) : [];
  if (!head.every(isIpv6Group) || !tail.every(isIpv6Group)) return null;
  if (halves.length === 1) return head.length === 8 ? head : null;
  const missing = 8 - head.length - tail.length;
  if (missing < 1) return null;
  return [...head, ...Array<string>(missing).fill("0"), ...tail];
}

function isIpv6Group(group: string): boolean {
  return /^[0-9a-f]{1,4}$/u.test(group);
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
