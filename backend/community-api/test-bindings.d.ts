// Wrangler generates the deployed bindings from the local and production-example configs. These
// two bindings exist only in Vitest/Miniflare, so they cannot be inferred from either config.
interface TestOnlyBindings {
  LOCAL_AUTH_SECRET: string;
  TEST_MIGRATIONS: D1Migration[];
}

declare namespace Cloudflare {
  interface Env extends TestOnlyBindings {}
}

interface Env extends TestOnlyBindings {}
