PRAGMA foreign_keys = ON;

CREATE TABLE users (
  steam_id64 TEXT PRIMARY KEY NOT NULL,
  status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'deleted')),
  created_at INTEGER NOT NULL,
  last_login_at INTEGER NOT NULL
) STRICT;

CREATE TABLE auth_sessions (
  id TEXT PRIMARY KEY NOT NULL,
  steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  status TEXT NOT NULL CHECK (status IN ('active', 'revoked')),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  revoked_at INTEGER
) STRICT;
CREATE INDEX idx_auth_sessions_user_status ON auth_sessions(steam_id64, status);

CREATE TABLE device_auth_sessions (
  id TEXT PRIMARY KEY NOT NULL,
  device_secret_hash TEXT NOT NULL UNIQUE,
  user_code_hash TEXT NOT NULL UNIQUE,
  steam_id64 TEXT REFERENCES users(steam_id64),
  status TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'denied', 'consumed')),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  approved_at INTEGER,
  consumed_at INTEGER
) STRICT;
CREATE INDEX idx_device_auth_expiry ON device_auth_sessions(status, expires_at);

CREATE TABLE refresh_token_families (
  id TEXT PRIMARY KEY NOT NULL,
  session_id TEXT NOT NULL REFERENCES auth_sessions(id),
  steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  revoked_at INTEGER,
  revoke_reason TEXT
) STRICT;
CREATE INDEX idx_refresh_families_session ON refresh_token_families(session_id);

CREATE TABLE refresh_tokens (
  id TEXT PRIMARY KEY NOT NULL,
  family_id TEXT NOT NULL REFERENCES refresh_token_families(id),
  token_hash TEXT NOT NULL UNIQUE,
  status TEXT NOT NULL CHECK (status IN ('active', 'rotated', 'revoked')),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  used_at INTEGER,
  rotated_to_id TEXT
) STRICT;
CREATE INDEX idx_refresh_tokens_family_status ON refresh_tokens(family_id, status);

CREATE TABLE maps (
  id TEXT PRIMARY KEY NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('published', 'unpublished', 'archived')),
  current_revision INTEGER NOT NULL CHECK (current_revision >= 1),
  title TEXT NOT NULL,
  title_sort TEXT NOT NULL,
  description TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL CHECK (created_at_ms >= 0),
  updated_at_ms INTEGER NOT NULL CHECK (updated_at_ms >= created_at_ms)
) STRICT;
CREATE INDEX idx_maps_published_updated ON maps(status, updated_at_ms DESC, id ASC);
CREATE INDEX idx_maps_published_created ON maps(status, created_at_ms DESC, id ASC);
CREATE INDEX idx_maps_published_title ON maps(status, title_sort ASC, id ASC);

CREATE TABLE map_versions (
  map_id TEXT NOT NULL REFERENCES maps(id),
  revision INTEGER NOT NULL CHECK (revision >= 1),
  status TEXT NOT NULL CHECK (status IN ('published', 'unpublished', 'rejected')),
  code TEXT NOT NULL,
  creator_ids_json TEXT NOT NULL,
  tags_json TEXT NOT NULL,
  configurations_json TEXT NOT NULL,
  validations_json TEXT NOT NULL,
  player_counts_csv TEXT NOT NULL,
  client_version INTEGER NOT NULL CHECK (client_version = 29),
  map_format_version INTEGER NOT NULL CHECK (map_format_version = 29),
  minimum_revive_version TEXT NOT NULL,
  revive_version TEXT,
  size_bytes INTEGER NOT NULL CHECK (size_bytes BETWEEN 1 AND 25165824),
  sha256 TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  bundle_key TEXT NOT NULL UNIQUE,
  thumbnail_size_bytes INTEGER,
  thumbnail_sha256 TEXT,
  thumbnail_key TEXT UNIQUE,
  created_at_ms INTEGER NOT NULL CHECK (created_at_ms >= 0),
  CHECK (
    (thumbnail_size_bytes IS NULL AND thumbnail_sha256 IS NULL AND thumbnail_key IS NULL)
    OR
    (thumbnail_size_bytes BETWEEN 1 AND 8388608 AND
     thumbnail_sha256 IS NOT NULL AND length(thumbnail_sha256) = 64 AND
     thumbnail_sha256 NOT GLOB '*[^0-9a-f]*' AND thumbnail_key IS NOT NULL)
  ),
  PRIMARY KEY (map_id, revision)
) STRICT;
CREATE INDEX idx_map_versions_published ON map_versions(map_id, status, revision DESC);

CREATE TABLE map_tags (
  map_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  tag TEXT NOT NULL,
  PRIMARY KEY (map_id, revision, tag),
  FOREIGN KEY (map_id, revision) REFERENCES map_versions(map_id, revision) ON DELETE CASCADE
) STRICT;
CREATE INDEX idx_map_tags_lookup ON map_tags(tag, map_id, revision);
