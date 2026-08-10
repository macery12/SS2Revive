import { env } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";
import type { AuthPrincipal } from "@ss2revive/community-contracts";
import {
  archiveOwnMap,
  listOpenReports,
  reportMap,
  restoreMap,
  takedownMap,
  unpublishOwnMap,
} from "../src/community-controls";
import { runtimeConfig } from "../src/config";
import { seedLocalFixture } from "../src/local-fixture";

const ORIGIN = "http://127.0.0.1:8787";
const OWNER = "76561198145479980";
const REPORTER = "76561198145479981";
const MAP_ID = "1a658233-92c5-4b63-87fc-4740c855730b";
const ownerPrincipal: AuthPrincipal = {
  steamId64: OWNER,
  sessionId: "11111111-1111-1111-1111-111111111111",
  scopes: ["maps:manage", "maps:report", "maps:moderate"],
};
const reporterPrincipal: AuthPrincipal = {
  steamId64: REPORTER,
  sessionId: "22222222-2222-2222-2222-222222222222",
  scopes: ["maps:manage", "maps:report"],
};

async function clearState(): Promise<void> {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM moderation_events"),
    env.DB.prepare("DELETE FROM map_reports"),
    env.DB.prepare("DELETE FROM upload_usage_daily"),
    env.DB.prepare("DELETE FROM map_uploads"),
    env.DB.prepare("DELETE FROM map_tags"),
    env.DB.prepare("DELETE FROM map_versions"),
    env.DB.prepare("DELETE FROM maps"),
    env.DB.prepare("DELETE FROM download_leases"),
    env.DB.prepare("DELETE FROM download_usage_daily"),
    env.DB.prepare("DELETE FROM steam_openid_sessions"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM refresh_token_families"),
    env.DB.prepare("DELETE FROM auth_sessions"),
    env.DB.prepare("DELETE FROM device_auth_sessions"),
    env.DB.prepare("DELETE FROM users"),
  ]);
  await seedLocalFixture(env, runtimeConfig(env));
  await env.DB.prepare(
    `INSERT INTO users (steam_id64, status, created_at, last_login_at) VALUES (?, 'active', ?, ?)`,
  ).bind(REPORTER, Date.UTC(2026, 7, 8), Date.UTC(2026, 7, 8)).run();
}

beforeEach(clearState);

describe("community ownership and moderation controls", () => {
  it("lets the verified owner unpublish and archive without deleting approved objects", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12);
    const unpublished = await unpublishOwnMap(
      new Request(`${ORIGIN}/v1/maps/${MAP_ID}/unpublish`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
      }), env, ownerPrincipal, MAP_ID, crypto.randomUUID(), nowMs,
    );
    expect(unpublished.status).toBe(200);
    expect(await env.DB.prepare("SELECT status FROM maps WHERE id = ?").bind(MAP_ID).first())
      .toMatchObject({ status: "unpublished" });

    const archived = await archiveOwnMap(
      new Request(`${ORIGIN}/v1/maps/${MAP_ID}`, { method: "DELETE" }),
      env, ownerPrincipal, MAP_ID, crypto.randomUUID(), nowMs + 1,
    );
    expect(archived.status).toBe(204);
    expect(await env.DB.prepare("SELECT status FROM maps WHERE id = ?").bind(MAP_ID).first())
      .toMatchObject({ status: "archived" });
    const objects = await env.MAP_BUCKET.list({ prefix: `approved/maps/${MAP_ID}/` });
    expect(objects.objects.length).toBeGreaterThan(0);
  });

  it("stores one bounded report per reporter and exposes it only to the maintainer", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12);
    const request = (reason: string) => new Request(`${ORIGIN}/v1/maps/${MAP_ID}/reports`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason, note: "Needs review" }),
    });
    expect((await reportMap(request("BROKEN"), env, reporterPrincipal, MAP_ID,
      crypto.randomUUID(), nowMs)).status).toBe(201);
    expect((await reportMap(request("COPY"), env, reporterPrincipal, MAP_ID,
      crypto.randomUUID(), nowMs + 1)).status).toBe(201);
    expect(await env.DB.prepare("SELECT COUNT(*) AS count FROM map_reports").first())
      .toMatchObject({ count: 1 });

    await expect(listOpenReports(new Request(`${ORIGIN}/v1/moderation/reports`), env,
      runtimeConfig(env), reporterPrincipal, crypto.randomUUID()))
      .rejects.toMatchObject({ code: "maintainer_required", status: 403 });
    const reports = await listOpenReports(new Request(`${ORIGIN}/v1/moderation/reports`), env,
      runtimeConfig(env), ownerPrincipal, crypto.randomUUID());
    expect(await reports.json()).toMatchObject({ items: [{ mapId: MAP_ID, reason: "COPY" }] });
  });

  it("records maintainer takedowns, resolves reports, and can restore the map", async () => {
    const nowMs = Date.UTC(2026, 7, 8, 12);
    await reportMap(new Request(`${ORIGIN}/v1/maps/${MAP_ID}/reports`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "BROKEN" }),
    }), env, reporterPrincipal, MAP_ID, crypto.randomUUID(), nowMs);
    const config = runtimeConfig(env);
    const takedown = await takedownMap(new Request(`${ORIGIN}/v1/moderation/maps/${MAP_ID}/takedown`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "Confirmed broken" }),
    }), env, config, ownerPrincipal, MAP_ID, crypto.randomUUID(), nowMs + 1);
    expect(takedown.status).toBe(200);
    expect(await env.DB.prepare("SELECT status FROM maps WHERE id = ?").bind(MAP_ID).first())
      .toMatchObject({ status: "archived" });
    expect(await env.DB.prepare("SELECT status FROM map_reports WHERE map_id = ?").bind(MAP_ID).first())
      .toMatchObject({ status: "resolved" });

    const restored = await restoreMap(new Request(`${ORIGIN}/v1/moderation/maps/${MAP_ID}/restore`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "Issue corrected" }),
    }), env, config, ownerPrincipal, MAP_ID, crypto.randomUUID(), nowMs + 2);
    expect(restored.status).toBe(200);
    expect(await env.DB.prepare("SELECT status FROM maps WHERE id = ?").bind(MAP_ID).first())
      .toMatchObject({ status: "published" });
    expect(await env.DB.prepare("SELECT COUNT(*) AS count FROM moderation_events WHERE map_id = ?")
      .bind(MAP_ID).first()).toMatchObject({ count: 2 });
  });
});
