import { levelCodeFromId } from "./level-code";
import {
  MAP_FORMAT_VERSION,
  MAX_BUNDLE_BYTES,
  MAX_CONTENT_BYTES,
  MAX_CREATORS,
  MAX_DESCRIPTION_CHARACTERS,
  MAX_IMAGE_BYTES,
  MAX_MANIFEST_BYTES,
  MAX_METADATA_ITEM_CHARACTERS,
  MAX_TAGS,
  MAX_TITLE_CHARACTERS,
} from "./types";
import { hasOnlyKeys, isCanonicalUuid, isPlainObject, isSha256 } from "./validation";

const BUNDLE_MAGIC = new TextEncoder().encode("SS2REVIVE LEVEL\n");
const LEVEL_MAGIC = new TextEncoder().encode("Surgeons");
const KNOWN_ENTRIES = new Set(["asset.json", "level.bin", "level.png", "thumb.png"]);
const MAX_ENTRIES = 16;
const MAX_NAME_BYTES = 64;
const MAX_JSON_DEPTH = 32;
const MAX_INT32 = 2_147_483_647;

export type BundleValidationCode =
  | "bundle_empty"
  | "bundle_oversized"
  | "bundle_magic_invalid"
  | "bundle_version_invalid"
  | "bundle_malformed"
  | "bundle_too_many_entries"
  | "bundle_duplicate_entry"
  | "bundle_entry_oversized"
  | "bundle_trailing_data"
  | "manifest_missing"
  | "manifest_invalid"
  | "manifest_duplicate_key"
  | "manifest_metadata_invalid"
  | "content_missing"
  | "content_magic_invalid"
  | "content_format_invalid"
  | "content_checksum_invalid"
  | "image_invalid";

export class BundleValidationError extends Error {
  readonly code: BundleValidationCode;

  constructor(code: BundleValidationCode, message: string) {
    super(message);
    this.name = "BundleValidationError";
    this.code = code;
  }
}

export interface BundleManifest {
  bundleVersion: 2;
  id: string;
  title: string;
  description: string;
  createdAt: number;
  exportedAt: number;
  clientVersion: 29;
  contentVersion: number;
  reviveVersion: string;
  contentSha256: string;
  creators: string[];
  tags: string[];
  configurations: unknown[];
  validations: unknown[];
}

export interface GameImageInfo {
  width: number;
  height: number;
  format: 1 | 2 | 3 | 4 | 5 | 7;
  dataLength: number;
  dataOffset: number;
}

export interface InspectedLevelBundle {
  containerVersion: 2;
  manifest: BundleManifest;
  code: string;
  content: Uint8Array;
  contentImage?: Uint8Array;
  thumbnail?: Uint8Array;
  thumbnailInfo?: GameImageInfo;
  bundleSha256: string;
}

export interface InspectBundleOptions {
  nowMs?: number;
}

interface ParsedEntries {
  manifest?: Uint8Array;
  content?: Uint8Array;
  contentImage?: Uint8Array;
  thumbnail?: Uint8Array;
}

function fail(code: BundleValidationCode, message: string): never {
  throw new BundleValidationError(code, message);
}

function equalPrefix(bytes: Uint8Array, expected: Uint8Array): boolean {
  if (bytes.length < expected.length) return false;
  for (let index = 0; index < expected.length; index += 1) {
    if (bytes[index] !== expected[index]) return false;
  }
  return true;
}

function entryLimit(name: string): number {
  if (name === "asset.json") return MAX_MANIFEST_BYTES;
  if (name === "level.bin") return MAX_CONTENT_BYTES;
  if (name === "level.png" || name === "thumb.png") return MAX_IMAGE_BYTES;
  return MAX_MANIFEST_BYTES;
}

