-- Per-upload storage charges are recorded on the upload itself. Moving from `none` to `reserved`
-- is the single atomic charging point; retries observe the existing state instead of incrementing
-- the counters again. Terminal rejection/expiry/cancellation moves back to `none`, and the refund
-- trigger restores both counters in the same D1 statement as the terminal state change.
ALTER TABLE map_uploads ADD COLUMN quota_state TEXT NOT NULL DEFAULT 'none'
  CHECK (quota_state IN ('none', 'reserved', 'published'));
ALTER TABLE map_uploads ADD COLUMN quota_bytes_charged INTEGER NOT NULL DEFAULT 0
  CHECK (quota_bytes_charged >= 0);
ALTER TABLE map_uploads ADD COLUMN quota_day_utc TEXT;
ALTER TABLE map_uploads ADD COLUMN quota_daily_limit INTEGER;
ALTER TABLE map_uploads ADD COLUMN quota_retained_limit INTEGER;
ALTER TABLE map_uploads ADD COLUMN quota_charged_at INTEGER;

-- Distinguish a durable owner-deletion tombstone from the legacy use of `archived` for a
-- maintainer takedown. The unique operation id also lets every statement in the archive batch
-- prove it belongs to the UPDATE that won, instead of relying on millisecond timestamp equality.
ALTER TABLE maps ADD COLUMN archive_operation_id TEXT
  CHECK (archive_operation_id IS NULL OR length(archive_operation_id) = 36);
-- A moderation batch uses the event UUID as its compare-and-swap witness. Statements after the
-- map transition can then prove that transition won, so a concurrent stale request cannot update
-- version/report state or record an event for a moderation action that never occurred.
ALTER TABLE maps ADD COLUMN moderation_operation_id TEXT
  CHECK (moderation_operation_id IS NULL OR length(moderation_operation_id) = 36);

-- Keep each validation in a separate trigger. Besides making each invariant independently
-- auditable, this avoids nested CASE ... END expressions inside a CREATE TRIGGER body; D1's
-- remote migration statement parser can otherwise mistake the CASE terminator for the trigger
-- terminator even though SQLite itself accepts the SQL.
CREATE TRIGGER trg_map_upload_charge_validate_fields
BEFORE UPDATE OF quota_state ON map_uploads
WHEN OLD.quota_state = 'none' AND NEW.quota_state = 'reserved'
  AND (NEW.quota_bytes_charged <= 0
       OR NEW.quota_day_utc IS NULL
       OR NEW.quota_daily_limit IS NULL
       OR NEW.quota_retained_limit IS NULL
       OR NEW.quota_charged_at IS NULL)
BEGIN
  SELECT RAISE(ABORT, 'invalid_upload_storage_charge');
END;

CREATE TRIGGER trg_map_upload_charge_validate_daily
BEFORE UPDATE OF quota_state ON map_uploads
WHEN OLD.quota_state = 'none' AND NEW.quota_state = 'reserved'
  AND COALESCE((
        SELECT bytes_published FROM upload_usage_daily
         WHERE steam_id64 = NEW.steam_id64 AND day_utc = NEW.quota_day_utc
      ), 0) + NEW.quota_bytes_charged > NEW.quota_daily_limit
BEGIN
  SELECT RAISE(ABORT, 'daily_upload_bytes_reached');
END;

CREATE TRIGGER trg_map_upload_charge_validate_retained
BEFORE UPDATE OF quota_state ON map_uploads
WHEN OLD.quota_state = 'none' AND NEW.quota_state = 'reserved'
  AND COALESCE((
        SELECT retained_bytes FROM account_storage
         WHERE steam_id64 = NEW.steam_id64
      ), 0) + NEW.quota_bytes_charged > NEW.quota_retained_limit
BEGIN
  SELECT RAISE(ABORT, 'storage_quota_reached');
END;

CREATE TRIGGER trg_map_upload_charge_apply
AFTER UPDATE OF quota_state ON map_uploads
WHEN OLD.quota_state = 'none' AND NEW.quota_state = 'reserved'
BEGIN
  INSERT INTO upload_usage_daily
    (steam_id64, day_utc, reservations, bytes_published, updated_at)
  VALUES
    (NEW.steam_id64, NEW.quota_day_utc, 0, NEW.quota_bytes_charged, NEW.quota_charged_at)
  ON CONFLICT(steam_id64, day_utc) DO UPDATE SET
    bytes_published = upload_usage_daily.bytes_published + excluded.bytes_published,
    updated_at = excluded.updated_at;

  INSERT INTO account_storage (steam_id64, retained_bytes, updated_at)
  VALUES (NEW.steam_id64, NEW.quota_bytes_charged, NEW.quota_charged_at)
  ON CONFLICT(steam_id64) DO UPDATE SET
    retained_bytes = account_storage.retained_bytes + excluded.retained_bytes,
    updated_at = excluded.updated_at;
END;

CREATE TRIGGER trg_map_upload_charge_refund
AFTER UPDATE OF quota_state ON map_uploads
WHEN OLD.quota_state = 'reserved' AND NEW.quota_state = 'none'
BEGIN
  UPDATE upload_usage_daily
     SET bytes_published = MAX(0, bytes_published - OLD.quota_bytes_charged),
         updated_at = COALESCE(NEW.completed_at, OLD.quota_charged_at, updated_at)
   WHERE steam_id64 = OLD.steam_id64 AND day_utc = OLD.quota_day_utc;

  UPDATE account_storage
     SET retained_bytes = MAX(0, retained_bytes - OLD.quota_bytes_charged),
         updated_at = COALESCE(NEW.completed_at, OLD.quota_charged_at, updated_at)
   WHERE steam_id64 = OLD.steam_id64;
END;

-- The orphan collector stores an opaque R2 cursor so each bounded hourly pass eventually visits
-- the entire approved prefix instead of repeatedly inspecting only its first page.
CREATE TABLE maintenance_state (
  key TEXT PRIMARY KEY NOT NULL,
  value TEXT NOT NULL,
  updated_at INTEGER NOT NULL
) STRICT;
