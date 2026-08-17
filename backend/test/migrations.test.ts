import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";

/**
 * Schema checks against the fully migrated database.
 *
 * The suite applies every migration in `migrations/` through the same code path
 * `wrangler d1 migrations apply` uses, so a broken migration fails here rather
 * than in production.
 */

interface ColumnInfo {
	name: string;
	notnull: number;
	dflt_value: string | null;
}

const columnsOf = async (table: string): Promise<string[]> => {
	// `table` is a literal from this test file, never request input.
	const { results } = await env.DB.prepare(`PRAGMA table_info(${table})`).all<ColumnInfo>();
	return results.map((column) => column.name);
};

const indexesOf = async (table: string): Promise<string[]> => {
	const { results } = await env.DB.prepare(`PRAGMA index_list(${table})`).all<{ name: string }>();
	return results.map((index) => index.name);
};

describe("profiles", () => {
	it("has the documented columns", async () => {
		expect(await columnsOf("profiles")).toEqual([
			"id",
			"category",
			"name",
			"character_name",
			"age",
			"affiliation",
			"occupation",
			"bio",
			"optional_json",
			"image_key",
			"request_label",
			"request_message",
			"published",
			"sort_order",
			"created_at",
			"updated_at",
			// Added by 0002 alongside the menu and news tables.
			"bundled_image",
			"updated_by",
			// Added by 0004: a second image slot, used for DJ brand marks.
			"logo_key",
			"logo_bundled",
		]);
	});

	it("indexes the public list query", async () => {
		expect(await indexesOf("profiles")).toContain("profiles_category_published");
	});
});

describe("menu_items", () => {
	it("has the documented columns", async () => {
		expect(await columnsOf("menu_items")).toEqual([
			"id",
			"name",
			"price_gil",
			"ingredients",
			"description",
			"taste",
			"image_key",
			"bundled_image",
			"published",
			"sort_order",
			"created_at",
			"updated_at",
			"updated_by",
		]);
	});

	it("rejects a negative price", async () => {
		await expect(
			env.DB.prepare(
				`INSERT INTO menu_items (id, name, price_gil, created_at, updated_at)
				 VALUES (?, ?, ?, ?, ?)`,
			)
				.bind("bad-price", "Bad Price", -1, "now", "now")
				.run(),
		).rejects.toThrow();
	});

	it("indexes the public list query", async () => {
		expect(await indexesOf("menu_items")).toContain("menu_items_published");
	});
});

describe("news_posts", () => {
	it("has the documented columns", async () => {
		expect(await columnsOf("news_posts")).toEqual([
			"id",
			"title",
			"summary",
			"body",
			"image_key",
			"bundled_image",
			"pinned",
			"published_at",
			"published",
			"created_at",
			"updated_at",
			"updated_by",
			// Added by 0003 for event announcements.
			"event_at",
			"link",
			"link_label",
		]);
	});

	it("indexes the feed and the event schedule", async () => {
		const indexes = await indexesOf("news_posts");

		expect(indexes).toContain("news_posts_published");
		expect(indexes).toContain("news_posts_event");
	});

	it("defaults the event details to empty rather than null", async () => {
		await env.DB.prepare(
			`INSERT INTO news_posts (id, title, published_at, created_at, updated_at)
			 VALUES (?, ?, ?, ?, ?)`,
		)
			.bind("no-event", "No Event", "now", "now", "now")
			.run();

		const row = await env.DB.prepare(
			"SELECT event_at, link, link_label FROM news_posts WHERE id = ?",
		)
			.bind("no-event")
			.first<{ event_at: string; link: string; link_label: string }>();

		expect(row).toEqual({ event_at: "", link: "", link_label: "" });
	});
});

describe.each(["profiles", "menu_items", "news_posts"])("%s publication flag", (table) => {
	it("defaults to unpublished", async () => {
		const { results } = await env.DB.prepare(`PRAGMA table_info(${table})`).all<ColumnInfo>();
		const published = results.find((column) => column.name === "published");

		expect(published?.dflt_value).toBe("0");
		expect(published?.notnull).toBe(1);
	});
});

describe("publication flag constraint", () => {
	it("rejects a value outside 0 and 1", async () => {
		await expect(
			env.DB.prepare(
				`INSERT INTO profiles (id, category, name, character_name, published, created_at, updated_at)
				 VALUES (?, ?, ?, ?, ?, ?, ?)`,
			)
				.bind("bad-flag", "photography", "Bad Flag", "Someone@Raiden", 2, "now", "now")
				.run(),
		).rejects.toThrow();
	});
});