function parseEntries(bytes: Uint8Array): { containerVersion: 2; entries: ParsedEntries } {
  if (bytes.length < BUNDLE_MAGIC.length + 2) fail("bundle_empty", "The bundle is empty or truncated.");
  if (bytes.length > MAX_BUNDLE_BYTES) fail("bundle_oversized", "The bundle exceeds 24 MiB.");
  if (!equalPrefix(bytes, BUNDLE_MAGIC)) fail("bundle_magic_invalid", "The bundle magic is invalid.");

  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = BUNDLE_MAGIC.length;
  const containerVersion = view.getUint16(offset, true);
  offset += 2;
  if (containerVersion !== 2) {
    fail("bundle_version_invalid", "Only clean-slate container version 2 is supported.");
  }

  const entries: ParsedEntries = {};
  const known = new Set<string>();
  const decoder = new TextDecoder("utf-8", { fatal: true });

  for (let entryCount = 0; ; entryCount += 1) {
    if (entryCount >= MAX_ENTRIES) fail("bundle_too_many_entries", "The bundle has too many entries.");
    if (offset + 2 > bytes.length) fail("bundle_malformed", "The bundle ends inside an entry header.");
    const nameLength = view.getUint16(offset, true);
    offset += 2;
    if (nameLength === 0) break;
    if (nameLength > MAX_NAME_BYTES || offset + nameLength + 4 > bytes.length) {
      fail("bundle_malformed", "The bundle entry header is malformed.");
    }

    let name: string;
    try {
      name = decoder.decode(bytes.subarray(offset, offset + nameLength));
    } catch {
      fail("bundle_malformed", "The bundle entry name is not valid UTF-8.");
    }
    offset += nameLength;
    const length = view.getInt32(offset, true);
    offset += 4;
    if (length < 0 || length > bytes.length - offset) {
      fail("bundle_malformed", "The bundle entry length is invalid.");
    }
    if (length > entryLimit(name)) {
      fail("bundle_entry_oversized", `The ${name || "unnamed"} entry exceeds its limit.`);
    }
    if (KNOWN_ENTRIES.has(name)) {
      if (known.has(name)) fail("bundle_duplicate_entry", `The ${name} entry appears more than once.`);
      known.add(name);
    }

    if (name === "asset.json") entries.manifest = bytes.slice(offset, offset + length);
    else if (name === "level.bin") entries.content = bytes.slice(offset, offset + length);
    else if (name === "level.png") entries.contentImage = bytes.slice(offset, offset + length);
    else if (name === "thumb.png") entries.thumbnail = bytes.slice(offset, offset + length);
    offset += length;
  }

  if (offset !== bytes.length) fail("bundle_trailing_data", "The bundle has trailing data.");
  return { containerVersion, entries };
}

class JsonShapeScanner {
  readonly #text: string;
  #offset = 0;
  duplicateKey = false;

  constructor(text: string) {
    this.#text = text;
  }

  scan(): boolean {
    try {
      this.#skipWhitespace();
      this.#value(0);
      this.#skipWhitespace();
      return this.#offset === this.#text.length;
    } catch {
      return false;
    }
  }

