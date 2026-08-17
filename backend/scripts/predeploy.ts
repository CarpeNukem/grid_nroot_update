import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Refuses to ship a misconfigured Worker.
 *
 * The tests cannot cover this: config is what decides which code path is live,
 * so a deploy-time check is the only place these can be caught. Run by
 * `npm run deploy:production` before wrangler is invoked.
 *
 * The one that really matters is ENVIRONMENT. The Worker accepts an
 * `x-dev-admin-email` header in place of a Cloudflare Access token when it is
 * "development" — correct locally, an authentication bypass anywhere else.
 */

const here = dirname(fileURLToPath(import.meta.url));
const configPath = join(here, "..", "wrangler.jsonc");

interface Environment {
	readonly vars?: Record<string, string>;
	readonly workers_dev?: boolean;
	readonly d1_databases?: { database_id?: string }[];
}

const config = JSON.parse(readFileSync(configPath, "utf8").replace(/^\s*\/\/.*$/gm, "")) as {
	env?: Record<string, Environment>;
};

const target = process.argv[2] ?? "production";
const environment = config.env?.[target];
const problems: string[] = [];

if (environment === undefined) {
	console.error(`No "${target}" environment in wrangler.jsonc.`);
	process.exit(1);
}

const vars = environment.vars ?? {};

if (vars.ENVIRONMENT === "development") {
	problems.push(
		'ENVIRONMENT is "development" — the local sign-in header would be accepted in production.',
	);
}

if (environment.workers_dev !== false) {
	problems.push(
		"workers_dev is not false — a workers.dev hostname would route around Cloudflare Access.",
	);
}

for (const [key, value] of Object.entries(vars)) {
	if (value.includes("REPLACE-ME")) {
		problems.push(`${key} still holds its placeholder.`);
	}
}

if (/127\.0\.0\.1|localhost/.test(vars.PUBLIC_MEDIA_BASE_URL ?? "")) {
	problems.push("PUBLIC_MEDIA_BASE_URL points at this machine.");
}

if (!(vars.PUBLIC_MEDIA_BASE_URL ?? "").startsWith("https://")) {
	problems.push("PUBLIC_MEDIA_BASE_URL is not https.");
}

for (const database of environment.d1_databases ?? []) {
	if (!database.database_id || database.database_id.includes("REPLACE-ME")) {
		problems.push("The D1 database_id is missing — run `wrangler d1 create` first.");
	}
}

if (problems.length > 0) {
	console.error(`Refusing to deploy "${target}":\n`);
	for (const problem of problems) {
		console.error(`  - ${problem}`);
	}
	console.error("\nNothing was deployed.");
	process.exit(1);
}

console.log(`"${target}" configuration looks deployable.`);
