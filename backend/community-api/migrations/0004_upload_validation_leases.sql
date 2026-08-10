ALTER TABLE map_uploads ADD COLUMN validation_lease TEXT;
ALTER TABLE map_uploads ADD COLUMN validation_started_at INTEGER;

CREATE INDEX idx_map_uploads_validation_lease
  ON map_uploads(id, status, validation_lease);