  #value(depth: number): void {
    if (depth > MAX_JSON_DEPTH) throw new Error("depth");
    const character = this.#text[this.#offset];
    if (character === "{") this.#object(depth + 1);
    else if (character === "[") this.#array(depth + 1);
    else if (character === '"') this.#string();
    else if (character === "t") this.#literal("true");
    else if (character === "f") this.#literal("false");
    else if (character === "n") this.#literal("null");
    else this.#number();
  }

  #object(depth: number): void {
    this.#offset += 1;
    this.#skipWhitespace();
    const keys = new Set<string>();
    if (this.#text[this.#offset] === "}") {
      this.#offset += 1;
      return;
    }
    for (;;) {
      if (this.#text[this.#offset] !== '"') throw new Error("object key");
      const keyLiteral = this.#string();
      const key = JSON.parse(keyLiteral) as string;
      if (keys.has(key)) this.duplicateKey = true;
      keys.add(key);
      this.#skipWhitespace();
      if (this.#text[this.#offset] !== ":") throw new Error("colon");
      this.#offset += 1;
      this.#skipWhitespace();
      this.#value(depth);
      this.#skipWhitespace();
      const character = this.#text[this.#offset];
      if (character === "}") {
        this.#offset += 1;
        return;
      }
      if (character !== ",") throw new Error("object delimiter");
      this.#offset += 1;
      this.#skipWhitespace();
    }
  }

  #array(depth: number): void {
    this.#offset += 1;
    this.#skipWhitespace();
    if (this.#text[this.#offset] === "]") {
      this.#offset += 1;
      return;
    }
    for (;;) {
      this.#value(depth);
      this.#skipWhitespace();
      const character = this.#text[this.#offset];
      if (character === "]") {
        this.#offset += 1;
        return;
      }
      if (character !== ",") throw new Error("array delimiter");
      this.#offset += 1;
      this.#skipWhitespace();
    }
  }

  #string(): string {
    const start = this.#offset;
    this.#offset += 1;
    for (;;) {
      const character = this.#text[this.#offset];
      if (character === undefined || character.charCodeAt(0) < 32) throw new Error("string");
      if (character === '"') {
        this.#offset += 1;
        return this.#text.slice(start, this.#offset);
      }
      if (character === "\\") {
        this.#offset += 1;
        const escape = this.#text[this.#offset];
        if (escape === "u") {
          const digits = this.#text.slice(this.#offset + 1, this.#offset + 5);
          if (!/^[0-9a-fA-F]{4}$/.test(digits)) throw new Error("unicode escape");
          this.#offset += 5;
          continue;
        }
        if (escape === undefined || !'"\\/bfnrt'.includes(escape)) throw new Error("escape");
      }
      this.#offset += 1;
    }
  }

  #number(): void {
    const remaining = this.#text.slice(this.#offset);
    const match = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(remaining);
    if (match === null) throw new Error("number");
    this.#offset += match[0].length;
  }

  #literal(value: string): void {
    if (!this.#text.startsWith(value, this.#offset)) throw new Error("literal");
    this.#offset += value.length;
  }

  #skipWhitespace(): void {
    while (/\s/.test(this.#text[this.#offset] ?? "") && this.#offset < this.#text.length) {
      const character = this.#text[this.#offset];
      if (character !== " " && character !== "\t" && character !== "\r" && character !== "\n") {
        throw new Error("whitespace");
      }
      this.#offset += 1;
    }
  }
}

function requireInteger(value: unknown, name: string, minimum = 0, maximum = Number.MAX_SAFE_INTEGER): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum) {
    fail("manifest_metadata_invalid", `${name} is not a bounded integer.`);
  }
  return value as number;
}

function requireString(value: unknown, name: string, maximum: number, allowEmpty: boolean): string {
  if (typeof value !== "string" || value.length > maximum || (!allowEmpty && value.trim().length === 0)) {
    fail("manifest_metadata_invalid", `${name} is invalid.`);
  }
  for (const character of value) {
    const code = character.codePointAt(0) ?? 0;
    if ((code >= 0 && code < 32) || (code >= 127 && code <= 159)) {
      fail("manifest_metadata_invalid", `${name} contains control characters.`);
    }
  }
  return value;
}

function requireStringArray(value: unknown, name: string, maximumItems: number): string[] {
  if (!Array.isArray(value) || value.length > maximumItems) {
    fail("manifest_metadata_invalid", `${name} has too many entries or is not an array.`);
  }
  const result: string[] = [];
  const seen = new Set<string>();
  for (const item of value) {
    const text = requireString(item, name, MAX_METADATA_ITEM_CHARACTERS, false);
    if (seen.has(text)) fail("manifest_metadata_invalid", `${name} contains a duplicate entry.`);
    seen.add(text);
    result.push(text);
  }
  return result;
}

