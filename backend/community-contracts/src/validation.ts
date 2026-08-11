import {
  MAX_DESCRIPTION_CHARACTERS,
  MAX_METADATA_ITEM_CHARACTERS,
  MAX_TITLE_CHARACTERS,
} from "./types";

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const SEMVER_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;
const MAX_UINT64 = 18_446_744_073_709_551_615n;

export function isCanonicalUuid(value: unknown): value is string {
  return typeof value === "string" && UUID_PATTERN.test(value);
}

export function isSha256(value: unknown): value is string {
  return typeof value === "string" && SHA256_PATTERN.test(value);
}

export function isSemanticVersion(value: unknown): value is string {
  return typeof value === "string" && value.length <= 29 && SEMVER_PATTERN.test(value);
}

export function isSteamId64(value: unknown): value is string {
  if (typeof value !== "string" || !/^[1-9][0-9]{16,19}$/.test(value)) return false;
  try {
    return BigInt(value) <= MAX_UINT64;
  } catch {
    return false;
  }
}

export function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return false;
  const prototype = Object.getPrototypeOf(value) as object | null;
  return prototype === Object.prototype || prototype === null;
}

export function hasOnlyKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const allowed = new Set(keys);
  return Object.keys(value).every((key) => allowed.has(key));
}

export function cleanText(value: unknown, maximum: number): string {
  if (typeof value !== "string") return "";
  let result = "";
  for (const character of value) {
    if (result.length >= maximum) break;
    const code = character.codePointAt(0) ?? 0;
    if (character === "\r" || character === "\n" || character === "\t") {
      result += " ";
    } else if ((code >= 0 && code < 32) || (code >= 127 && code <= 159)) {
      continue;
    } else {
      result += character;
    }
  }
  return result.trim();
}

export function cleanTitle(value: unknown): string {
  return cleanText(value, MAX_TITLE_CHARACTERS) || "Untitled level";
}

export function cleanDescription(value: unknown): string {
  return cleanText(value, MAX_DESCRIPTION_CHARACTERS);
}

export function cleanMetadataItem(value: unknown): string {
  return cleanText(value, MAX_METADATA_ITEM_CHARACTERS);
}

export function parseBoundedPositiveInteger(
  value: string | null,
  fallback: number,
  maximum: number,
): number | null {
  if (value === null || value === "") return fallback;
  if (!/^[1-9][0-9]*$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed <= maximum ? parsed : null;
}
