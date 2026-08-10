CREATE TABLE map_uploads (
  id TEXT PRIMARY KEY NOT NULL,
  steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  status TEXT NOT NULL CHECK (
    status IN ('reserved', 'uploaded', 'validating', 'published', 'rejected', 'cancelled', 'expired')
  ),
  expected_size_bytes INTEGER NOT NULL CHECK (expected_size_bytes BETWEEN 1 AND 25165824),
  expected_sha256 TEXT NOT NULL CHECK (
    length(expected_sha256) = 64 AND expected_sha256 NOT GLOB '*[^0-9a-f]*'
  ),
  quarantine_key TEXT NOT NULL UNIQUE,
  map_id TEXT,
  revision INTEGER CHECK (revision IS NULL OR revision >= 1),
  error_code TEXT,
  error_message TEXT,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  uploaded_at INTEGER,
  completed_at INTEGER,
  CHECK (expires_at > created_at),
  CHECK (
    (status = 'published' AND map_id IS NOT NULL AND revision IS NOT NULL)
    OR status != 'published'
  )
) STRICT;

CREATE INDEX idx_map_uploads_owner_created
  ON map_uploads(steam_id64, created_at DESC);
CREATE INDEX idx_map_uploads_status_expiry
  ON map_uploads(status, expires_at);