function validateStructuredMetadata(configurations: unknown, validations: unknown): {
  configurations: unknown[];
  validations: unknown[];
} {
  if (!Array.isArray(configurations) || configurations.length > 8) {
    fail("manifest_metadata_invalid", "configurations must be an array with at most 8 entries.");
  }
  for (const configuration of configurations) {
    if (!isPlainObject(configuration)) fail("manifest_metadata_invalid", "A configuration is not an object.");
    if (!hasOnlyKeys(configuration, ["numberPlayers", "numberTeams", "levelTeamConfigurations"])) {
      fail("manifest_metadata_invalid", "A configuration contains an unsupported field.");
    }
    requireInteger(configuration.numberPlayers, "numberPlayers", 1, 4);
    requireInteger(configuration.numberTeams, "numberTeams", 0, 4);
    const teams = configuration.levelTeamConfigurations;
    if (teams === undefined) continue;
    if (!Array.isArray(teams) || teams.length > 4) {
      fail("manifest_metadata_invalid", "levelTeamConfigurations is invalid.");
    }
    for (const team of teams) {
      if (!isPlainObject(team)) fail("manifest_metadata_invalid", "A team configuration is not an object.");
      if (!hasOnlyKeys(team, ["objectives", "playersInTeam"])) {
        fail("manifest_metadata_invalid", "A team configuration contains an unsupported field.");
      }
      if (team.objectives !== undefined) requireStringArray(team.objectives, "objectives", 32);
      if (team.playersInTeam !== undefined) {
        if (!Array.isArray(team.playersInTeam) || team.playersInTeam.length > 4) {
          fail("manifest_metadata_invalid", "playersInTeam is invalid.");
        }
        for (const player of team.playersInTeam) requireInteger(player, "player index", 0, 3);
      }
    }
  }

  if (!Array.isArray(validations) || validations.length > 32) {
    fail("manifest_metadata_invalid", "validations must be an array with at most 32 entries.");
  }
  for (const validation of validations) {
    if (!isPlainObject(validation)) fail("manifest_metadata_invalid", "A validation is not an object.");
    if (!hasOnlyKeys(validation, ["description"])) {
      fail("manifest_metadata_invalid", "A validation contains an unsupported field.");
    }
    if (validation.description !== undefined) requireString(validation.description, "validation description", 255, true);
  }
  return { configurations, validations };
}

function parseManifest(bytes: Uint8Array, containerVersion: 2, nowMs: number): BundleManifest {
  if (bytes.length === 0 || bytes.length > MAX_MANIFEST_BYTES) {
    fail("manifest_missing", "The bundle has no readable manifest.");
  }
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    fail("manifest_invalid", "The manifest is not valid UTF-8.");
  }
  const scanner = new JsonShapeScanner(text);
  if (!scanner.scan()) fail("manifest_invalid", "The manifest is invalid or nested too deeply.");
  if (scanner.duplicateKey) fail("manifest_duplicate_key", "The manifest contains a duplicate object key.");

  let raw: unknown;
  try {
    raw = JSON.parse(text) as unknown;
  } catch {
    fail("manifest_invalid", "The manifest is not valid JSON.");
  }
  if (!isPlainObject(raw)) fail("manifest_invalid", "The manifest root must be an object.");
  if (!hasOnlyKeys(raw, [
    "bundleVersion", "id", "title", "description", "createdAt", "exportedAt",
    "clientVersion", "contentVersion", "reviveVersion", "contentSha256",
    "creators", "tags", "configurations", "validations",
  ])) {
    fail("manifest_metadata_invalid", "The manifest contains an unsupported field.");
  }

  const bundleVersion = requireInteger(raw.bundleVersion, "bundleVersion", 2, 2);
  if (bundleVersion !== containerVersion) fail("manifest_metadata_invalid", "Bundle versions disagree.");
  if (!isCanonicalUuid(raw.id)) fail("manifest_metadata_invalid", "The map id is not a canonical GUID.");
  const title = requireString(raw.title, "title", MAX_TITLE_CHARACTERS, false);
  const description = requireString(raw.description, "description", MAX_DESCRIPTION_CHARACTERS, true);
  const createdAt = requireInteger(raw.createdAt, "createdAt");
  const exportedAt = requireInteger(raw.exportedAt, "exportedAt");
  if (exportedAt < createdAt || createdAt > nowMs + 86_400_000 || exportedAt > nowMs + 86_400_000) {
    fail("manifest_metadata_invalid", "The manifest timestamps are inconsistent.");
  }
  const clientVersion = requireInteger(raw.clientVersion, "clientVersion", MAP_FORMAT_VERSION, MAP_FORMAT_VERSION);
  const contentVersion = requireInteger(raw.contentVersion, "contentVersion", 1, MAX_INT32);
  const reviveVersion = requireString(raw.reviveVersion, "reviveVersion", 64, true);
  if (!isSha256(raw.contentSha256)) fail("content_checksum_invalid", "contentSha256 is not canonical lowercase SHA-256.");
  const creators = requireStringArray(raw.creators, "creators", MAX_CREATORS);
  const tags = requireStringArray(raw.tags, "tags", MAX_TAGS);
  const structured = validateStructuredMetadata(raw.configurations, raw.validations);

  return {
    bundleVersion: bundleVersion as 2,
    id: raw.id,
    title,
    description,
    createdAt,
    exportedAt,
    clientVersion: clientVersion as 29,
    contentVersion,
    reviveVersion,
    contentSha256: raw.contentSha256,
    creators,
    tags,
    configurations: structured.configurations,
    validations: structured.validations,
  };
}

