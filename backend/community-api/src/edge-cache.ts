import type { RuntimeConfig } from "./config";

/**
 * A Worker response is not stored in Cloudflare's cache just because it carries a public
 * Cache-Control directive — the Worker *is* the origin, so every request re-ran the D1 query and
 * the R2 operations behind it. Reads that are identical for every caller go through the Cache API
 * explicitly so repeat traffic costs nothing beyond the Worker invocation itself.
 */
const edgeCache = (caches as unknown as { default: Cache }).default;

/** Conditional and ranged reads bypass the cache so validator and range semantics stay exact. */
function isCacheable(request: Request): boolean {
  return request.method === "GET" &&
    !request.headers.has("Range") && !request.headers.has("If-Range") &&
    !request.headers.has("If-None-Match") && !request.headers.has("If-Match") &&
    !request.headers.has("Authorization");
}

function cacheKey(url: string): Request {
  return new Request(url, { method: "GET" });
}

/**
 * Cache an approved object's bytes under a content-addressed key rather than under its public URL.
 * Caching the URL would let a cached 200 outlive the authorization that produced it — a takedown,
 * an unpublish, or corrupted D1 metadata would keep serving from cache because the database check
 * no longer ran. Keying on the SHA-256 means the entry is immutable by construction, so the
 * per-request D1 authorization lookup always happens and only the R2 read is elided.
 */
export async function cachedObjectBody(
  ctx: ExecutionContext,
  sha256: string,
  load: () => Promise<ReadableStream | null>,
): Promise<ReadableStream | null> {
  const key = cacheKey(`https://object.invalid/sha256/${sha256}`);
  const hit = await edgeCache.match(key);
  if (hit?.body != null) return hit.body;
  const body = await load();
  if (body === null) return null;
  const [toCache, toReturn] = body.tee();
  ctx.waitUntil(edgeCache.put(key, new Response(toCache, {
    headers: { "Cache-Control": "public, max-age=31536000, immutable" },
  })));
  return toReturn;
}

export async function withEdgeCache(
  request: Request,
  ctx: ExecutionContext,
  handler: () => Promise<Response>,
): Promise<Response> {
  if (!isCacheable(request)) return handler();
  const key = cacheKey(new URL(request.url).toString());
  const hit = await edgeCache.match(key);
  if (hit !== undefined) return hit;
  const response = await handler();
  if (response.status === 200 && (response.headers.get("Cache-Control") ?? "").includes("public")) {
    ctx.waitUntil(edgeCache.put(key, response.clone()));
  }
  return response;
}

/**
 * Drop the cached public views of a map after its publication or moderation state changes, so a
 * takedown is visible immediately rather than after the cache entry expires.
 */
export function purgeMapCache(
  ctx: ExecutionContext,
  config: RuntimeConfig,
  mapId: string,
  revisions: readonly number[],
): void {
  const urls = [
    `${config.publicOrigin}/v1/catalog`,
    `${config.publicOrigin}/v1/maps/${mapId}`,
    `${config.publicOrigin}/v1/maps/${mapId}/versions`,
  ];
  for (const revision of revisions) {
    urls.push(`${config.publicOrigin}/v1/maps/${mapId}/versions/${revision}/download`);
    urls.push(`${config.publicOrigin}/v1/maps/${mapId}/versions/${revision}/thumbnail`);
  }
  ctx.waitUntil(Promise.all(urls.map((url) => edgeCache.delete(cacheKey(url)))).then(() => undefined));
}
