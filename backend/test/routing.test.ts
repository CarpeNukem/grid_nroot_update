import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

describe("dispatch", () => {
	it("returns the standard envelope for an unknown path", async () => {
		const response = await SELF.fetch("https://example.com/v1/nope");

		expect(response.status).toBe(404);
		expect(await response.json()).toEqual({
			error: { code: "NOT_FOUND", message: "The requested resource is unavailable." },
		});
	});

	it("answers 405 with an accurate Allow header on a known path", async () => {
		const response = await SELF.fetch("https://example.com/v1/health", { method: "POST" });

		expect(response.status).toBe(405);
		expect(response.headers.get("allow")).toBe("GET, HEAD, OPTIONS");
		expect(await response.json()).toMatchObject({ error: { code: "METHOD_NOT_ALLOWED" } });
	});

	it("serves HEAD with headers but no body", async () => {
		const response = await SELF.fetch("https://example.com/v1/health", { method: "HEAD" });

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("application/json; charset=utf-8");
		expect(await response.text()).toBe("");
	});

	it("answers CORS preflight for public reads", async () => {
		const response = await SELF.fetch("https://example.com/v1/health", { method: "OPTIONS" });

		expect(response.status).toBe(204);
		expect(response.headers.get("access-control-allow-origin")).toBe("*");
	});

	it("does not leak internals on a malformed path escape", async () => {
		const response = await SELF.fetch("https://example.com/v1/%E0%A4%A");
		const body = await response.text();

		expect(response.status).toBe(404);
		expect(body).not.toMatch(/URIError|at \w+ \(/);
	});
});
