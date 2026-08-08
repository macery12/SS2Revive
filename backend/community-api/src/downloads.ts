import { isSha256, MAX_BUNDLE_BYTES, MAX_IMAGE_BYTES } from "@ss2revive/community-contracts";
import type { RuntimeConfig } from "./config";
import { completeDownload, reserveDownload } from "./download-quota";
import { emptyResponse, HttpError, responseHeaders } from "./http";
import { downloadableVersion, type MapRow } from "./maps";

interface ByteRange {
  offset: number;
  length: number;
}

function etagMatches(header: string | null, etag: string): boolean {
  if (header === null) return false;
  return header.split(",").map((value) => value.trim()).some((value) => value === "*" || value === etag);
}

function parseRange(header: string, total: number): ByteRange | null {
  if (!header.startsWith("bytes=") || header.includes(",")) return null;
  const value = header.slice(6);
  const match = /^(\d*)-(\d*)$/.exec(value);
  if (match === null) return null;
  const startText = match[1] ?? "";
  const endText = match[2] ?? "";
  if (startText === "" && endText === "") return null;
  if (startText === "") {
    if (!/^[1-9][0-9]*$/.test(endText)) return null;
    const suffix = Number(endText);
    if (!Number.isSafeInteger(suffix) || suffix < 1) return null;
    const length = Math.min(suffix, total);
    return { offset: total - length, length };
  }
  if (!/^(?:0|[1-9][0-9]*)$/.test(startText)) return null;
  const start = Number(startText);
  if (!Number.isSafeInteger(start) || start >= total) return null;
  if (endText === "") return { offset: start, length: total - start };
  if (!/^(?:0|[1-9][0-9]*)$/.test(endText)) return null;
  const requestedEnd = Number(endText);
  if (!Number.isSafeInteger(requestedEnd) || requestedEnd < start) return null;
  const end = Math.min(requestedEnd, total - 1);
  return { offset: start, length: end - start + 1 };
}

function objectMetadata(row: MapRow, kind: "bundle" | "thumbnail"): {
  key: string;
  size: number;
  sha256: string;
  contentType: string;
  disposition?: string;
} {
  if (kind === "bundle") {
    return {
      key: row.bundle_key,
      size: row.size_bytes,
      sha256: row.sha256,
      contentType: "application/vnd.ss2revive.level",
      disposition: `attachment; filename="map-${row.id}-r${row.revision}.ss2level"`,
    };
  }
  if (row.thumbnail_key === null || row.thumbnail_size_bytes === null || row.thumbnail_sha256 === null) {
    throw new HttpError(404, "revision_not_found", "The map thumbnail was not found.");
  }
  return {
    key: row.thumbnail_key,
    size: row.thumbnail_size_bytes,
    sha256: row.thumbnail_sha256,
    contentType: "application/octet-stream",
  };
}

export async function downloadObject(
  request: Request,
  env: Env,
  config: RuntimeConfig,
  steamId64: string,
  mapId: string,
  revision: number,
  kind: "bundle" | "thumbnail",
  requestId: string,
  nowMs: number,
): Promise<Response> {
  if (request.method !== "GET" && request.method !== "HEAD") {
    throw new HttpError(405, "method_not_allowed", "The method is not allowed.", { Allow: "GET, HEAD" });
  }
  const row = await downloadableVersion(env, mapId, revision);
  const metadata = objectMetadata(row, kind);
  const maximumSize = kind === "bundle" ? MAX_BUNDLE_BYTES : MAX_IMAGE_BYTES;
  if (metadata.size < 1 || metadata.size > maximumSize || !isSha256(metadata.sha256) || metadata.key.length > 512) {
    throw new HttpError(503, "object_metadata_mismatch", "The stored object metadata is invalid.", { "Retry-After": "30" });
  }
  const etag = `"sha256-${metadata.sha256}"`;
  if (request.headers.has("If-Match") && !etagMatches(request.headers.get("If-Match"), etag)) {
    throw new HttpError(412, "etag_mismatch", "The requested object version does not match.");
  }

  const rangeHeader = request.headers.get("Range");
  if (etagMatches(request.headers.get("If-None-Match"), etag)) {
    return emptyResponse(304, requestId, { ETag: etag });
  }
  const head = await env.MAP_BUCKET.head(metadata.key);
  if (head === null || head.size !== metadata.size || head.customMetadata?.sha256 !== metadata.sha256) {
    throw new HttpError(503, "object_metadata_mismatch", "The stored object failed its metadata check.", { "Retry-After": "30" });
  }

  let range: ByteRange | null = null;
  if (rangeHeader !== null && (request.headers.get("If-Range") === null || request.headers.get("If-Range") === etag)) {
    range = parseRange(rangeHeader, metadata.size);
    if (range === null) {
      throw new HttpError(416, "range_not_satisfiable", "The byte range is not satisfiable.", {
        "Content-Range": `bytes */${metadata.size}`,
      });
    }
  }

  const status = range === null ? 200 : 206;
  const contentLength = range?.length ?? metadata.size;
  const headers = responseHeaders(requestId, {
    "Accept-Ranges": "bytes",
    "Cache-Control": "private, max-age=31536000, immutable",
    "Content-Length": String(contentLength),
    "Content-Type": metadata.contentType,
    ETag: etag,
    "X-Content-SHA256": metadata.sha256,
  });
  if (metadata.disposition !== undefined) headers.set("Content-Disposition", metadata.disposition);
  if (range !== null) {
    headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${metadata.size}`);
  }
  if (request.method === "HEAD") return new Response(null, { status, headers });

  const object = await env.MAP_BUCKET.get(
    metadata.key,
    range === null ? undefined : { range: { offset: range.offset, length: range.length } },
  );
  if (object === null || object.size !== metadata.size || object.customMetadata?.sha256 !== metadata.sha256) {
    throw new HttpError(503, "object_metadata_mismatch", "The stored object failed its metadata check.", { "Retry-After": "30" });
  }
  const lease = await reserveDownload(env, config, steamId64, contentLength, nowMs);
  const reader = object.body.getReader();
  let settled = false;
  const settle = async (): Promise<void> => {
    if (settled) return;
    settled = true;
    await completeDownload(env, lease);
  };
  const body = new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const result = await reader.read();
        if (result.done) {
          await settle();
          controller.close();
        } else {
          controller.enqueue(result.value);
        }
      } catch (error) {
        controller.error(error);
        await settle();
      }
    },
    async cancel(reason) {
      try {
        await reader.cancel(reason);
      } finally {
        await settle();
      }
    },
  });
  return new Response(body, { status, headers });
}
