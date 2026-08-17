import { env } from "cloudflare:test";

/** Shared fixtures and request helpers for the route tests. */

export const ADMIN_EMAIL = "editor@thegrid.test";
export const ORIGIN = "https://example.com";

/** Signs a request in as an administrator using the development sign-in path. */
export const asAdmin = (headers: Record<string, string> = {}): Record<string, string> => ({
	"x-dev-admin-email": ADMIN_EMAIL,
	...headers,
});

export const jsonHeaders = (headers: Record<string, string> = {}): Record<string, string> => ({
	"content-type": "application/json",
	...headers,
});

/**
 * Empties every table and the media bucket.
 *
 * Storage is shared across tests (isolated storage cannot run on Windows — see
 * vitest.config.ts), so each test is responsible for starting clean.
 */
export async function resetTables(): Promise<void> {
	await env.DB.batch([
		env.DB.prepare("DELETE FROM profiles"),
		env.DB.prepare("DELETE FROM menu_items"),
		env.DB.prepare("DELETE FROM news_posts"),
	]);

	const stored = await env.MEDIA.list();
	if (stored.objects.length > 0) {
		await env.MEDIA.delete(stored.objects.map((object) => object.key));
	}
}

export const profileFixture = (
	overrides: Record<string, unknown> = {},
): Record<string, unknown> => ({
	id: "iris-voss",
	category: "photography",
	name: "Iris Voss",
	characterName: "Damona Dawnfeather@Raiden",
	bio: "Rumored to formally be Corpo.. or still is.",
	image: "iris_voss.png",
	requestLabel: "REQUEST A PHOTOSHOOT",
	requestMessage: "Hello, I'd like to request a photoshoot.",
	...overrides,
});

export const menuFixture = (overrides: Record<string, unknown> = {}): Record<string, unknown> => ({
	id: "frostbite",
	name: "Frostbite",
	priceGil: 15000,
	ingredients: "ceruleum-infused vodka, blue curacao, synth-mint",
	description: "A neon-blue cryo shot with a sharp mint-citrus bite.",
	taste: "Sharp, cold, and electric.",
	bundledImage: "frostbite.png",
	...overrides,
});

export const newsFixture = (overrides: Record<string, unknown> = {}): Record<string, unknown> => ({
	id: "rooftop-reopening",
	title: "The rooftop reopens",
	summary: "Doors open again this weekend.",
	body: "The rooftop deck is back online after maintenance.",
	publishedAt: "2026-08-01T20:00:00.000Z",
	...overrides,
});