function readBits(bytes: Uint8Array, startBit: number, count: number): number | null {
  if (startBit < 0 || count < 0 || count > 31 || startBit + count > bytes.length * 8) return null;
  let value = 0;
  for (let bit = 0; bit < count; bit += 1) {
    const absolute = startBit + bit;
    const set = ((bytes[Math.floor(absolute / 8)] ?? 0) >> (absolute % 8)) & 1;
    value += set * 2 ** bit;
  }
  return value;
}

export function inspectGameImage(bytes: Uint8Array): GameImageInfo {
  if (bytes.length < 13 || bytes.length > MAX_IMAGE_BYTES) fail("image_invalid", "The game image size is invalid.");
  const width = readBits(bytes, 0, 31);
  const height = readBits(bytes, 31, 31);
  const format = readBits(bytes, 62, 4);
  const dataLength = readBits(bytes, 66, 31);
  if (width === null || height === null || format === null || dataLength === null) {
    fail("image_invalid", "The game image header is truncated.");
  }
  const bytesPerPixel = new Map<number, number>([[1, 1], [2, 2], [3, 3], [4, 4], [5, 4], [7, 2]]).get(format);
  if (
    width < 1 || height < 1 || width > 2048 || height > 2048 || width * height > 4 * 1024 * 1024 ||
    dataLength < 1 || dataLength > MAX_IMAGE_BYTES || bytesPerPixel === undefined ||
    width * height * bytesPerPixel > dataLength || 13 + dataLength !== bytes.length
  ) {
    fail("image_invalid", "The game image dimensions, format, or payload length is unsafe.");
  }
  return { width, height, format: format as GameImageInfo["format"], dataLength, dataOffset: 13 };
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const source = new Uint8Array(bytes.byteLength);
  source.set(bytes);
  const digest = await crypto.subtle.digest("SHA-256", source);
  return [...new Uint8Array(digest)].map((value) => value.toString(16).padStart(2, "0")).join("");
}

export async function inspectLevelBundle(
  input: ArrayBuffer | Uint8Array,
  options: InspectBundleOptions = {},
): Promise<InspectedLevelBundle> {
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
  const { containerVersion, entries } = parseEntries(bytes);
  if (entries.manifest === undefined) fail("manifest_missing", "The bundle has no asset.json entry.");
  if (entries.content === undefined || entries.content.length === 0) fail("content_missing", "The bundle has no level.bin entry.");
  if (entries.content.length < 10 || !equalPrefix(entries.content, LEVEL_MAGIC)) {
    fail("content_magic_invalid", "level.bin is not a Surgeon Simulator 2 level.");
  }
  const levelVersion = entries.content[8]! | (entries.content[9]! << 8);
  if (levelVersion !== MAP_FORMAT_VERSION) fail("content_format_invalid", "Only map format 29 is accepted.");

  const manifest = parseManifest(entries.manifest, containerVersion, options.nowMs ?? Date.now());
  if (manifest.clientVersion !== levelVersion) {
    fail("content_format_invalid", "The declared and embedded map versions disagree.");
  }
  const contentSha256 = await sha256Hex(entries.content);
  if (contentSha256 !== manifest.contentSha256) {
    fail("content_checksum_invalid", "level.bin does not match contentSha256.");
  }

  let thumbnailInfo: GameImageInfo | undefined;
  if (entries.contentImage !== undefined) inspectGameImage(entries.contentImage);
  if (entries.thumbnail !== undefined) thumbnailInfo = inspectGameImage(entries.thumbnail);

  const result: InspectedLevelBundle = {
    containerVersion,
    manifest,
    code: levelCodeFromId(manifest.id),
    content: entries.content,
    bundleSha256: await sha256Hex(bytes),
  };
  if (entries.contentImage !== undefined) result.contentImage = entries.contentImage;
  if (entries.thumbnail !== undefined) result.thumbnail = entries.thumbnail;
  if (thumbnailInfo !== undefined) result.thumbnailInfo = thumbnailInfo;
  return result;
}
