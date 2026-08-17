import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";

/**
 * Guards on the deployment config itself.
 *
 * The Worker's own tests cannot catch a misconfigured deploy, because the
 * config is what decides which code path is live. The one that matters is
 * ENVIRONMENT: the development sign-in header stands in for Cloudflare Access
 * locally, and shipping it would be an authentication bypass.
 */
describe("wrangler configuration", () => {
	// Parsed in vitest.config.ts and handed across as a binding — workerd has no
	// filesystem, so the file cannot be read from inside a test.
	const config = env.TEST_WRANGLER_CONFIG;

	it("keeps the top level in development for wrangler dev and tests", () => {
		expect(config.vars.ENVIRONMENT).toBe("development");
	});

	it("declares a production environment that is not development", () => {
		const production = config.env?.production;

		expect(production).toBeDefined();
		expect(production?.vars.ENVIRONMENT).toBe("production");
	});

	it("does not expose a workers.dev URL in production", () => {
		// A hostname Access does not cover would route straight around it.
		expect(config.env?.production?.workers_dev).toBe(false);
	});

	/*
	 * Production is unconfigured until someone fills in a real origin, so these
	 * lie dormant rather than failing the suite for everyone. They wake up as
	 * soon as the first placeholder is replaced, which is exactly when a
	 * half-finished deploy config becomes possible.
	 */
	const productionVars = config.env?.production?.vars ?? {};

	// Any remaining placeholder means production has not been set up yet.
	// `npm run predeploy` is what refuses to ship a half-filled config; these
	// only need to hold once real values are in place.
	const unconfigured = Object.values(productionVars).some((value) => value.includes("REPLACE-ME"));

	it.skipIf(unconfigured)("never points production media at a local address", () => {
		expect(productionVars.PUBLIC_MEDIA_BASE_URL ?? "").not.toMatch(/127\.0\.0\.1|localhost/);
	});

	it.skipIf(unconfigured)("serves production media over https", () => {
		expect(productionVars.PUBLIC_MEDIA_BASE_URL ?? "").toMatch(/^https:\/\//);
	});
});
