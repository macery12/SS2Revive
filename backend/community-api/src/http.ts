import type { ApiErrorBody } from "@ss2revive/community-contracts";

export class HttpError extends Error {
  readonly status: number;
  readonly code: string;
  readonly headers: HeadersInit | undefined;

  constructor(status: number, code: string, message: string, headers?: HeadersInit) {
    super(message);
    this.name = "HttpError";
    this.status = status;
    this.code = code;
    this.headers = headers;
  }
}

const BASE_HEADERS: Record<string, string> = {
  "Cache-Control": "no-store",
  "Content-Security-Policy": "default-src 'none'; frame-ancestors 'none'; base-uri 'none'",
  "Referrer-Policy": "no-referrer",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
  "Cross-Origin-Opener-Policy": "same-origin",
  "X-Permitted-Cross-Domain-Policies": "none",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
};

export function responseHeaders(requestId: string, extra?: HeadersInit): Headers {
  const headers = new Headers(BASE_HEADERS);
  headers.set("X-Request-Id", requestId);
  if (extra !== undefined) {
    const supplied = new Headers(extra);
    supplied.forEach((value, key) => headers.set(key, value));
  }
  return headers;
}

export function jsonResponse(value: unknown, status: number, requestId: string, extra?: HeadersInit): Response {
  const text = JSON.stringify(value);
  const headers = responseHeaders(requestId, extra);
  headers.set("Content-Type", "application/json; charset=utf-8");
  return new Response(text, { status, headers });
}

export function emptyResponse(status: number, requestId: string, extra?: HeadersInit): Response {
  return new Response(null, { status, headers: responseHeaders(requestId, extra) });
}

export function errorResponse(error: HttpError, requestId: string): Response {
  const body: ApiErrorBody = { error: { code: error.code, message: error.message, requestId } };
  return jsonResponse(body, error.status, requestId, error.headers);
}

export async function readBoundedBody(request: Request, maximumBytes: number): Promise<Uint8Array> {
  const declared = request.headers.get("Content-Length");
  if (declared !== null) {
    if (!/^(?:0|[1-9][0-9]*)$/.test(declared) || Number(declared) > maximumBytes) {
      throw new HttpError(413, "invalid_request", "The request body is too large.");
    }
  }
  if (request.body === null) return new Uint8Array();
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const result = await reader.read();
      if (result.done) break;
      total += result.value.byteLength;
      if (total > maximumBytes) {
        await reader.cancel("body limit exceeded");
        throw new HttpError(413, "invalid_request", "The request body is too large.");
      }
      chunks.push(result.value);
    }
  } finally {
    reader.releaseLock();
  }
  const body = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    body.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return body;
}

export async function readJsonObject(request: Request, maximumBytes = 16 * 1024): Promise<Record<string, unknown>> {
  const contentType = request.headers.get("Content-Type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (contentType !== "application/json") {
    throw new HttpError(415, "invalid_request", "Content-Type must be application/json.");
  }
  const bytes = await readBoundedBody(request, maximumBytes);
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) as unknown;
  } catch {
    throw new HttpError(400, "invalid_request", "The JSON body is invalid.");
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new HttpError(400, "invalid_request", "The JSON body must be an object.");
  }
  return value as Record<string, unknown>;
}

export function requireMethod(request: Request, method: string): void {
  if (request.method !== method) {
    throw new HttpError(405, "method_not_allowed", "The method is not allowed.", { Allow: method });
  }
}
