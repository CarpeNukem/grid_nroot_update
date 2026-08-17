import type { D1Migration } from "@cloudflare/vitest-pool-workers/config";
import type { Env } from "../src/types.js";

declare module "cloudflare:test" {
	interface ProvidedEnv extends Env {
		/** Injected by vitest.config.ts, not a real deployment binding. */
		TEST_MIGRATIONS: D1Migration[];
		/** Base64 of the real encoded files in test/fixtures. */
		TEST_MEDIA_FIXTURES: Record<string, string>;
	}
}
