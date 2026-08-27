import { env, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { PUBLIC_READ_EDGE_TTL_SECONDS } from "../src/cache.js";
import { ADMIN_EMAIL, asAdmin, jsonHeaders, menuFixture, resetTables } from "./helpers.js";

/**
 * The edge cache in front of `/v1/catalog`.
 *
 * The deck polls once a minute for as long as it runs, and every poll used to
 * cost four D1 queries whether it answered 200 or 304 — the ETag is a hash of
 * the body, so the body had to be built either way. These tests pin the two
 * things that make caching safe to rely on: that a second request really is
 * served without touching the database, and that conditional requests still
 * behave correctly on top of a shared entry.
 */

/** Distinct from the other suites' callers, to stay off the shared 60/min budget. */
const CALLER = { "cf-connecting-ip": "203.0.113.11" } as const;

const catalog = (headers: Record<string, string> = {}): Promise<Response> =>
	SELF.fetch("https://example.com/v1/catalog", { headers: { ...CALLER, ...headers } });

/**
 * Empties the cache between tests.
 *
 * Storage is shared across this run, so an entry written by one test would
 * otherwise decide the next one's result.
 */
async function clearCatalogCache(): Promise<void> {
	await caches.default.delete(new Request("https://example.com/v1/catalog", { method: "GET" }));
}

describe("catalogue edge cache", () => {
	beforeEach(async () => {
		await resetTables();
		await clearCatalogCache();
	});

	it("serves a second request without reading the database again", async () => {
		const first = await catalog();
		expect(first.status).toBe(200);
		const firstBody = await first.text();

		// Delete every row. A request that still queries D1 must now come back
		// empty; one served from the cache returns exactly what it stored.
		await env.DB.batch([
			env.DB.prepare("DELETE FROM profiles"),
			env.DB.prepare("DELETE FROM menu_items"),
			env.DB.prepare("DELETE FROM news_posts"),
		]);

		const second = await catalog();

		expect(second.status).toBe(200);
		expect(await second.text()).toBe(firstBody);
	});

	it("reports whether the edge served it", async () => {
		expect((await catalog()).headers.get("x-cache")).toBe("MISS");
		expect((await catalog()).headers.get("x-cache")).toBe("HIT");
	});

	it("gives every caller the same tag, so one entry serves all of them", async () => {
		const tag = (await catalog()).headers.get("etag");
		const other = await SELF.fetch("https://example.com/v1/catalog", {
			headers: { "cf-connecting-ip": "203.0.113.12" },
		});

		expect(tag).not.toBeNull();
		expect(other.headers.get("etag")).toBe(tag);
	});

	it("still answers 304 to a conditional request", async () => {
		const tag = (await catalog()).headers.get("etag") ?? "";
		const conditional = await catalog({ "if-none-match": tag });

		expect(conditional.status).toBe(304);
		expect(await conditional.text()).toBe("");
		expect(conditional.headers.get("etag")).toBe(tag);
	});

	it("never lets one caller's conditional request become the shared entry", async () => {
		// If `If-None-Match` were part of the cache key, or a 304 were stored,
		// the next caller would be served an empty body as though it were the
		// catalogue. This is the failure that would be invisible in production.
		const tag = (await catalog()).headers.get("etag") ?? "";
		expect((await catalog({ "if-none-match": tag })).status).toBe(304);

		const fresh = await catalog();

		expect(fresh.status).toBe(200);
		expect((await fresh.text()).length).toBeGreaterThan(0);
	});

	it("gives each response its own request id rather than the cached one", async () => {
		const first = await catalog();
		const second = await catalog();

		const firstId = first.headers.get("x-request-id");
		expect(firstId).not.toBeNull();
		expect(second.headers.get("x-request-id")).not.toBe(firstId);
	});

	it("picks up an edit once the entry expires", async () => {
		await catalog();

		await SELF.fetch("https://example.com/v1/admin/menu", {
			method: "POST",
			headers: jsonHeaders(asAdmin(CALLER)),
			body: JSON.stringify(menuFixture({ id: "post-cache", name: "Post Cache" })),
		});
		await SELF.fetch("https://example.com/v1/admin/menu/post-cache/publish", {
			method: "POST",
			headers: jsonHeaders(asAdmin(CALLER)),
			body: JSON.stringify({ published: true }),
		});

		// Cache entries are per colo and there is no purge that reaches them all,
		// so the window is what bounds staleness. Expiry is simulated here rather
		// than waited out.
		await clearCatalogCache();

		const body = (await catalog()).text();

		expect(await body).toContain("post-cache");
	});

	it("keeps the window short enough that an edit is not left sitting", () => {
		// Freshness is a product decision, not an implementation detail: the deck
		// polls every minute, so the edge window is what decides whether an edit
		// lands on the next poll or the one after.
		expect(PUBLIC_READ_EDGE_TTL_SECONDS).toBeLessThanOrEqual(30);
		expect(PUBLIC_READ_EDGE_TTL_SECONDS).toBeGreaterThan(0);
	});

	it("does not cache anything an editor is authenticated for", async () => {
		// Admin reads are per-editor and must never land in a shared entry.
		const listing = await SELF.fetch("https://example.com/v1/admin/menu", {
			headers: asAdmin(CALLER),
		});

		expect(listing.headers.get("cache-control")).toBe("no-store");
		expect(ADMIN_EMAIL).not.toBe("");
	});
});
