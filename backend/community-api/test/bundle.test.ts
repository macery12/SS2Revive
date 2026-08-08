import { describe, expect, it } from "vitest";
import {
  BundleValidationError,
  inspectLevelBundle,
  levelCodeFromId,
  MAX_BUNDLE_BYTES,
} from "@ss2revive/community-contracts";
import { buildLocalFixture } from "../src/local-fixture";

function expectBundleError(code: string, action: () => Promise<unknown>): Promise<void> {
  return expect(action()).rejects.toMatchObject({ name: "BundleValidationError", code });
}

describe("Level bundle contract", () => {
  it("matches the game's fixed GUID share-code vector", () => {
    expect(levelCodeFromId("1a658233-92c5-4b63-87fc-4740c855730b")).toBe("M4JlGsWSY0uH_EdAyFVzCw");
    expect(levelCodeFromId("not-a-guid")).toBe("");
  });

  it("accepts the deterministic current-format fixture", async () => {
    const fixture = await buildLocalFixture();
    const parsed = await inspectLevelBundle(fixture.bytes, { nowMs: Date.UTC(2026, 7, 8) });
    expect(parsed.manifest.id).toBe("1a658233-92c5-4b63-87fc-4740c855730b");
    expect(parsed.manifest.clientVersion).toBe(29);
    expect(parsed.code).toBe("M4JlGsWSY0uH_EdAyFVzCw");
    expect(parsed.bundleSha256).toBe(fixture.sha256);
    expect(parsed.thumbnailInfo).toMatchObject({ width: 1, height: 1, format: 4, dataLength: 4 });
  });

  it("rejects truncation, trailing bytes, and checksum tampering", async () => {
    const fixture = await buildLocalFixture();
    await expectBundleError("bundle_malformed", () => inspectLevelBundle(fixture.bytes.slice(0, -1)));
    const trailing = new Uint8Array(fixture.bytes.length + 1);
    trailing.set(fixture.bytes);
    await expectBundleError("bundle_trailing_data", () => inspectLevelBundle(trailing));
    const tampered = fixture.bytes.slice();
    const contentMarker = new TextEncoder().encode("Surgeons");
    const contentIndex = tampered.findIndex((_, offset) =>
      contentMarker.every((value, inner) => tampered[offset + inner] === value));
    expect(contentIndex).toBeGreaterThan(0);
    const tamperedIndex = contentIndex + 10;
    tampered[tamperedIndex] = (tampered[tamperedIndex] ?? 0) ^ 1;
    await expectBundleError("content_checksum_invalid", () => inspectLevelBundle(tampered));
  });

  it("rejects an oversized bundle before parsing", async () => {
    await expectBundleError("bundle_oversized", () => inspectLevelBundle(new Uint8Array(MAX_BUNDLE_BYTES + 1)));
  });

  it("rejects legacy container versions", async () => {
    const fixture = await buildLocalFixture();
    const legacy = fixture.bytes.slice();
    legacy[16] = 1;
    legacy[17] = 0;
    await expectBundleError("bundle_version_invalid", () => inspectLevelBundle(legacy));
  });

  it("rejects duplicate manifest keys", async () => {
    const fixture = await buildLocalFixture();
    const bytes = fixture.bytes.slice();
    const marker = new TextEncoder().encode('"title":');
    const index = bytes.findIndex((_, offset) => marker.every((value, inner) => bytes[offset + inner] === value));
    expect(index).toBeGreaterThan(0);
    const replacement = new TextEncoder().encode('"id"   :');
    expect(replacement.length).toBe(marker.length);
    bytes.set(replacement, index);
    await expectBundleError("manifest_duplicate_key", () => inspectLevelBundle(bytes));
  });

  it("rejects duplicate known container entries", async () => {
    const fixture = await buildLocalFixture();
    const marker = new TextEncoder().encode("thumb.png");
    const nameIndex = fixture.bytes.findIndex((_, offset) =>
      marker.every((value, inner) => fixture.bytes[offset + inner] === value));
    expect(nameIndex).toBeGreaterThan(1);
    const entryStart = nameIndex - 2;
    const entryLength = 2 + marker.length + 4 + fixture.thumbnail.length;
    const duplicate = fixture.bytes.slice(entryStart, entryStart + entryLength);
    const bytes = new Uint8Array(fixture.bytes.length + duplicate.length);
    bytes.set(fixture.bytes.slice(0, -2));
    bytes.set(duplicate, fixture.bytes.length - 2);
    await expectBundleError("bundle_duplicate_entry", () => inspectLevelBundle(bytes));
  });

  it("exposes typed validation errors without raw payloads", () => {
    const error = new BundleValidationError("bundle_malformed", "safe message");
    expect(error).toMatchObject({ name: "BundleValidationError", code: "bundle_malformed", message: "safe message" });
  });
});
