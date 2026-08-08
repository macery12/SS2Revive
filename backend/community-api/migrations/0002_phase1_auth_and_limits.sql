ALTER TABLE auth_sessions ADD COLUMN source_device_session_id TEXT;
CREATE UNIQUE INDEX idx_auth_sessions_source_device
  ON auth_sessions(source_device_session_id)
  WHERE source_device_session_id IS NOT NULL;

ALTER TABLE device_auth_sessions ADD COLUMN last_polled_at INTEGER;

CREATE UNIQUE INDEX idx_refresh_family_one_active
  ON refresh_tokens(family_id)
  WHERE status = 'active';

CREATE TRIGGER trg_auth_session_requires_approved_device
BEFORE INSERT ON auth_sessions
WHEN NEW.source_device_session_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM device_auth_sessions d
    WHERE d.id = NEW.source_device_session_id
      AND d.status = 'approved'
      AND d.expires_at > NEW.created_at
  )
BEGIN
  SELECT RAISE(ABORT, 'device_session_not_approved');
END;

CREATE TABLE steam_openid_sessions (
  id TEXT PRIMARY KEY NOT NULL,
  device_session_id TEXT NOT NULL UNIQUE REFERENCES device_auth_sessions(id) ON DELETE CASCADE,
  state_hash TEXT NOT NULL UNIQUE,
  status TEXT NOT NULL CHECK (status IN ('pending', 'verified', 'confirmed', 'failed')),
  return_to TEXT NOT NULL,
  steam_id64 TEXT,
  response_nonce_hash TEXT UNIQUE,
  confirm_token_hash TEXT,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  verified_at INTEGER,
  confirmed_at INTEGER
) STRICT;
CREATE INDEX idx_steam_openid_expiry ON steam_openid_sessions(status, expires_at);

CREATE TABLE download_usage_daily (
  steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  day_utc TEXT NOT NULL,
  bytes_reserved INTEGER NOT NULL DEFAULT 0 CHECK (bytes_reserved >= 0),
  download_starts INTEGER NOT NULL DEFAULT 0 CHECK (download_starts >= 0),
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (steam_id64, day_utc)
) STRICT;

CREATE TABLE download_leases (
  id TEXT PRIMARY KEY NOT NULL,
  steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  day_utc TEXT NOT NULL,
  bytes_reserved INTEGER NOT NULL CHECK (bytes_reserved > 0),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
) STRICT;
CREATE INDEX idx_download_leases_actor_expiry
  ON download_leases(steam_id64, expires_at);
CREATE INDEX idx_download_leases_expiry ON download_leases(expires_at);
