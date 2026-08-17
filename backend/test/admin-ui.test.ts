import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

/**
 * The admin page itself.
 *
 * The page is only a shell — every check that matters happens in the API it
 * calls — so these tests cover the two things that would break silently: the
 * headers it is served with, and the fact that serving a page must not become a
 * way to reach the API without signing in.
 */
describe("admin UI", () => {
	it("is served as HTML", async () => {
		const response = await SELF.fetch("https://example.com/admin");

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("text/html; charset=utf-8");
		expect(await response.text()).toContain("Cyberdeck Admin");
	});

	it("sends a policy that allows its own script but nothing external", async () => {
		const response = await SELF.fetch("https://example.com/admin");
		const policy = response.headers.get("content-security-policy") ?? "";

		// The JSON routes' deny-all policy would stop the page working at all.
		expect(policy).toContain("script-src 'unsafe-inline'");
		expect(policy).toContain("connect-src 'self'");
		expect(policy).toContain("default-src 'none'");
		expect(policy).toContain("frame-ancestors 'none'");
	});

	it("is never cached", async () => {
		const response = await SELF.fetch("https://example.com/admin");

		expect(response.headers.get("cache-control")).toBe("no-store");
	});

	it("does not authenticate anything by itself", async () => {
		// Reaching the page must not imply access to the data behind it.
		await SELF.fetch("https://example.com/admin");
		const listing = await SELF.fetch("https://example.com/v1/admin/news");

		expect(listing.status).toBe(401);
	});

	it("ships no credentials in the page", async () => {
		const html = await (await SELF.fetch("https://example.com/admin")).text();

		expect(html).not.toMatch(/CF_ACCESS|api[_-]?key|secret|Bearer\s/i);
	});
});
