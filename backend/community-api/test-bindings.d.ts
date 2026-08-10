// Wrangler generates the platform bindings in worker-configuration.d.ts. These bindings exist
// only in Vitest/Miniflare or as Worker secrets, so they cannot be inferred from wrangler.jsonc.
interface TestOnlyBindings {
  LOCAL_AUTH_SECRET: string;
  AUTH_SIGNING_SECRET: string;
  TEST_MIGRATIONS: D1Migration[];
}

declare namespace Cloudflare {
  interface Env extends TestOnlyBindings {}
}

interface Env extends TestOnlyBindings {}
