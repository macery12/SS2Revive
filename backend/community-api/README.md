# SS2Revive Community API

This package is the Cloudflare Worker behind SS2Revive's community-map browser. It serves public
map metadata and downloads, authenticates creators through a browser-assisted Steam device flow,
quarantines and validates uploads, and exposes owner, report, and maintainer-moderation controls.

The commands below use Wrangler's local D1, R2, and Queue simulation. They do not deploy or modify
the production service.

## Requirements

- Node.js 24
- pnpm 11
- PowerShell 7 or Windows PowerShell 5.1 for the examples

From `backend`, install the locked workspace dependencies:

```powershell
pnpm install --frozen-lockfile
```

## One-time local configuration

Create the ignored `.dev.vars` file and replace its placeholder with a unique base64-encoded
32-byte secret:

```powershell
Copy-Item community-api\.dev.vars.example community-api\.dev.vars
$secretBytes = [byte[]]::new(32)
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($secretBytes)
$rng.Dispose()
$localSecret = [Convert]::ToBase64String($secretBytes)
(Get-Content community-api\.dev.vars) -replace '<generate-a-local-secret-at-least-32-characters-long>', $localSecret |
  Set-Content community-api\.dev.vars
pnpm --filter @ss2revive/community-api db:migrate:local
```

Wrangler keeps local state under `community-api/.wrangler/`; both it and `.dev.vars` are ignored.

## Run and seed locally

Start the Worker on loopback from `backend`:

```powershell
pnpm --filter @ss2revive/community-api dev
```

In another PowerShell window, seed the deterministic test map:

```powershell
$api = 'http://127.0.0.1:8787'
$localSecret = (Get-Content community-api\.dev.vars | Where-Object { $_ -like 'LOCAL_AUTH_SECRET=*' }).Substring(18)
Invoke-RestMethod -Method Post -Uri "$api/_local/seed" -Headers @{ 'X-Local-Setup-Secret' = $localSecret }
```

The seed route exists only when `ENVIRONMENT=local` and `ALLOW_MOCK_AUTH=true`. It is loopback-only
and requires the local setup secret.

## Exercise authentication

Local mode preserves the production device-session and token contracts but approves the configured
mock SteamID64 at `/activate` instead of contacting Steam:

```powershell
$device = Invoke-RestMethod -Method Post -Uri "$api/v1/auth/device-sessions" `
  -ContentType 'application/json' -Body '{}'
Start-Process "$api$($device.activationPath)"
```

Approve the code in the browser, then exchange the one-time device secret:

```powershell
$tokenBody = @{ deviceSessionId = $device.deviceSessionId; deviceSecret = $device.deviceSecret } |
  ConvertTo-Json -Compress
$tokens = Invoke-RestMethod -Method Post -Uri "$api/v1/auth/device-sessions/token" `
  -ContentType 'application/json' -Body $tokenBody
$headers = @{ Authorization = "Bearer $($tokens.accessToken)" }
Invoke-RestMethod -Uri "$api/v1/me" -Headers $headers
```

Production mode replaces the local activation page with Steam OpenID start, callback, and explicit
confirmation routes. A token poll before approval returns `authorization_pending`; polling too
quickly is rate-limited. Refresh tokens rotate on every use, and replay revokes the token family.

## Implemented API

