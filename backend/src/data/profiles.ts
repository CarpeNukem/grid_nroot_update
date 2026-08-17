import {
	mediaUrlFor,
	type ProfileOptional,
	type ProfileRow,
	type PublicProfile,
} from "./schema.js";
import type { ProfileInput } from "./validation.js";

/**
 * Profile storage.
 *
 * Every query is parameterised and every column list is written out literally —
 * no identifier is ever interpolated, so there is no path for a field name to
 * come from a request.
 */

const COLUMNS = `id, category, name, character_name, age, affiliation, occupation, bio,
	optional_json, image_key, bundled_image, logo_key, logo_bundled,
	request_label, request_message,
	published, sort_order, created_at, updated_at, updated_by`;

/** Stable public ordering: category, then explicit order, then id as a tiebreak. */
const PUBLIC_ORDER = "ORDER BY category ASC, sort_order ASC, id ASC";

/**
 * Reconstructs the nested `optional` block.
 *
 * Malformed JSON is treated as absent rather than fatal: one bad row should
 * cost that row its optional details, not take down the whole directory.
 */
function parseOptional(optionalJson: string | null): ProfileOptional | undefined {
	if (optionalJson === null || optionalJson.length === 0) {
		return undefined;
	}

	try {
		const parsed: unknown = JSON.parse(optionalJson);
		if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
			return undefined;
		}

		return parsed as ProfileOptional;
	} catch {
		return undefined;
	}
}

export function toPublicProfile(row: ProfileRow, mediaBaseUrl: string): PublicProfile {
	const optional = parseOptional(row.optional_json);
	const imageUrl = mediaUrlFor(mediaBaseUrl, row.image_key);
	const logoUrl = mediaUrlFor(mediaBaseUrl, row.logo_key);

	return {
		id: row.id,
		category: row.category,
		name: row.name,
		characterName: row.character_name,
		age: row.age,
		affiliation: row.affiliation,
		occupation: row.occupation,
		bio: row.bio,
		...(optional === undefined ? {} : { optional }),
		...(imageUrl === undefined ? {} : { imageUrl }),
		bundledImage: row.bundled_image,
		...(logoUrl === undefined ? {} : { logoUrl }),
		logoImage: row.logo_bundled,
		requestLabel: row.request_label,
		requestMessage: row.request_message,
	};
}

export async function listPublishedProfiles(
	db: D1Database,
	category?: string,
): Promise<ProfileRow[]> {
	const statement =
		category === undefined
			? db.prepare(`SELECT ${COLUMNS} FROM profiles WHERE published = 1 ${PUBLIC_ORDER}`)
			: db
					.prepare(
						`SELECT ${COLUMNS} FROM profiles WHERE published = 1 AND category = ? ${PUBLIC_ORDER}`,
					)
					.bind(category);

	const { results } = await statement.all<ProfileRow>();
	return results;
}

export async function getPublishedProfile(
	db: D1Database,
	id: string,
): Promise<ProfileRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM profiles WHERE id = ? AND published = 1`)
		.bind(id)
		.first<ProfileRow>();

	return row ?? undefined;
}

/** Admin listing. Includes unpublished rows and must never back a public route. */
export async function listAllProfiles(db: D1Database): Promise<ProfileRow[]> {
	const { results } = await db
		.prepare(`SELECT ${COLUMNS} FROM profiles ${PUBLIC_ORDER}`)
		.all<ProfileRow>();

	return results;
}

export async function getProfile(db: D1Database, id: string): Promise<ProfileRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM profiles WHERE id = ?`)
		.bind(id)
		.first<ProfileRow>();

	return row ?? undefined;
}

/**
 * Creates or replaces a profile.
 *
 * `published` and `created_at` are absent from the conflict clause on purpose:
 * re-importing or editing a record must not silently publish it, and must not
 * rewrite when it first appeared.
 */
export async function upsertProfile(
	db: D1Database,
	input: ProfileInput,
	now: string,
	editor: string,
): Promise<void> {
	await db
		.prepare(
			`INSERT INTO profiles (
				id, category, name, character_name, age, affiliation, occupation, bio,
				optional_json, image_key, bundled_image, logo_key, logo_bundled,
				request_label, request_message,
				sort_order, created_at, updated_at, updated_by
			) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
			ON CONFLICT(id) DO UPDATE SET
				category = excluded.category,
				name = excluded.name,
				character_name = excluded.character_name,
				age = excluded.age,
				affiliation = excluded.affiliation,
				occupation = excluded.occupation,
				bio = excluded.bio,
				optional_json = excluded.optional_json,
				image_key = excluded.image_key,
				bundled_image = excluded.bundled_image,
				logo_key = excluded.logo_key,
				logo_bundled = excluded.logo_bundled,
				request_label = excluded.request_label,
				request_message = excluded.request_message,
				sort_order = excluded.sort_order,
				updated_at = excluded.updated_at,
				updated_by = excluded.updated_by`,
		)
		.bind(
			input.id,
			input.category,
			input.name,
			input.characterName,
			input.age,
			input.affiliation,
			input.occupation,
			input.bio,
			input.optional === null ? null : JSON.stringify(input.optional),
			input.imageKey,
			input.bundledImage,
			input.logoKey,
			input.logoImage,
			input.requestLabel,
			input.requestMessage,
			input.sortOrder,
			now,
			now,
			editor,
		)
		.run();
}

export async function deleteProfile(db: D1Database, id: string): Promise<boolean> {
	const result = await db.prepare("DELETE FROM profiles WHERE id = ?").bind(id).run();
	return (result.meta.changes ?? 0) > 0;
}

export async function setProfilePublished(
	db: D1Database,
	id: string,
	published: boolean,
	now: string,
	editor: string,
): Promise<boolean> {
	const result = await db
		.prepare("UPDATE profiles SET published = ?, updated_at = ?, updated_by = ? WHERE id = ?")
		.bind(published ? 1 : 0, now, editor, id)
		.run();

	return (result.meta.changes ?? 0) > 0;
}
