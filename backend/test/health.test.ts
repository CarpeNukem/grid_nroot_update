import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

describe("GET /v1/health", () => {
	it("reports availability", async () => {
		const response = await SELF.fetch("https://example.com/v1/health");

		expect(response.status).toBe(200);
		expect(await response.json()).toMatchObject({
			status: "ok",
			environment: "development",
			schemaVersion: 1,
		});
	});

	it("is never cached", async () => {
		const response = await SELF.fetch("https://example.com/v1/health");

		expect(response.headers.get("cache-control")).toBe("no-store");
	});

	it("applies security headers and a correlation id", async () => {
		const response = await SELF.fetch("https://example.com/v1/health");

		expect(response.headers.get("content-type")).toBe("application/json; charset=utf-8");
		expect(response.headers.get("x-content-type-options")).toBe("nosniff");
		expect(response.headers.get("referrer-policy")).toBe("no-referrer");
		expect(response.headers.get("x-request-id")).toBeTruthy();
	});

	it("does not expose binding names or ids", async () => {
		const response = await SELF.fetch("https://example.com/v1/health");
		const body = await response.text();

		expect(body).not.toMatch(/grid-cyberdeck-dev|grid-cyberdeck-media|database_id|\bDB\b/);
	});
});
