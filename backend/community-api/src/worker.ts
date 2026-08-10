import { isCanonicalUuid } from "@ss2revive/community-contracts";
import {
  authenticate,
  createDeviceSession,
  localActivationPage,
  logoutSession,
  pollDeviceSession,
  refreshSession,
} from "./auth";
import { runtimeConfig } from "./config";
import { opaqueHash } from "./crypto";
import { downloadObject } from "./downloads";
import { errorResponse, HttpError, jsonResponse, requireMethod } from "./http";
import { seedLocalFixture } from "./local-fixture";
import { cleanupExpiredState } from "./maintenance";
import { catalogDocument, listMaps, mapDetail, mapVersions } from "./maps";
import { actorRateKey, anonymousRateKey, enforceRateLimit } from "./rate-limit";
import {
  confirmSteamLogin,
  startSteamLogin,
  steamActivationPage,
  steamCallback,
} from "./steam-openid";
import {
  cancelUpload,
  getUploadStatus,
  putUploadBundle,
  reserveUpload,
  validateUploadMessage,
} from "./uploads";

function safePath(request: Request): string {
  if (request.url.length > 4096) throw new HttpError(414, "invalid_request", "The request URL is too long.");
  const rawAfterScheme = request.url.indexOf("://");
  const rawPathStart = request.url.indexOf("/", rawAfterScheme < 0 ? 0 : rawAfterScheme + 3);
  const rawPathWithQuery = rawPathStart < 0 ? "/" : request.url.slice(rawPathStart);
  const rawPath = rawPathWithQuery.split("?", 1)[0] ?? "/";
  if (
    rawPath.length > 1024 || rawPath.includes("\\") || rawPath.includes("//") ||
    /%(?:2f|5c|2e|25)/iu.test(rawPath) || /[\u0000-\u001f\u007f-\u009f]/u.test(rawPath)
  ) {
    throw new HttpError(400, "invalid_request", "The request path is invalid.");
  }
  let decoded: string;
  try {
    decoded = decodeURIComponent(rawPath);
  } catch {
    throw new HttpError(400, "invalid_request", "The request path is invalid.");
  }
  if (decoded !== new URL(request.url).pathname) {
    throw new HttpError(400, "invalid_request", "The request path is ambiguous.");
  }
  return decoded;
}

function requireLoopbackMockRoute(request: Request, allowMockAuth: boolean): void {
  const url = new URL(request.url);
  const loopback = url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]";
  if (!allowMockAuth || !loopback || url.protocol !== "http:") {
    throw new HttpError(404, "not_found", "The route was not found.");
  }
}

function requireCanonicalOrigin(request: Request, config: ReturnType<typeof runtimeConfig>): void {
  if (new URL(request.url).origin !== config.publicOrigin) {
    throw new HttpError(421, "misdirected_request", "The request was sent to an unexpected origin.");
  }
}

async function limitAnonymous(
  request: Request,
  env: Env,
  config: ReturnType<typeof runtimeConfig>,
  routeClass: string,
): Promise<void> {
  await enforceRateLimit(env.AUTH_RATE_LIMITER, await anonymousRateKey(request, config, routeClass));
}

async function limitPublicRead(
  request: Request,
  env: Env,
  config: ReturnType<typeof runtimeConfig>,
  routeClass: string,
  download = false,
): Promise<void> {
  const limiter = download ? env.DOWNLOAD_RATE_LIMITER : env.API_RATE_LIMITER;
  await enforceRateLimit(limiter, await anonymousRateKey(request, config, routeClass));
}

async function limitActor(
  env: Env,
  config: ReturnType<typeof runtimeConfig>,
  routeClass: string,
  steamId64: string,
): Promise<void> {
  await enforceRateLimit(env.API_RATE_LIMITER, await actorRateKey(config, routeClass, steamId64));
}

