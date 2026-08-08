import type { RuntimeConfig } from "./config";
import { HttpError } from "./http";

const LEASE_LIFETIME_MS = 15 * 60 * 1000;

export interface DownloadLease {
  id: string;
  steamId64: string;
  dayUtc: string;
  bytes: number;
}

function utcDay(nowMs: number): string {
  return new Date(nowMs).toISOString().slice(0, 10);
}

export async function reserveDownload(
  env: Env,
  config: RuntimeConfig,
  steamId64: string,
  bytes: number,
  nowMs: number,
): Promise<DownloadLease> {
  if (!Number.isSafeInteger(bytes) || bytes < 1 || bytes > config.downloadDailyBytes) {
    throw new HttpError(429, "quota_exceeded", "The daily download quota is exhausted.", {
      "Retry-After": "3600",
    });
  }
  const id = crypto.randomUUID();
  const dayUtc = utcDay(nowMs);
  const results = await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO download_usage_daily
         (steam_id64, day_utc, bytes_reserved, download_starts, updated_at)
       VALUES (?, ?, 0, 0, ?)
       ON CONFLICT(steam_id64, day_utc) DO NOTHING`,
    ).bind(steamId64, dayUtc, nowMs),
    env.DB.prepare(
      `INSERT INTO download_leases
         (id, steam_id64, day_utc, bytes_reserved, created_at, expires_at)
       SELECT ?, ?, ?, ?, ?, ?
       WHERE (SELECT bytes_reserved FROM download_usage_daily
              WHERE steam_id64 = ? AND day_utc = ?) <= ? - ?
         AND (SELECT COUNT(*) FROM download_leases
              WHERE steam_id64 = ? AND expires_at > ?) < ?
       RETURNING id`,
    ).bind(
      id, steamId64, dayUtc, bytes, nowMs, nowMs + LEASE_LIFETIME_MS,
      steamId64, dayUtc, config.downloadDailyBytes, bytes,
      steamId64, nowMs, config.downloadConcurrency,
    ),
    env.DB.prepare(
      `UPDATE download_usage_daily
          SET bytes_reserved = bytes_reserved + ?, download_starts = download_starts + 1,
              updated_at = ?
        WHERE steam_id64 = ? AND day_utc = ?
          AND EXISTS (SELECT 1 FROM download_leases WHERE id = ?)`,
    ).bind(bytes, nowMs, steamId64, dayUtc, id),
  ]);
  const inserted = results[1]?.results as Array<{ id?: unknown }> | undefined;
  if (inserted?.[0]?.id === id) return { id, steamId64, dayUtc, bytes };

  const state = await env.DB.prepare(
    `SELECT u.bytes_reserved,
            (SELECT COUNT(*) FROM download_leases l
              WHERE l.steam_id64 = u.steam_id64 AND l.expires_at > ?) AS active_leases
       FROM download_usage_daily u WHERE u.steam_id64 = ? AND u.day_utc = ?`,
  ).bind(nowMs, steamId64, dayUtc).first<{ bytes_reserved: number; active_leases: number }>();
  if (state !== null && state.active_leases >= config.downloadConcurrency) {
    throw new HttpError(429, "download_concurrency_exceeded", "Too many downloads are already active.", {
      "Retry-After": "30",
    });
  }
  throw new HttpError(429, "quota_exceeded", "The daily download quota is exhausted.", {
    "Retry-After": "3600",
  });
}

export async function completeDownload(env: Env, lease: DownloadLease): Promise<void> {
  await env.DB.prepare(`DELETE FROM download_leases WHERE id = ? AND steam_id64 = ?`)
    .bind(lease.id, lease.steamId64).run();
}

export async function refundDownload(env: Env, lease: DownloadLease, nowMs: number): Promise<void> {
  await env.DB.batch([
    env.DB.prepare(
      `UPDATE download_usage_daily
          SET bytes_reserved = MAX(0, bytes_reserved -
                COALESCE((SELECT bytes_reserved FROM download_leases WHERE id = ?), 0)),
              download_starts = MAX(0, download_starts -
                CASE WHEN EXISTS (SELECT 1 FROM download_leases WHERE id = ?) THEN 1 ELSE 0 END),
              updated_at = ?
        WHERE steam_id64 = ? AND day_utc = ?`,
    ).bind(lease.id, lease.id, nowMs, lease.steamId64, lease.dayUtc),
    env.DB.prepare(`DELETE FROM download_leases WHERE id = ? AND steam_id64 = ?`)
      .bind(lease.id, lease.steamId64),
  ]);
}
