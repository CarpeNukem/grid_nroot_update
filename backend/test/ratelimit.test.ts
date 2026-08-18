import { describe, expect, it } from "vitest";
import { enforceRateLimit, isRateLimited } from "../src/security/ratelimit.js";
import type { Env } from "../src/types.js";

/**
 * Which routes are metered, and what happens when one is refused.
 *
 * The limiter binding itself is Cloudflare's, so what is worth testing is the
 * decision around it: that admin and media are exempt for the right reasons,
 * that an absent binding fails open rather than breaking local development, and
 * that a refusal is a clean 429 rather than an unhandled throw.
 */

const request = (headers: Record<string, string> = {}): Request =>
	new Request("https://example.com/v1/catalog", { headers });

const envWith = (limiter: Env["PUBLIC_READ_LIMITER"]): Env =>
	({ PUBLIC_READ_LIMITER: limiter }) as Env;

describe("which paths are metered", () => {
	it("meters public reads", () => {
		for (const path of ["/v1/catalog", "/v1/menu", "/v1/profiles", "/v1/news", "/v1/pages/wifi"]) {
			expect(isRateLimited(path), path).toBe(true);
		}
	});

	it("exempts admin routes, which sit behind Access already", () => {
		expect(isRateLimited("/v1/admin/news")).toBe(false);
		expect(isRateLimited("/admin")).toBe(false);
	});

	it("exempts media, which is immutable and edge-cached", () => {
		// Throttling these would break image loading in the deck without
		// protecting the database they never touch.
		expect(isRateLimited("/media/news/x/abc.png")).toBe(false);
	});
});

describe("enforcement", () => {
	it("allows a request the limiter accepts", async () => {
		const env = envWith({ limit: async () => ({ success: true }) });

		await expect(enforceRateLimit(request(), env)).resolves.toBeUndefined();
	});

	it("throws a 429 once the limiter refuses", async () => {
		const env = envWith({ limit: async () => ({ success: false }) });

		await expect(enforceRateLimit(request(), env)).rejects.toMatchObject({
			status: 429,
			code: "RATE_LIMITED",
		});
	});

	it("keys on the Cloudflare-set client IP", async () => {
		const seen: string[] = [];
		const env = envWith({
			limit: async ({ key }) => {
				seen.push(key as string);
				return { success: true };
			},
		});

		await enforceRateLimit(request({ "cf-connecting-ip": "203.0.113.7" }), env);

		expect(seen).toEqual(["203.0.113.7"]);
	});

	it("ignores a client-supplied forwarding header", async () => {
		// X-Forwarded-For is attacker-controlled; trusting it would hand out a
		// fresh quota per spoofed value.
		const seen: string[] = [];
		const env = envWith({
			limit: async ({ key }) => {
				seen.push(key as string);
				return { success: true };
			},
		});

		await enforceRateLimit(request({ "x-forwarded-for": "1.2.3.4" }), env);

		expect(seen).toEqual(["unidentified"]);
	});

	it("shares one bucket when the caller cannot be identified", async () => {
		const seen: string[] = [];
		const env = envWith({
			limit: async ({ key }) => {
				seen.push(key as string);
				return { success: true };
			},
		});

		await enforceRateLimit(request(), env);

		expect(seen).toEqual(["unidentified"]);
	});

	it("does nothing when no limiter is bound", async () => {
		// Local development and the test pool may not provide one; the limiter is
		// a safeguard, not a correctness requirement.
		await expect(enforceRateLimit(request(), {} as Env)).resolves.toBeUndefined();
	});
});