async function localSeed(
  request: Request,
  env: Env,
  requestId: string,
): Promise<Response> {
  const config = runtimeConfig(env);
  requireLoopbackMockRoute(request, config.allowMockAuth);
  requireMethod(request, "POST");
  const supplied = request.headers.get("X-Local-Setup-Secret") ?? "";
  const suppliedHash = await opaqueHash(config, "local-setup", supplied);
  const expectedHash = await opaqueHash(config, "local-setup", config.authSecret);
  if (supplied.length < 32 || suppliedHash !== expectedHash) {
    throw new HttpError(401, "auth_required", "The local setup secret is required.");
  }
  if (request.body !== null || Number(request.headers.get("Content-Length") ?? "0") !== 0) {
    throw new HttpError(400, "invalid_request", "The local seed request must not have a body.");
  }
  const fixture = await seedLocalFixture(env, config);
  return jsonResponse({ requestId, seeded: fixture }, 200, requestId);
}

async function route(request: Request, env: Env, requestId: string): Promise<Response> {
  const config = runtimeConfig(env);
  requireCanonicalOrigin(request, config);
  const path = safePath(request);
  const nowMs = Date.now();

  if (path === "/health") {
    requireMethod(request, "GET");
    return jsonResponse({ status: "ok" }, 200, requestId);
  }
  if (path === "/_local/seed") return localSeed(request, env, requestId);
  if (path === "/activate") {
    await limitAnonymous(request, env, config, "activation");
    if (!config.allowMockAuth) return steamActivationPage(request, requestId);
    requireLoopbackMockRoute(request, config.allowMockAuth);
    return localActivationPage(request, env, config, requestId, nowMs);
  }

  if (path === "/v1/auth/device-sessions") {
    requireMethod(request, "POST");
    await limitAnonymous(request, env, config, "device-create");
    return createDeviceSession(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/auth/device-sessions/token") {
    requireMethod(request, "POST");
    await limitAnonymous(request, env, config, "device-poll");
    return pollDeviceSession(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/auth/refresh") {
    requireMethod(request, "POST");
    await limitAnonymous(request, env, config, "refresh");
    return refreshSession(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/auth/logout") {
    requireMethod(request, "POST");
    await limitAnonymous(request, env, config, "logout");
    return logoutSession(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/auth/steam/start") {
    requireMethod(request, "POST");
    if (config.allowMockAuth) throw new HttpError(404, "not_found", "The route was not found.");
    await limitAnonymous(request, env, config, "steam-start");
    return startSteamLogin(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/auth/steam/callback") {
    requireMethod(request, "GET");
    if (config.allowMockAuth) throw new HttpError(404, "not_found", "The route was not found.");
    await limitAnonymous(request, env, config, "steam-callback");
    return steamCallback(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/auth/steam/confirm") {
    requireMethod(request, "POST");
    if (config.allowMockAuth) throw new HttpError(404, "not_found", "The route was not found.");
    await limitAnonymous(request, env, config, "steam-confirm");
    return confirmSteamLogin(request, env, config, requestId, nowMs);
  }
  if (path === "/v1/me") {
    requireMethod(request, "GET");
    const principal = await authenticate(request, env, config, "maps:read", nowMs);
    await limitActor(env, config, "account", principal.steamId64);
    return jsonResponse({ requestId, steamId64: principal.steamId64, scopes: principal.scopes }, 200, requestId);
  }

  if (path === "/v1/uploads") {
    requireMethod(request, "POST");
    const principal = await authenticate(request, env, config, "maps:upload", nowMs);
    await limitActor(env, config, "upload-reserve", principal.steamId64);
    return reserveUpload(request, env, config, principal, requestId, nowMs);
  }

  const uploadBundleMatch = /^\/v1\/uploads\/([^/]+)\/bundle$/u.exec(path);
  if (uploadBundleMatch !== null) {
    const principal = await authenticate(request, env, config, "maps:upload", nowMs);
    await limitActor(env, config, "upload-bytes", principal.steamId64);
    return putUploadBundle(
      request,
      env,
      config,
      principal,
      uploadBundleMatch[1] ?? "",
      requestId,
      nowMs,
    );
  }

  const uploadMatch = /^\/v1\/uploads\/([^/]+)$/u.exec(path);
  if (uploadMatch !== null) {
    const principal = await authenticate(request, env, config, "maps:upload", nowMs);
    await limitActor(env, config, "upload-status", principal.steamId64);
    if (request.method === "GET") {
      return getUploadStatus(request, env, config, principal, uploadMatch[1] ?? "", requestId);
    }
    if (request.method === "DELETE") {
      return cancelUpload(request, env, config, principal, uploadMatch[1] ?? "", requestId, nowMs);
    }
    throw new HttpError(405, "method_not_allowed", "The method is not allowed.", { Allow: "GET, DELETE" });
  }

  if (path === "/v1/catalog") {
    requireMethod(request, "GET");
    await limitPublicRead(request, env, config, "catalog");
    return catalogDocument(env, requestId, nowMs);
  }

  if (path === "/v1/maps") {
    requireMethod(request, "GET");
    await limitPublicRead(request, env, config, "maps");
    return listMaps(request, env, config, requestId, nowMs);
  }

  const downloadMatch = /^\/v1\/maps\/([^/]+)\/versions\/([1-9][0-9]*)\/(download|thumbnail)$/u.exec(path);
  if (downloadMatch !== null) {
    const mapId = downloadMatch[1] ?? "";
    const revision = Number(downloadMatch[2]);
    if (!isCanonicalUuid(mapId) || !Number.isSafeInteger(revision)) {
      throw new HttpError(404, "revision_not_found", "The map revision was not found.");
    }
    await limitPublicRead(request, env, config, "download", true);
    return downloadObject(
      request,
      env,
      mapId,
      revision,
      downloadMatch[3] === "download" ? "bundle" : "thumbnail",
      requestId,
    );
  }

  const versionsMatch = /^\/v1\/maps\/([^/]+)\/versions$/u.exec(path);
  if (versionsMatch !== null) {
    requireMethod(request, "GET");
    await limitPublicRead(request, env, config, "maps");
    return mapVersions(env, versionsMatch[1] ?? "", requestId);
  }

  const detailMatch = /^\/v1\/maps\/([^/]+)$/u.exec(path);
  if (detailMatch !== null) {
    requireMethod(request, "GET");
    await limitPublicRead(request, env, config, "maps");
    return mapDetail(env, detailMatch[1] ?? "", requestId);
  }

  throw new HttpError(404, "not_found", "The route was not found.");
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const requestId = crypto.randomUUID();
    const startedAt = Date.now();
    let response: Response;
    try {
      response = await route(request, env, requestId);
    } catch (error) {
      if (error instanceof HttpError) response = errorResponse(error, requestId);
      else {
        console.error(JSON.stringify({ event: "unhandled_error", requestId, name: error instanceof Error ? error.name : "unknown" }));
        response = errorResponse(new HttpError(500, "internal_error", "The request could not be completed."), requestId);
      }
    }
    if (env.ENVIRONMENT === "production") {
      response.headers.set("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }
    if (env.ENVIRONMENT !== "local" || response.status >= 400) {
      console.log(JSON.stringify({
        event: "http_request",
        requestId,
        method: request.method,
        pathname: new URL(request.url).pathname.slice(0, 256),
        status: response.status,
        durationMs: Date.now() - startedAt,
      }));
    }
    return response;
  },
  async scheduled(controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    ctx.waitUntil(cleanupExpiredState(env, controller.scheduledTime));
  },
  async queue(batch: MessageBatch<unknown>, env: Env): Promise<void> {
    for (const message of batch.messages) {
      try {
        await validateUploadMessage(env, message.body);
        message.ack();
      } catch (error) {
        console.error(JSON.stringify({
          event: "upload_validation_failed",
          messageId: message.id,
          attempt: message.attempts,
          name: error instanceof Error ? error.name : "unknown",
        }));
        message.retry({ delaySeconds: Math.min(300, 10 * Math.max(1, message.attempts)) });
      }
    }
  },
} satisfies ExportedHandler<Env>;
