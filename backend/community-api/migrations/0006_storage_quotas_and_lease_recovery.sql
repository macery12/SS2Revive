-- Storage accounting, validation-lease recovery, and index/retention gaps found in the
-- 2026-08-10 security review.

-- The lifetime map-count quota only applied to new map identifiers, so republishing revisions of
-- an existing map consumed R2 without bound. Charge every accepted revision against a daily byte
-- allowance as well as the reservation count.
ALTER TABLE upload_usage_daily ADD COLUMN bytes_published INTEGER NOT NULL DEFAULT 0
  CHECK (bytes_published >= 0);

-- Running total of approved bytes an account currently has retained, maintained as revisions are
-- published and released so a per-account ceiling can be enforced without scanning R2.
CREATE TABLE account_storage (
  steam_id64 TEXT PRIMARY KEY NOT NULL REFERENCES users(steam_id64),
  retained_bytes INTEGER NOT NULL DEFAULT 0 CHECK (retained_bytes >= 0),
  updated_at INTEGER NOT NULL
) STRICT;

-- A validator killed between claiming its lease and completing left the row stranded in
-- 'validating' forever: the claim requires status 'uploaded', and the maintenance sweep only
-- looked at 'reserved'/'uploaded'. Record when the lease expires so it can be reclaimed safely.
ALTER TABLE map_uploads ADD COLUMN validation_lease_expires_at INTEGER;

CREATE INDEX idx_map_uploads_validating_lease
  ON map_uploads(status, validation_lease_expires_at);

-- The per-account daily report count filtered on reporter_steam_id64, but every existing index
-- leads with map_id or status, so each report scanned the whole table.
CREATE INDEX idx_map_reports_reporter_created
  ON map_reports(reporter_steam_id64, created_at);

-- Retention for moderation history and resolved reports, which previously had no policy at all.
CREATE INDEX idx_map_reports_resolved_at ON map_reports(resolved_at);

-- The device-link confirmation page asked the user to approve "the code you entered" without ever
-- showing which code that was, so an attacker could send a victim a prefilled activation link on
-- the real domain and have them approve the attacker's device. Carry the code on the short-lived
-- OpenID session so the confirmation page can display it for comparison (RFC 8628 section 5.4).
ALTER TABLE steam_openid_sessions ADD COLUMN user_code TEXT;

-- Seed the running total from what each account already has stored, so the new ceiling reflects
-- reality instead of granting every existing publisher a fresh allowance.
INSERT INTO account_storage (steam_id64, retained_bytes, updated_at)
SELECT m.owner_steam_id64,
       COALESCE(SUM(v.size_bytes + COALESCE(v.thumbnail_size_bytes, 0)), 0),
       CAST(strftime('%s', 'now') AS INTEGER) * 1000
  FROM maps m JOIN map_versions v ON v.map_id = m.id
 WHERE m.owner_steam_id64 IS NOT NULL
 GROUP BY m.owner_steam_id64
ON CONFLICT(steam_id64) DO NOTHING;

-- The now-unused download_leases and download_usage_daily tables are dropped in migration 0007,
-- after the Worker that still references them has been replaced. Dropping them here would break
-- the currently deployed cron in the window between migrating and deploying.
