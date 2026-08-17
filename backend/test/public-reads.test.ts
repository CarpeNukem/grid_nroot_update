import { SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import {
	asAdmin,
	jsonHeaders,
	menuFixture,
	newsFixture,
	profileFixture,
	resetTables,
} from "./helpers.js";

const post = (path: string, body: unknown): Promise<Response> =>
	SELF.fetch(`https://example.com${path}`, {
		method: "POST",
		headers: jsonHeaders(asAdmin()),
		body: JSON.stringify(body),
	});

/** Creates a record and publishes it in one step. */
async function publish(base: string, body: Record<string, unknown>): Promise<void> {
	await post(base, body);
	await post(`${base}/${body.id as string}/publish`, { published: true });
}

describe("public reads", () => {
	beforeEach(resetTables);

	it("returns published profiles in a stable order", async () => {
		await publish("/v1/admin/profiles", profileFixture({ id: "b-second", sortOrder: 2 }));
		await publish("/v1/admin/profiles", profileFixture({ id: "a-first", sortOrder: 1 }));

		const body = (await (await SELF.fetch("https://example.com/v1/profiles")).json()) as {
			profiles: { id: string }[];
		};

		expect(body.profiles.map((profile) => profile.id)).toEqual(["a-first", "b-second"]);
	});

	it("filters profiles by category", async () => {
		await publish("/v1/admin/profiles", profileFixture({ id: "a-first", category: "photography" }));
		await publish("/v1/admin/profiles", profileFixture({ id: "b-second", category: "security" }));

		const body = (await (
			await SELF.fetch("https://example.com/v1/profiles?category=security")
		).json()) as { profiles: { id: string }[] };

		expect(body.profiles.map((profile) => profile.id)).toEqual(["b-second"]);
	});

	it("rejects a malformed category filter", async () => {
		const response = await SELF.fetch("https://example.com/v1/profiles?category=../etc");

		expect(response.status).toBe(400);
		expect(await response.json()).toMatchObject({ error: { code: "INVALID_IDENTIFIER" } });
	});

	it("preserves the nested optional block", async () => {
		await publish(
			"/v1/admin/profiles",
			profileFixture({ optional: { pronouns: "She/Her", race: "Eldritch voidkin" } }),
		);

		const body = (await (await SELF.fetch("https://example.com/v1/profiles/iris-voss")).json()) as {
			profile: { optional: Record<string, string> };
		};

		expect(body.profile.optional).toEqual({ pronouns: "She/Her", race: "Eldritch voidkin" });
	});

	it("survives combining marks and non-Latin text intact", async () => {
		const occupation = "Ē̸̚͝͠r̸̐̉̄͛r̷͔͑̎̾ô̴̓͗͐r̶̋̉͒̓  . . . / / / . . . Photography";
		await publish("/v1/admin/profiles", profileFixture({ occupation }));

		const body = (await (await SELF.fetch("https://example.com/v1/profiles/iris-voss")).json()) as {
			profile: { occupation: string };
		};

		expect(body.profile.occupation).toBe(occupation);
	});

	it("formats gil the way the drinks card renders it", async () => {
		await publish("/v1/admin/menu", menuFixture({ priceGil: 15000 }));

		const body = (await (await SELF.fetch("https://example.com/v1/menu")).json()) as {
			menu: { priceGil: number; priceLabel: string }[];
		};

		expect(body.menu[0]).toMatchObject({ priceGil: 15000, priceLabel: "15 000" });
	});

	it("omits imageUrl when no remote media exists", async () => {
		await publish("/v1/admin/menu", menuFixture());

		const body = (await (await SELF.fetch("https://example.com/v1/menu")).json()) as {
			menu: Record<string, unknown>[];
		};

		expect(body.menu[0]).not.toHaveProperty("imageUrl");
		expect(body.menu[0]).toMatchObject({ bundledImage: "frostbite.png" });
	});

	it("sorts news pinned first, then newest first", async () => {
		await publish(
			"/v1/admin/news",
			newsFixture({ id: "older", publishedAt: "2026-07-01T00:00:00.000Z" }),
		);
		await publish(
			"/v1/admin/news",
			newsFixture({ id: "newer", publishedAt: "2026-08-01T00:00:00.000Z" }),
		);
		await publish(
			"/v1/admin/news",
			newsFixture({ id: "stuck", publishedAt: "2026-01-01T00:00:00.000Z", pinned: true }),
		);

		const body = (await (await SELF.fetch("https://example.com/v1/news")).json()) as {
			news: { id: string }[];
		};

		expect(body.news.map((post) => post.id)).toEqual(["stuck", "newer", "older"]);
	});

	it("hides a future-dated announcement until its time arrives", async () => {
		await publish(
			"/v1/admin/news",
			newsFixture({ id: "not-yet", publishedAt: "2099-01-01T00:00:00.000Z" }),
		);

		const body = (await (await SELF.fetch("https://example.com/v1/news")).json()) as {
			news: unknown[];
		};

		expect(body.news).toHaveLength(0);
		expect((await SELF.fetch("https://example.com/v1/news/not-yet")).status).toBe(404);
	});

	it("returns the event date, its Discord form, link, and flyer", async () => {
		await publish(
			"/v1/admin/news",
			newsFixture({
				eventAt: "<t:1786222800:F>",
				link: "https://discord.gg/example",
				linkLabel: "RSVP on Discord",
				flyerImage: "rooftop.png",
			}),
		);

		const body = (await (
			await SELF.fetch("https://example.com/v1/news/rooftop-reopening")
		).json()) as { post: Record<string, unknown> };

		expect(body.post).toMatchObject({
			eventAt: "2026-08-08T21:00:00.000Z",
			eventDiscord: "<t:1786222800:F>",
			link: "https://discord.gg/example",
			linkLabel: "RSVP on Discord",
			flyerImage: "rooftop.png",
		});
		// No R2 object yet, so the flyer falls back to bundled art.
		expect(body.post).not.toHaveProperty("flyerUrl");
	});

	it("omits event, link, and flyer fields entirely when unset", async () => {
		await publish("/v1/admin/news", newsFixture());

		const body = (await (
			await SELF.fetch("https://example.com/v1/news/rooftop-reopening")
		).json()) as { post: Record<string, unknown> };

		for (const field of ["eventAt", "eventDiscord", "link", "linkLabel", "flyerUrl"]) {
			expect(body.post).not.toHaveProperty(field);
		}
		expect(body.post).toMatchObject({ flyerImage: "" });
	});

	it("keeps the event date independent of when the post goes live", async () => {
		// Announced on the 1st, event on the 8th: visible now, not on the 8th.
		await publish(
			"/v1/admin/news",
			newsFixture({ publishedAt: "2026-08-01", eventAt: "2099-01-01T00:00:00Z" }),
		);

		const body = (await (await SELF.fetch("https://example.com/v1/news")).json()) as {
			news: { eventAt: string }[];
		};

		expect(body.news).toHaveLength(1);
		expect(body.news[0]?.eventAt).toBe("2099-01-01T00:00:00.000Z");
	});

	it("refuses a dangerous link scheme end to end", async () => {
		const response = await post(
			"/v1/admin/news",
			newsFixture({ id: "bad-link", link: "javascript:alert(1)" }),
		);

		expect(response.status).toBe(400);
	});

	it("returns a resource-specific 404 code", async () => {
		const cases = [
			["/v1/profiles/nobody", "PROFILE_NOT_FOUND"],
			["/v1/menu/nothing", "MENU_ITEM_NOT_FOUND"],
			["/v1/news/nothing", "NEWS_POST_NOT_FOUND"],
		] as const;

		for (const [path, code] of cases) {
			const response = await SELF.fetch(`https://example.com${path}`);
			expect(response.status).toBe(404);
			expect(await response.json()).toMatchObject({ error: { code } });
		}
	});
});

describe("catalog", () => {
	beforeEach(resetTables);

	it("carries all three collections in one response", async () => {
		await publish("/v1/admin/profiles", profileFixture());
		await publish("/v1/admin/menu", menuFixture());
		await publish("/v1/admin/news", newsFixture());

		const body = (await (await SELF.fetch("https://example.com/v1/catalog")).json()) as {
			schemaVersion: number;
			mediaRevision: string;
			profiles: unknown[];
			menu: unknown[];
			news: unknown[];
		};

		expect(body.schemaVersion).toBe(1);
		expect(body.profiles).toHaveLength(1);
		expect(body.menu).toHaveLength(1);
		expect(body.news).toHaveLength(1);
		expect(body.mediaRevision).toBe("none");
	});

	it("excludes unpublished records", async () => {
		await post("/v1/admin/menu", menuFixture());

		const body = (await (await SELF.fetch("https://example.com/v1/catalog")).json()) as {
			menu: unknown[];
		};

		expect(body.menu).toHaveLength(0);
	});
});

describe("conditional requests", () => {
	beforeEach(resetTables);

	const cacheablePaths = ["/v1/catalog", "/v1/profiles", "/v1/menu", "/v1/news"];

	it.each(cacheablePaths)("answers 304 for a matching ETag on %s", async (path) => {
		await publish("/v1/admin/menu", menuFixture());

		const first = await SELF.fetch(`https://example.com${path}`);
		const etag = first.headers.get("etag");

		expect(etag).toBeTruthy();
		expect(first.headers.get("cache-control")).toContain("max-age");

		const second = await SELF.fetch(`https://example.com${path}`, {
			headers: { "if-none-match": etag as string },
		});

		expect(second.status).toBe(304);
		expect(await second.text()).toBe("");
		expect(second.headers.get("etag")).toBe(etag);
	});

	it("keeps the ETag stable across repeated identical reads", async () => {
		await publish("/v1/admin/menu", menuFixture());

		const first = await SELF.fetch("https://example.com/v1/menu");
		const second = await SELF.fetch("https://example.com/v1/menu");

		expect(second.headers.get("etag")).toBe(first.headers.get("etag"));
	});

	it("changes the ETag when the content changes", async () => {
		await publish("/v1/admin/menu", menuFixture());
		const before = (await SELF.fetch("https://example.com/v1/menu")).headers.get("etag");

		await SELF.fetch("https://example.com/v1/admin/menu/frostbite", {
			method: "PUT",
			headers: jsonHeaders(asAdmin()),
			body: JSON.stringify(menuFixture({ priceGil: 16000 })),
		});

		const after = (await SELF.fetch("https://example.com/v1/menu")).headers.get("etag");

		expect(after).not.toBe(before);
	});

	it("accepts a weak validator and the wildcard", async () => {
		await publish("/v1/admin/menu", menuFixture());
		const etag = (await SELF.fetch("https://example.com/v1/menu")).headers.get("etag") as string;

		const weak = await SELF.fetch("https://example.com/v1/menu", {
			headers: { "if-none-match": `W/${etag}` },
		});
		const wildcard = await SELF.fetch("https://example.com/v1/menu", {
			headers: { "if-none-match": "*" },
		});

		expect(weak.status).toBe(304);
		expect(wildcard.status).toBe(304);
	});
});
