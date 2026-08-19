import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import worker from "../src/index.js";
import { isAdminHostname, isAdminSurface } from "../src/security/hosts.js";
import type { Env } from "../src/types.js";

/**
 * The public site, and the hostname rule that has to hold once a second
 * hostname points at this Worker.
 *
 * Cloudflare Access is bound to `api.nroot.io`. The router matches on path
 * alone, so without the guard `grid.nroot.io/admin` would serve the panel with
 * nothing in front of it. That is the failure these tests exist to catch, and
 * it is invisible in development — where the guard is deliberately relaxed — so
 * the production behaviour is driven through the handler with an explicit env
 * rather than through SELF.
 */

const ADMIN_HOST = "api.nroot.io";
const SITE_HOST = "grid.nroot.io";

/**
 * A caller address of this file's own.
 *
 * The public read limiter allows 60 requests a minute per caller, and a request
 * with no `CF-Connecting-IP` falls into a single shared bucket — so every test
 * file that omits it spends from the same 60. Claiming a documentation address
 * (RFC 5737) keeps this file's requests off that budget, which is both why it
 * is here and the reason the suite does not fail once these tests are added.
 */
const CALLER = { "cf-connecting-ip": "203.0.113.9" } as const;

const get = (path: string): Promise<Response> =>
	SELF.fetch(`https://example.com${path}`, { headers: CALLER });

const productionEnv = (): Env =>
	({
		...env,
		ENVIRONMENT: "production",
		ADMIN_HOSTNAME: ADMIN_HOST,
	}) as Env;

const fetchAs = (host: string, path: string, testEnv: Env): Promise<Response> =>
	worker.fetch(new Request(`https://${host}${path}`, { headers: CALLER }), testEnv);

describe("admin hostname guard", () => {
	it("recognises both admin surfaces", () => {
		expect(isAdminSurface("/admin")).toBe(true);
		expect(isAdminSurface("/v1/admin/news")).toBe(true);

		expect(isAdminSurface("/")).toBe(false);
		expect(isAdminSurface("/v1/catalog")).toBe(false);
		// Near misses that must not be mistaken for the real prefix.
		expect(isAdminSurface("/admin-tools")).toBe(false);
		expect(isAdminSurface("/v1/administrators")).toBe(false);
	});

	it("allows admin only on the configured hostname", () => {
		const production = productionEnv();

		expect(isAdminHostname(production, ADMIN_HOST)).toBe(true);
		expect(isAdminHostname(production, "API.NROOT.IO")).toBe(true);
		expect(isAdminHostname(production, SITE_HOST)).toBe(false);
		expect(isAdminHostname(production, "grid-cyberdeck-api.workers.dev")).toBe(false);
	});

	it("denies admin everywhere when the hostname is not configured", () => {
		// A missing setting must lock the editor out, never open the panel up.
		for (const value of [undefined, "", "   "]) {
			const misconfigured = { ...productionEnv(), ADMIN_HOSTNAME: value } as Env;

			expect(isAdminHostname(misconfigured, ADMIN_HOST)).toBe(false);
			expect(isAdminHostname(misconfigured, SITE_HOST)).toBe(false);
		}
	});

	it("exempts development, which has no stable hostname", () => {
		expect(isAdminHostname(env, "127.0.0.1")).toBe(true);
		expect(isAdminHostname(env, "localhost")).toBe(true);
	});

	it("hides the admin page on the public hostname", async () => {
		const response = await fetchAs(SITE_HOST, "/admin", productionEnv());

		// 404, not 403: a visitor to the venue site has no business learning
		// that there is an admin panel somewhere to go looking for.
		expect(response.status).toBe(404);
		expect(await response.text()).not.toContain("Cyberdeck Admin");
	});

	it("hides the admin API on the public hostname", async () => {
		const response = await fetchAs(SITE_HOST, "/v1/admin/news", productionEnv());

		expect(response.status).toBe(404);
	});

	it("still requires authentication on the admin hostname", async () => {
		// The guard narrows where admin exists; it does not authenticate anyone.
		const response = await fetchAs(ADMIN_HOST, "/v1/admin/news", productionEnv());

		expect(response.status).toBe(401);
	});

	it("leaves public reads reachable on the public hostname", async () => {
		const response = await fetchAs(SITE_HOST, "/v1/catalog", productionEnv());

		expect(response.status).toBe(200);
	});

	it("serves the site on the public hostname", async () => {
		const response = await fetchAs(SITE_HOST, "/", productionEnv());

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("text/html; charset=utf-8");
	});
});

