import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
	type MenuItemInput,
	type NewsPostInput,
	type ProfileInput,
	parseMenuItemInput,
	parseNewsPostInput,
	parseProfileInput,
} from "../src/data/validation.js";
import { ApiError } from "../src/security/errors.js";

/**
 * Builds a repeatable seed script from the bundled data files.
 *
 * Reads the same `staff_profiles.json` the plugin ships — there is no second
 * profile format to keep in sync — validates every record with the *same*
 * validators the admin API uses, and emits SQL.
 *
 * Nothing here talks to Cloudflare. It writes a .sql file; applying it is a
 * separate, explicit `wrangler d1 execute` step.
 *
 * Every statement is an upsert that leaves `published` alone, so re-running it
 * updates content without publishing anything. New records land unpublished.
 *
 *   npm run seed:build     write .wrangler/seed.sql
 *   npm run seed:local     write it and apply it to the local D1 file
 */

const here = dirname(fileURLToPath(import.meta.url));
const backendRoot = resolve(here, "..");
const repoRoot = resolve(backendRoot, "..");

const SOURCES = {
	profiles: join(repoRoot, "staff_profiles.json"),
	menu: join(backendRoot, "seed", "menu_items.json"),
	news: join(backendRoot, "seed", "news_posts.json"),
} as const;

/** Where bundled art for each collection lives, for reference checking. */
const ART_DIRECTORIES = {
	profiles: join(repoRoot, "img", "profile_pics"),
	menu: join(repoRoot, "img", "drinks"),
	news: join(repoRoot, "img"),
} as const;

const OUTPUT = join(backendRoot, ".wrangler", "seed.sql");

const problems: string[] = [];

function readJsonArray(path: string, label: string): unknown[] {
	if (!existsSync(path)) {
		problems.push(`${label}: source file not found at ${path}`);
		return [];
	}

	try {
		const parsed: unknown = JSON.parse(readFileSync(path, "utf8"));
		if (!Array.isArray(parsed)) {
			problems.push(`${label}: expected a JSON array`);
			return [];
		}

		return parsed;
	} catch (error) {
		problems.push(`${label}: could not parse JSON — ${(error as Error).message}`);
		return [];
	}
}

/**
 * Validates a collection.
 *
 * Records are validated independently so one bad entry reports its own problem
 * instead of masking the rest, and duplicate ids are rejected outright rather
 * than letting a later row silently overwrite an earlier one.
 */
function validateAll<T extends { id: string }>(
	entries: unknown[],
	label: string,
	parse: (body: unknown, id?: string) => T,
): T[] {
	const parsed: T[] = [];
	const seen = new Set<string>();

	entries.forEach((entry, index) => {
		// Position in the file becomes the display order unless one is given.
		const withOrder =
			typeof entry === "object" && entry !== null && !Array.isArray(entry)
				? { sortOrder: index, ...(entry as Record<string, unknown>) }
				: entry;

		try {
			const record = parse(withOrder);
			if (seen.has(record.id)) {
				problems.push(`${label}[${index}]: duplicate id "${record.id}"`);
				return;
			}

			seen.add(record.id);
			parsed.push(record);
		} catch (error) {
			const message = error instanceof ApiError ? error.message : (error as Error).message;
			problems.push(`${label}[${index}]: ${message}`);
		}
	});

	return parsed;
}

/** Confirms referenced bundled art actually exists, so a record cannot point at nothing. */
function checkArt(
	records: readonly { id: string; bundledImage: string }[],
	label: string,
	directory: string,
): void {
	for (const record of records) {
		if (record.bundledImage.length === 0) {
			continue;
		}

		if (!existsSync(join(directory, record.bundledImage))) {
			problems.push(`${label}: "${record.id}" references missing art ${record.bundledImage}`);
		}
	}
}

/** SQL string literal. Validation already rejects control characters. */
const sqlText = (value: string): string => `'${value.replaceAll("'", "''")}'`;

const sqlNullableText = (value: string | null): string =>
	value === null ? "NULL" : sqlText(value);

