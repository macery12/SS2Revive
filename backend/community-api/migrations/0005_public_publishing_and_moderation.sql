ALTER TABLE maps ADD COLUMN owner_steam_id64 TEXT REFERENCES users(steam_id64);
ALTER TABLE maps ADD COLUMN unpublished_at_ms INTEGER;
ALTER TABLE maps ADD COLUMN archived_at_ms INTEGER;
ALTER TABLE maps ADD COLUMN moderation_reason TEXT;

UPDATE maps
   SET owner_steam_id64 = (
     SELECT u.steam_id64
       FROM map_uploads u
      WHERE u.map_id = maps.id AND u.status = 'published'
      ORDER BY u.completed_at DESC
      LIMIT 1
   )
 WHERE owner_steam_id64 IS NULL;

CREATE INDEX idx_maps_owner_status
  ON maps(owner_steam_id64, status, updated_at_ms DESC);

CREATE TABLE upload_usage_daily (
  steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  day_utc TEXT NOT NULL,
  reservations INTEGER NOT NULL DEFAULT 0 CHECK (reservations >= 0),
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (steam_id64, day_utc)
) STRICT;

CREATE TABLE map_reports (
  id TEXT PRIMARY KEY NOT NULL,
  map_id TEXT NOT NULL REFERENCES maps(id),
  reporter_steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  reason TEXT NOT NULL CHECK (
    reason IN ('BROKEN', 'NSFW_CONTENT', 'COPY', 'LEVEL_DESIGN_COPYRIGHT', 'FARMING', 'OTHER')
  ),
  note TEXT NOT NULL DEFAULT '' CHECK (length(note) <= 512),
  status TEXT NOT NULL CHECK (status IN ('open', 'resolved', 'dismissed')),
  created_at INTEGER NOT NULL,
  resolved_at INTEGER,
  resolved_by_steam_id64 TEXT REFERENCES users(steam_id64),
  resolution TEXT,
  UNIQUE (map_id, reporter_steam_id64)
) STRICT;
CREATE INDEX idx_map_reports_status_created ON map_reports(status, created_at ASC);
CREATE INDEX idx_map_reports_map_status ON map_reports(map_id, status);

CREATE TABLE moderation_events (
  id TEXT PRIMARY KEY NOT NULL,
  map_id TEXT NOT NULL REFERENCES maps(id),
  maintainer_steam_id64 TEXT NOT NULL REFERENCES users(steam_id64),
  action TEXT NOT NULL CHECK (action IN ('takedown', 'restore')),
  reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 256),
  created_at INTEGER NOT NULL
) STRICT;
CREATE INDEX idx_moderation_events_map_created ON moderation_events(map_id, created_at DESC);
