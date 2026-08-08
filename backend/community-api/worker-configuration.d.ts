declare namespace Cloudflare {
  interface Env {
    DB: D1Database;
    MAP_BUCKET: R2Bucket;
    VALIDATION_QUEUE: Queue<never>;
    ENVIRONMENT: string;
    ALLOW_MOCK_AUTH: string;
    AUTH_ISSUER: string;
    AUTH_AUDIENCE: string;
    LOCAL_AUTH_SECRET: string;
    AUTH_SIGNING_SECRET: string;
    MOCK_STEAM_ID64: string;
    PUBLIC_ORIGIN: string;
    DOWNLOAD_DAILY_BYTES: string;
    DOWNLOAD_CONCURRENCY: string;
    AUTH_RATE_LIMITER: RateLimit;
    API_RATE_LIMITER: RateLimit;
    DOWNLOAD_RATE_LIMITER: RateLimit;
    TEST_MIGRATIONS: D1Migration[];
  }
}

interface Env extends Cloudflare.Env {}