function profileStatement(input: ProfileInput, now: string): string {
	return `INSERT INTO profiles (
	id, category, name, character_name, age, affiliation, occupation, bio,
	optional_json, image_key, bundled_image, request_label, request_message,
	sort_order, created_at, updated_at, updated_by
) VALUES (
	${sqlText(input.id)}, ${sqlText(input.category)}, ${sqlText(input.name)},
	${sqlText(input.characterName)}, ${sqlText(input.age)}, ${sqlText(input.affiliation)},
	${sqlText(input.occupation)}, ${sqlText(input.bio)},
	${sqlNullableText(input.optional === null ? null : JSON.stringify(input.optional))},
	${sqlText(input.imageKey)}, ${sqlText(input.bundledImage)},
	${sqlText(input.requestLabel)}, ${sqlText(input.requestMessage)},
	${input.sortOrder}, ${sqlText(now)}, ${sqlText(now)}, 'seed-import'
)
ON CONFLICT(id) DO UPDATE SET
	category = excluded.category, name = excluded.name,
	character_name = excluded.character_name, age = excluded.age,
	affiliation = excluded.affiliation, occupation = excluded.occupation,
	bio = excluded.bio, optional_json = excluded.optional_json,
	image_key = excluded.image_key, bundled_image = excluded.bundled_image,
	request_label = excluded.request_label, request_message = excluded.request_message,
	sort_order = excluded.sort_order, updated_at = excluded.updated_at,
	updated_by = excluded.updated_by;`;
}

function menuStatement(input: MenuItemInput, now: string): string {
	return `INSERT INTO menu_items (
	id, name, price_gil, ingredients, description, taste,
	image_key, bundled_image, sort_order, created_at, updated_at, updated_by
) VALUES (
	${sqlText(input.id)}, ${sqlText(input.name)}, ${input.priceGil},
	${sqlText(input.ingredients)}, ${sqlText(input.description)}, ${sqlText(input.taste)},
	${sqlText(input.imageKey)}, ${sqlText(input.bundledImage)},
	${input.sortOrder}, ${sqlText(now)}, ${sqlText(now)}, 'seed-import'
)
ON CONFLICT(id) DO UPDATE SET
	name = excluded.name, price_gil = excluded.price_gil,
	ingredients = excluded.ingredients, description = excluded.description,
	taste = excluded.taste, image_key = excluded.image_key,
	bundled_image = excluded.bundled_image, sort_order = excluded.sort_order,
	updated_at = excluded.updated_at, updated_by = excluded.updated_by;`;
}

function newsStatement(input: NewsPostInput, now: string): string {
	return `INSERT INTO news_posts (
	id, title, summary, body, image_key, bundled_image,
	pinned, published_at, event_at, link, link_label,
	created_at, updated_at, updated_by
) VALUES (
	${sqlText(input.id)}, ${sqlText(input.title)}, ${sqlText(input.summary)},
	${sqlText(input.body)}, ${sqlText(input.imageKey)}, ${sqlText(input.bundledImage)},
	${input.pinned ? 1 : 0}, ${sqlText(input.publishedAt)}, ${sqlText(input.eventAt)},
	${sqlText(input.link)}, ${sqlText(input.linkLabel)},
	${sqlText(now)}, ${sqlText(now)}, 'seed-import'
)
ON CONFLICT(id) DO UPDATE SET
	title = excluded.title, summary = excluded.summary, body = excluded.body,
	image_key = excluded.image_key, bundled_image = excluded.bundled_image,
	pinned = excluded.pinned, published_at = excluded.published_at,
	event_at = excluded.event_at, link = excluded.link,
	link_label = excluded.link_label,
	updated_at = excluded.updated_at, updated_by = excluded.updated_by;`;
}

function main(): void {
	const now = new Date().toISOString();

	const profiles = validateAll(readJsonArray(SOURCES.profiles, "profiles"), "profiles", (body) =>
		parseProfileInput(body),
	);
	const menu = validateAll(readJsonArray(SOURCES.menu, "menu"), "menu", (body) =>
		parseMenuItemInput(body),
	);
	const news = validateAll(readJsonArray(SOURCES.news, "news"), "news", (body) =>
		parseNewsPostInput(body),
	);

	checkArt(profiles, "profiles", ART_DIRECTORIES.profiles);
	checkArt(menu, "menu", ART_DIRECTORIES.menu);
	checkArt(news, "news", ART_DIRECTORIES.news);

	if (problems.length > 0) {
		console.error("Seed import rejected:\n");
		for (const problem of problems) {
			console.error(`  - ${problem}`);
		}
		console.error("\nNo SQL was written.");
		process.exit(1);
	}

	const statements = [
		"-- Generated by scripts/import-seed.ts. Do not edit by hand.",
		"-- Upserts only: existing `published` flags are preserved and new rows",
		"-- arrive unpublished, so applying this never exposes anything.",
		"",
		...profiles.map((record) => profileStatement(record, now)),
		...menu.map((record) => menuStatement(record, now)),
		...news.map((record) => newsStatement(record, now)),
		"",
	];

	mkdirSync(dirname(OUTPUT), { recursive: true });
	writeFileSync(OUTPUT, statements.join("\n\n"), "utf8");

	console.log(
		`Wrote ${OUTPUT}\n  profiles: ${profiles.length}\n  menu: ${menu.length}\n  news: ${news.length}`,
	);
}

main();
