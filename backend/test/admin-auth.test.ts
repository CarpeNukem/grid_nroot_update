import { env, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { requireAdmin } from "../src/security/auth.js";
import { ADMIN_EMAIL, asAdmin, jsonHeaders, menuFixture, resetTables } from "./helpers.js";

/**
 * The admin boundary.
 *
 * These are the tests that matter most: if any of them regress, an unsigned
 * caller can edit published venue content.
 */
describe("admin authentication", () => {
	beforeEach(resetTables);

	const adminPaths = [
		["GET", "/v1/admin/profiles"],
		["GET", "/v1/admin/menu"],
		["GET", "/v1/admin/news"],
		["POST", "/v1/admin/profiles"],
		["POST", "/v1/admin/menu"],
		["POST", "/v1/admin/news"],
		["PUT", "/v1/admin/menu/frostbite"],
		["DELETE", "/v1/admin/menu/frostbite"],
		["POST", "/v1/admin/menu/frostbite/publish"],
	] as const;

	it.each(adminPaths)("rejects unauthenticated %s %s", async (method, path) => {
		const response = await SELF.fetch(`https://example.com${path}`, {
			method,
			headers: jsonHeaders(),
			...(method === "GET" || method === "DELETE" ? {} : { body: "{}" }),
		});

		expect(response.status).toBe(401);
		expect(await response.json()).toMatchObject({ error: { code: "UNAUTHORIZED" } });
	});

	it("rejects an email that is not on the allowlist", async () => {
		const response = await SELF.fetch("https://example.com/v1/admin/menu", {
			headers: { "x-dev-admin-email": "stranger@example.com" },
		});

		expect(response.status).toBe(401);
	});

	it("accepts an allowlisted editor", async () => {
		const response = await SELF.fetch("https://example.com/v1/admin/menu", {
			headers: asAdmin(),
		});

		expect(response.status).toBe(200);
		expect(await response.json()).toEqual({ items: [] });
	});

	it("refuses the development sign-in path outside development", async () => {
		const productionEnv = { ...env, ENVIRONMENT: "production" };
		const request = new Request("https://example.com/v1/admin/menu", {
			headers: { "x-dev-admin-email": ADMIN_EMAIL },
		});

		// Outside development the header is not even consulted: the request falls
		// through to the Access path and is refused for having no assertion.
		await expect(requireAdmin(request, productionEnv)).rejects.toMatchObject({
			status: 401,
			code: "UNAUTHORIZED",
		});
	});

	it("never answers an admin route with the public wildcard CORS policy", async () => {
		const response = await SELF.fetch("https://example.com/v1/admin/menu", {
			headers: asAdmin({ origin: "https://attacker.example" }),
		});

		expect(response.headers.get("access-control-allow-origin")).toBeNull();
	});

	it("allows the configured admin origin only", async () => {
		const allowed = await SELF.fetch("https://example.com/v1/admin/menu", {
			headers: asAdmin({ origin: env.ADMIN_ALLOWED_ORIGIN }),
		});

		expect(allowed.headers.get("access-control-allow-origin")).toBe(env.ADMIN_ALLOWED_ORIGIN);
		expect(allowed.headers.get("vary")).toBe("Origin");
	});

	it("does not attach wildcard CORS to an admin preflight", async () => {
		const response = await SELF.fetch("https://example.com/v1/admin/menu", {
			method: "OPTIONS",
			headers: { origin: "https://attacker.example" },
		});

		expect(response.headers.get("access-control-allow-origin")).toBeNull();
	});

	it("records the authenticated editor on the row", async () => {
		await SELF.fetch("https://example.com/v1/admin/menu", {
			method: "POST",
			headers: jsonHeaders(asAdmin()),
			body: JSON.stringify(menuFixture()),
		});

		const row = await env.DB.prepare("SELECT updated_by FROM menu_items WHERE id = ?")
			.bind("frostbite")
			.first<{ updated_by: string }>();

		expect(row?.updated_by).toBe(ADMIN_EMAIL);
	});
});
