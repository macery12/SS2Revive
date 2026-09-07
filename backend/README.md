# SS2Revive community backend

This pnpm workspace contains the Cloudflare Worker that powers SS2Revive's public community-map
service. It provides public catalogue and download routes, Steam-authenticated publishing and
owner controls, maintainer moderation, and the shared bundle contracts used by the Worker and its
tests.

The game does not receive D1 credentials, R2 credentials, or a direct public-bucket URL. It talks
only to the Worker API.

## Workspace

| Path | Purpose |
| --- | --- |
| `community-api` | Worker routes, Steam device authentication, D1/R2/Queue integration, migrations, and tests |
| `community-contracts` | Shared API types, level-code handling, and bounded `.ss2level` validation |
| `scripts/secret-scan.mjs` | Repository secret scan used by the local security workflow |

## Quickstart

Requirements are Node.js 24 and pnpm 11. From this `backend` directory:

```powershell
pnpm install --frozen-lockfile
Copy-Item community-api\.dev.vars.example community-api\.dev.vars
pnpm --filter @ss2revive/community-api db:migrate:local
pnpm --filter @ss2revive/community-api dev
```

Replace the placeholder in `.dev.vars` with a base64-encoded random 32-byte secret before starting
the Worker. Detailed local setup, seeding, authentication, and route documentation is in
[`community-api/README.md`](community-api/README.md).

## Development and verification

From this directory:

```powershell
pnpm check
pnpm test
pnpm build
pnpm run security:secrets
pnpm audit --audit-level high
```

`pnpm build` is a Wrangler dry run; it writes a local bundle and does not deploy. Production
bindings, secrets, migrations, and deployment remain maintainer-managed.

## Security boundary

- Steam OpenID proves control of a Steam identity. The game also checks that the browser identity
  matches the Steam account running the client.
- Access tokens are short-lived and scoped. Refresh tokens rotate, and replay revokes their token
  family.
- Uploads reserve bounded private quarantine space and idempotent D1-backed byte/storage quota,
  are checksum-checked, and pass the shared current-format validator before publication. Terminal
  rejection, cancellation, and expiry refund the reservation.
- D1 stores ownership, publication, quota, session, report, and moderation state. R2 stores private
  quarantine and approved immutable objects. Queue consumers validate uploads; scheduled cleanup
  releases expired state.
- Public downloads are served through the Worker only after current D1 publication state and R2
  metadata agree, so unpublish and takedown decisions remain authoritative. Owner archive removes
  stored revisions and leaves a tombstone that requires a newer revision before republishing.
- Maintainer access is a scoped authenticated capability tied to configured SteamID64 values, not
  an obscure URL.

Do not commit `.dev.vars`, production Wrangler configuration, Cloudflare state, tokens, or game
assets. See the repository [license](../LICENSE) and [security reporting guidance](../README.md#report-a-security-issue).
