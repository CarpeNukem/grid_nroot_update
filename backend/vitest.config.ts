import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { defineWorkersConfig, readD1Migrations } from "@cloudflare/vitest-pool-workers/config";

/**
 * Real encoded media, base64'd into a binding.
 *
 * workerd has no filesystem, so binary fixtures cannot be read from inside a
 * test. They are loaded here in Node and handed across, the same way migrations
 * are.
 */
const mediaFixtures = Object.fromEntries(
	["silent.mp4", "withaudio.mp4"]
		.map((name) => {
			const path = join(import.meta.dirname, "test", "fixtures", name);
			// Absent is fine — the tests that need them skip. See test/fixtures/README.md.
			return existsSync(path) ? [name, readFileSync(path).toString("base64")] : undefined;
		})
		.filter((entry) => entry !== undefined),
);

/*
 * The wrangler config, parsed here so a test can assert on it. workerd has no
 * filesystem, so it cannot read the file itself.
 */
const wranglerConfig = JSON.parse(
	// JSONC is not JSON: strip whole-line comments before parsing.
	readFileSync(join(import.meta.dirname, "wrangler.jsonc"), "utf8").replace(/^\s*\/\/.*$/gm, ""),
);

// Migrations are read once at config time and handed to each test worker as a
// binding, so the suite exercises the same SQL that `wrangler d1 migrations
// apply` runs. A broken migration fails the tests rather than production.
const migrations = await readD1Migrations("./migrations");

export default defineWorkersConfig({
	test: {
		setupFiles: ["./test/apply-migrations.ts"],
		poolOptions: {
			workers: {
				singleWorker: true,
				// Miniflare's per-suite storage stacking cannot unlink its R2 SQLite
				// file on Windows (EBUSY), which fails the run outright once R2 is
				// actually written to. Tests share one store and clean up explicitly
				// in `resetTables` instead.
				isolatedStorage: false,
				wrangler: { configPath: "./wrangler.jsonc" },
				miniflare: {
					bindings: {
						TEST_MIGRATIONS: migrations,
						// Test-only. Kept out of wrangler.jsonc because the real value is a
						// secret; the development sign-in path also requires
						// ENVIRONMENT === "development", which production never sets.
						ADMIN_ALLOWED_EMAILS: "editor@thegrid.test, other@thegrid.test",
						TEST_MEDIA_FIXTURES: mediaFixtures,
						TEST_WRANGLER_CONFIG: wranglerConfig,
					},
				},
			},
		},
	},
});