describe("public site", () => {
	it("is served as HTML", async () => {
		const response = await get("/");

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("text/html; charset=utf-8");
		expect(await response.text()).toContain("THE GRID");
	});

	it("serves its artwork from its own origin", async () => {
		const response = await get("/assets/rooftop.webp");

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("image/webp");
		expect(response.headers.get("cache-control")).toContain("max-age");
	});

	it("serves only the artwork it lists", async () => {
		// The name is looked up in a fixed map, never joined onto a path, so
		// there is no traversal to get wrong.
		for (const name of ["nope.webp", "../wrangler.jsonc", "index.html"]) {
			expect((await get(`/assets/${name}`)).status).toBe(404);
		}
	});

	it("sends a policy that allows its own script but nothing external", async () => {
		const policy = (await get("/")).headers.get("content-security-policy") ?? "";

		expect(policy).toContain("default-src 'none'");
		expect(policy).toContain("script-src 'unsafe-inline'");
		// The page rewrites media onto its own origin, so 'self' is sufficient
		// and no second hostname has to be trusted for images.
		expect(policy).toContain("img-src 'self' data:");
		expect(policy).toContain("connect-src 'self'");
		expect(policy).toContain("frame-ancestors 'none'");
	});

	it("never posts anything back", async () => {
		const response = await get("/");
		const html = await response.text();

		// Read-only by construction: the deck's controls navigate between views
		// or copy to the clipboard, and nothing submits. Enforced by the policy
		// as well as by the markup, so a future edit cannot quietly add one.
		expect(html).not.toMatch(/<form/);
		expect(response.headers.get("content-security-policy")).toContain("form-action 'none'");
	});

	it("leaves the crew's in-game actions out", async () => {
		const html = await (await get("/")).text();

		// requestLabel drives a /tell from the plugin. On the web it would be a
		// button that cannot do the one thing it names, so it is not rendered.
		expect(html).not.toMatch(/profile\.requestLabel|profile\.requestMessage/);
		expect(html).toContain("deliberately not rendered");
	});

	it("offers only the views a browser can honestly serve", async () => {
		const html = await (await get("/")).text();

		for (const view of ["menu:", "wifi:", "address:", "crew:", "broadcast:"]) {
			expect(html).toContain(view);
		}

		// Network reads live game memory, Settings configures a plugin, and the
		// activities are played in-game. None of them can work here.
		expect(html).not.toMatch(/DrawNetworkView|"network":|settings:|breach|cipher/i);
	});

	it("builds its content as text nodes rather than markup", async () => {
		const html = await (await get("/")).text();

		// Editor-authored content is still stored text arriving over the wire.
		expect(html).not.toContain(".innerHTML");
		expect(html).toContain("textContent");
	});

	it("ships no credentials in the page", async () => {
		const html = await (await get("/")).text();

		expect(html).not.toMatch(/CF_ACCESS|api[_-]?key|secret|Bearer\s/i);
	});
});

describe("indexing", () => {
	it("marks the site page noindex in both places", async () => {
		const response = await get("/");
		const html = await response.text();

		expect(response.headers.get("x-robots-tag")).toContain("noindex");
		expect(html).toContain('name="robots"');
		expect(html).toContain("noindex");
	});

	it("marks the API noindex too, which no meta tag could reach", async () => {
		const response = await get("/v1/catalog");

		expect(response.headers.get("x-robots-tag")).toContain("noindex");
	});

	it("permits crawling so the noindex is actually read", async () => {
		const response = await get("/robots.txt");
		const body = await response.text();

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("text/plain; charset=utf-8");

		// A blanket Disallow would be self-defeating: a crawler refused the page
		// never reads the noindex on it, and can still list the bare URL from a
		// link found elsewhere.
		expect(body).not.toMatch(/^Disallow:\s*\/\s*$/m);
		expect(body).toMatch(/^Disallow:\s*$/m);
	});

	it("does not advertise the admin paths", async () => {
		const body = await (await get("/robots.txt")).text();

		expect(body).not.toContain("/admin");
	});
});
