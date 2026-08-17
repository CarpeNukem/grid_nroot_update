import { env, SELF } from "cloudflare:test";
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

const put = (path: string, body: unknown): Promise<Response> =>
	SELF.fetch(`https://example.com${path}`, {
		method: "PUT",
		headers: jsonHeaders(asAdmin()),
		body: JSON.stringify(body),
	});

const del = (path: string): Promise<Response> =>
	SELF.fetch(`https://example.com${path}`, { method: "DELETE", headers: asAdmin() });

const get = (path: string): Promise<Response> =>
	SELF.fetch(`https://example.com${path}`, { headers: asAdmin() });

describe.each([
	["profiles", "/v1/admin/profiles", profileFixture, "iris-voss"],
	["menu", "/v1/admin/menu", menuFixture, "frostbite"],
	["news", "/v1/admin/news", newsFixture, "rooftop-reopening"],
] as const)("admin CRUD: %s", (_label, base, fixture, id) => {
	beforeEach(resetTables);

	it("creates a record, unpublished", async () => {
		const response = await post(base, fixture());

		expect(response.status).toBe(201);
		expect(response.headers.get("location")).toBe(`${base}/${id}`);

		const body = (await response.json()) as { item: { id: string; published: number } };
		expect(body.item.id).toBe(id);
		expect(body.item.published).toBe(0);
	});

	it("refuses to overwrite an existing id on create", async () => {
		await post(base, fixture());
		const second = await post(base, fixture());

		expect(second.status).toBe(409);
		expect(await second.json()).toMatchObject({ error: { code: "ALREADY_EXISTS" } });
	});

	it("updates through PUT without republishing", async () => {
		await post(base, fixture());
		await post(`${base}/${id}/publish`, { published: true });

		const updated = await put(`${base}/${id}`, fixture());
		expect(updated.status).toBe(200);

		const body = (await updated.json()) as { item: { published: number } };
		expect(body.item.published).toBe(1);
	});

	it("ignores an id in the body and keeps the path id authoritative", async () => {
		await post(base, fixture());
		const response = await put(`${base}/${id}`, fixture({ id: "somewhere-else" }));

		expect(response.status).toBe(200);
		expect((await response.json()) as { item: { id: string } }).toMatchObject({
			item: { id },
		});
		expect(await (await get(`${base}/somewhere-else`)).status).toBe(404);
	});

	it("publishes and unpublishes explicitly", async () => {
		await post(base, fixture());

		const published = (await (await post(`${base}/${id}/publish`, { published: true })).json()) as {
			item: { published: number };
		};
		expect(published.item.published).toBe(1);

		const hidden = (await (await post(`${base}/${id}/publish`, { published: false })).json()) as {
			item: { published: number };
		};
		expect(hidden.item.published).toBe(0);
	});

	it("deletes a record", async () => {
		await post(base, fixture());

		expect((await del(`${base}/${id}`)).status).toBe(200);
		expect((await get(`${base}/${id}`)).status).toBe(404);
	});

	it("returns the standard envelope for a missing record", async () => {
		for (const response of [
			await get(`${base}/does-not-exist`),
			await del(`${base}/does-not-exist`),
			await post(`${base}/does-not-exist/publish`, { published: true }),
		]) {
			expect(response.status).toBe(404);
			expect(await response.json()).toMatchObject({ error: { code: expect.any(String) } });
		}
	});

	it("rejects a malformed id in the path", async () => {
		const response = await get(`${base}/Not_A_Slug`);

		expect(response.status).toBe(400);
		expect(await response.json()).toMatchObject({ error: { code: "INVALID_IDENTIFIER" } });
	});

	it("rejects an unrecognised field", async () => {
		const response = await post(base, fixture({ colour: "neon" }));

		expect(response.status).toBe(400);
		expect(await response.json()).toMatchObject({ error: { code: "BAD_REQUEST" } });
	});

	it("refuses to set published through create or update", async () => {
		const created = await post(base, fixture({ published: 1 }));

		expect(created.status).toBe(400);
	});

	it("rejects a body that is not JSON", async () => {
		const response = await SELF.fetch(`https://example.com${base}`, {
			method: "POST",
			headers: jsonHeaders(asAdmin()),
			body: "{not json",
		});

		expect(response.status).toBe(400);
	});

	it("rejects an oversized body", async () => {
		const response = await post(base, fixture({ description: "x".repeat(70_000) }));

		expect(response.status).toBe(413);
	});

	it("keeps unpublished records out of the public read", async () => {
		await post(base, fixture());

		const publicPath = base.replace("/admin", "");
		const list = await SELF.fetch(`https://example.com${publicPath}`);
		const body = (await list.json()) as Record<string, unknown[]>;
		const collection = Object.values(body).find(Array.isArray) ?? [];

		expect(collection).toHaveLength(0);
		expect((await SELF.fetch(`https://example.com${publicPath}/${id}`)).status).toBe(404);
	});

	it("exposes the record publicly once published", async () => {
		await post(base, fixture());
		await post(`${base}/${id}/publish`, { published: true });

		const publicPath = base.replace("/admin", "");
		const response = await SELF.fetch(`https://example.com${publicPath}/${id}`);

		expect(response.status).toBe(200);
	});

	it("never leaks the editor audit trail publicly", async () => {
		await post(base, fixture());
		await post(`${base}/${id}/publish`, { published: true });

		const publicPath = base.replace("/admin", "");
		const body = await (await SELF.fetch(`https://example.com${publicPath}/${id}`)).text();

		expect(body).not.toContain("updated_by");
		expect(body).not.toContain("@thegrid.test");
	});
});

describe("admin listing", () => {
	beforeEach(resetTables);

	it("includes unpublished records", async () => {
		await post("/v1/admin/menu", menuFixture());

		const body = (await (await get("/v1/admin/menu")).json()) as { items: { published: number }[] };

		expect(body.items).toHaveLength(1);
		expect(body.items[0]?.published).toBe(0);
	});

	it("is never cached", async () => {
		const response = await get("/v1/admin/menu");

		expect(response.headers.get("cache-control")).toBe("no-store");
	});
});

describe("news validation", () => {
	beforeEach(resetTables);

	it("rejects an impossible date", async () => {
		const response = await post("/v1/admin/news", newsFixture({ publishedAt: "2026-02-30" }));

		expect(response.status).toBe(400);
	});

	it("normalises a date-only value to a UTC instant", async () => {
		await post("/v1/admin/news", newsFixture({ publishedAt: "2026-08-01" }));

		const row = await env.DB.prepare("SELECT published_at FROM news_posts WHERE id = ?")
			.bind("rooftop-reopening")
			.first<{ published_at: string }>();

		expect(row?.published_at).toBe("2026-08-01T00:00:00.000Z");
	});
});
