-- The per-account download budget in download-quota.ts was implemented, tested, and never wired
-- into any request path: nothing outside the test suite ever called reserveDownload, while
-- config.ts still hard-failed startup when its variables were malformed. Downloads are
-- intentionally anonymous, so the tables are removed rather than left as an inert control that
-- implies a protection the service does not actually have.
--
-- Kept separate from 0006 so the destructive step is reviewable on its own. Note that
-- `wrangler d1 migrations apply` runs every pending migration in one invocation, so this does not
-- by itself create a gap between schema and code: deploy the Worker immediately after migrating.
DROP TABLE IF EXISTS download_leases;
DROP TABLE IF EXISTS download_usage_daily;