| Method | Route | Authorization |
| --- | --- | --- |
| `GET` | `/health` | None |
| `POST` | `/_local/seed` | Local setup secret; local mode only |
| `GET`, `POST` | `/activate` | Device code; local mock or production Steam flow |
| `POST` | `/v1/auth/device-sessions` | None |
| `POST` | `/v1/auth/device-sessions/token` | Device secret |
| `POST` | `/v1/auth/refresh` | Refresh token |
| `POST` | `/v1/auth/logout` | Refresh token |
| `POST` | `/v1/auth/steam/start` | Production browser flow |
| `GET` | `/v1/auth/steam/callback` | Steam OpenID assertion |
| `POST` | `/v1/auth/steam/confirm` | Production browser flow |
| `GET` | `/v1/me` | `maps:read` |
| `POST` | `/v1/uploads` | `maps:upload` |
| `PUT` | `/v1/uploads/{uploadId}/bundle` | `maps:upload`; reserved owner only |
| `GET`, `DELETE` | `/v1/uploads/{uploadId}` | `maps:upload`; reserved owner only |
| `GET` | `/v1/catalog` | None |
| `GET` | `/v1/maps` | None |
| `GET`, `DELETE` | `/v1/maps/{mapId}` | Public read; owner `maps:manage` to archive |
| `POST` | `/v1/maps/{mapId}/unpublish` | Owner `maps:manage` |
| `POST` | `/v1/maps/{mapId}/reports` | `maps:report` |
| `GET` | `/v1/maps/{mapId}/versions` | None |
| `GET`, `HEAD` | `/v1/maps/{mapId}/versions/{revision}/download` | None; published maps only |
| `GET`, `HEAD` | `/v1/maps/{mapId}/versions/{revision}/thumbnail` | None; published maps only |
| `GET` | `/v1/moderation/reports` | Maintainer `maps:moderate` |
| `POST` | `/v1/moderation/maps/{mapId}/takedown` | Maintainer `maps:moderate` |
| `POST` | `/v1/moderation/maps/{mapId}/restore` | Maintainer `maps:moderate` |

## Publication and moderation flow

1. An authenticated creator reserves an upload with its exact size and SHA-256.
2. The bundle is written to a private R2 quarantine key only when request length and checksum match
   the reservation.
3. A Queue consumer claims a validation lease, parses the bounded bundle, verifies its inner
   checksums and current game format, and confirms the authenticated Steam account is a creator.
4. Approved content is copied to content-addressed R2 keys and D1 publication state is updated.
   Failed validation rejects the upload and removes quarantine data.
5. Owner unpublish/archive actions and maintainer takedowns invalidate public metadata/cache state.
   Maintainer takedown applies only to currently published maps, and ordinary republishing cannot
   clear an active moderation reason.

Quota reservation is idempotent and D1-backed. Terminal rejection, cancellation, or expiry refunds
daily-byte and retained-storage reservations. Owner archive releases every stored version and R2
object, refunds retained bytes, and leaves a tombstone: the same revision cannot reappear, but the
owner can publish a newer revision. Archived maps do not consume the active map-count quota.

The service also enforces configured daily reservation, daily-byte, retained-storage, and map-count
quotas, a three-active-upload ceiling, per-route actor/IP rate limits, bounded catalogue responses,
and scheduled cleanup of expired sessions, leases, uploads, and retained moderation records. The
scheduled R2 orphan sweep persists its pagination cursor and scans bounded pages across runs.

## Safety boundary

- R2 is not a public trust boundary. Clients receive Worker routes, never bucket credentials or raw
  private object keys.
- Every public download rechecks current D1 publication state and R2 size/checksum metadata before
  serving bytes. Whole-object body caching does not bypass that authorization lookup.
- Steam OpenID proves the browser's Steam identity; the client separately requires it to match the
  account running the game. It does not expose or collect a Steam password.
- Publisher input remains untrusted after authentication. Bundle size, structure, nesting,
  metadata, checksums, ownership, revision, quota, and moderation state are validated server-side.
- Maintainer routes require both the `maps:moderate` scope and membership in the configured
  `MAINTAINER_STEAM_IDS` set.
- Local mock auth and seeding fail closed outside loopback local mode.

Do not run `wrangler deploy`, add `remote: true`, or use `--remote` as part of local development.
Production D1/R2/Queue resources, secrets, migrations, and deployment are maintainer-managed.

## Development and verification

From `backend`:

```powershell
pnpm check
pnpm test
pnpm test:coverage
pnpm build
pnpm run security:secrets
pnpm audit --audit-level high
```

`pnpm build` uses `wrangler deploy --dry-run`; it bundles to a local `dist` directory and does not
deploy.

## License

SS2Revive is available under the repository's [MIT License](../../LICENSE). Contributions should
keep credentials, Cloudflare state, extracted game content, and third-party game assets out of the
repository.
