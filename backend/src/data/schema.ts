/**
 * Types mirroring the migrations and the public JSON contract.
 *
 * Row types are the storage shape; `Public*` types are what leaves the API.
 * They are deliberately separate — `published` and internal keys must not be
 * able to reach a public response by accident.
 */

/** Wire schema version. A plugin seeing a higher value keeps its bundled data. */
export const SCHEMA_VERSION = 1;

/** The three collections the admin API manages. */
export const RESOURCES = ["profiles", "menu", "news"] as const;
export type Resource = (typeof RESOURCES)[number];

// ---------------------------------------------------------------- profiles

export interface ProfileRow {
	readonly id: string;
	readonly category: string;
	readonly name: string;
	readonly character_name: string;
	readonly age: string;
	readonly affiliation: string;
	readonly occupation: string;
	readonly bio: string;
	/** JSON object, or null. Matches the nested `optional` block in staff_profiles.json. */
	readonly optional_json: string | null;
	/** R2 object key, e.g. `profiles/iris-voss/<sha256>.png`. Never a URL. */
	readonly image_key: string;
	/** Art bundled in the plugin, e.g. `iris_voss.png`. Fallback when no R2 object exists. */
	readonly bundled_image: string;
	/** R2 key of the brand mark, e.g. a resident DJ logo. */
	readonly logo_key: string;
	/** Bundled logo filename, used when there is no R2 object. */
	readonly logo_bundled: string;
	/** Free text describing what a DJ plays. Empty for everyone else. */
	readonly genres: string;
	readonly request_label: string;
	readonly request_message: string;
	readonly published: 0 | 1;
	readonly sort_order: number;
	readonly created_at: string;
	readonly updated_at: string;
	/** Verified email of the last editor. Audit trail only; never public. */
	readonly updated_by: string;
}

/** The optional block, all fields free text and all genuinely optional. */
export interface ProfileOptional {
	readonly pronunciation?: string;
	readonly pronouns?: string;
	readonly race?: string;
	readonly availability?: string;
	readonly quote?: string;
}

export interface PublicProfile {
	readonly id: string;
	readonly category: string;
	readonly name: string;
	readonly characterName: string;
	readonly age: string;
	readonly affiliation: string;
	readonly occupation: string;
	readonly bio: string;
	readonly optional?: ProfileOptional;
	/** Absolute media URL, present only when an R2 object exists. */
	readonly imageUrl?: string;
	/** Bundled art filename to fall back to. */
	readonly bundledImage: string;
	/** Brand mark from R2, present only when one exists. */
	readonly logoUrl?: string;
	/** Bundled logo filename, used when there is no R2 object. */
	readonly logoImage: string;
	/** Present only when set; the deck shows it under the name for DJs. */
	readonly genres?: string;
	readonly requestLabel: string;
	readonly requestMessage: string;
}

// -------------------------------------------------------------------- menu

export interface MenuItemRow {
	readonly id: string;
	readonly name: string;
	readonly price_gil: number;
	readonly ingredients: string;
	readonly description: string;
	readonly taste: string;
	readonly image_key: string;
	readonly bundled_image: string;
	readonly published: 0 | 1;
	readonly sort_order: number;
	readonly created_at: string;
	readonly updated_at: string;
	/** Verified email of the last editor. Audit trail only; never public. */
	readonly updated_by: string;
}

export interface PublicMenuItem {
	readonly id: string;
	readonly name: string;
	readonly priceGil: number;
	/** Display form of `priceGil`, e.g. `10 000`, matching the plugin's drinks card. */
	readonly priceLabel: string;
	readonly ingredients: string;
	readonly description: string;
	readonly taste: string;
	readonly imageUrl?: string;
	readonly bundledImage: string;
}

// -------------------------------------------------------------------- news

export interface NewsPostRow {
	readonly id: string;
	readonly title: string;
	readonly summary: string;
	readonly body: string;
	readonly image_key: string;
	readonly bundled_image: string;
	readonly pinned: 0 | 1;
	/** ISO 8601 UTC instant. When the post becomes visible, and the feed's sort key. */
	readonly published_at: string;
	/** ISO 8601 UTC instant of the event itself, or empty when there is no event. */
	readonly event_at: string;
	readonly link: string;
	readonly link_label: string;
	readonly published: 0 | 1;
	readonly created_at: string;
	readonly updated_at: string;
	/** Verified email of the last editor. Audit trail only; never public. */
	readonly updated_by: string;
}

export interface PublicNewsPost {
	readonly id: string;
	readonly title: string;
	readonly summary: string;
	readonly body: string;
	readonly pinned: boolean;
	readonly publishedAt: string;
	/** Present only when the announcement has an event date. */
	readonly eventAt?: string;
	/** The same instant as a Discord timestamp, ready to paste: `<t:…:F>`. */
	readonly eventDiscord?: string;
	readonly link?: string;
	readonly linkLabel?: string;
	/** Flyer from R2, present only when an object exists. */
	readonly flyerUrl?: string;
	/** Flyer bundled in the plugin, used when there is no R2 object. */
	readonly flyerImage: string;
}

// ------------------------------------------------------------------ shared

/**
 * Formats gil with a thin space between thousands, matching the drinks card
 * the plugin already renders ("10 000 gil").
 */
export function formatGil(priceGil: number): string {
	return priceGil.toLocaleString("en-US").replaceAll(",", " ");
}

/**
 * Discord message timestamp for an instant.
 *
 * Discord renders `<t:seconds:style>` in each reader's own timezone, which is
 * exactly right for a venue whose guests span every region. Style `F` is the
 * long form — "Saturday, 17 August 2026 21:00". The instant is what gets
 * stored; this is a rendering of it, like `priceLabel` is of `priceGil`.
 */
export function toDiscordTimestamp(isoInstant: string, style = "F"): string {
	return `<t:${Math.floor(new Date(isoInstant).getTime() / 1000)}:${style}>`;
}

/**
 * Reads a Discord timestamp back into an ISO instant.
 *
 * Accepted so an announcement can be written by pasting the same string that
 * went into the Discord post, rather than converting it by hand. Any style
 * suffix is accepted and discarded — it only affects rendering.
 */
export function fromDiscordTimestamp(value: string): string | undefined {
	const match = /^<t:(-?\d{1,15})(?::[tTdDfFR])?>$/.exec(value.trim());
	if (match === null) {
		return undefined;
	}

	const instant = new Date(Number(match[1]) * 1000);
	return Number.isNaN(instant.getTime()) ? undefined : instant.toISOString();
}

/**
 * Builds the public URL for an R2 key.
 *
 * Returns undefined for an empty key so callers omit `imageUrl` entirely and
 * the client falls back to bundled art rather than requesting a missing object.
 */
export function mediaUrlFor(baseUrl: string, imageKey: string): string | undefined {
	if (imageKey.length === 0) {
		return undefined;
	}

	return `${baseUrl.replace(/\/+$/, "")}/${imageKey}`;
}
