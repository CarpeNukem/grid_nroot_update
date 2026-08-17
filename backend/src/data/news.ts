import {
	mediaUrlFor,
	type NewsPostRow,
	type PublicNewsPost,
	toDiscordTimestamp,
} from "./schema.js";
import type { NewsPostInput } from "./validation.js";

/** Announcement storage. Same parameterisation rules as profiles.ts. */

const COLUMNS = `id, title, summary, body, image_key, bundled_image,
	pinned, published_at, event_at, link, link_label,
	published, created_at, updated_at, updated_by`;

/** Pinned first, then newest first, with id as a deterministic tiebreak. */
const PUBLIC_ORDER = "ORDER BY pinned DESC, published_at DESC, id ASC";

/**
 * Wire shape for one announcement.
 *
 * Optional fields are omitted rather than sent empty, so a client can treat
 * "absent" as "do not render this row" without also testing for empty strings.
 * `eventDiscord` is derived here so the plugin can offer a copy-to-clipboard
 * that pastes straight into Discord.
 */
export function toPublicNewsPost(row: NewsPostRow, mediaBaseUrl: string): PublicNewsPost {
	const flyerUrl = mediaUrlFor(mediaBaseUrl, row.image_key);
	const hasEvent = row.event_at.length > 0;

	return {
		id: row.id,
		title: row.title,
		summary: row.summary,
		body: row.body,
		pinned: row.pinned === 1,
		publishedAt: row.published_at,
		...(hasEvent ? { eventAt: row.event_at, eventDiscord: toDiscordTimestamp(row.event_at) } : {}),
		...(row.link.length === 0 ? {} : { link: row.link }),
		...(row.link_label.length === 0 ? {} : { linkLabel: row.link_label }),
		...(flyerUrl === undefined ? {} : { flyerUrl }),
		flyerImage: row.bundled_image,
	};
}

/**
 * The public feed.
 *
 * A post dated in the future stays hidden until that time passes, so an
 * announcement can be written and published ahead of the night it belongs to.
 */
export async function listPublishedNewsPosts(
	db: D1Database,
	now: string,
	limit = 50,
): Promise<NewsPostRow[]> {
	const { results } = await db
		.prepare(
			`SELECT ${COLUMNS} FROM news_posts
			 WHERE published = 1 AND published_at <= ?
			 ${PUBLIC_ORDER}
			 LIMIT ?`,
		)
		.bind(now, limit)
		.all<NewsPostRow>();

	return results;
}

export async function getPublishedNewsPost(
	db: D1Database,
	id: string,
	now: string,
): Promise<NewsPostRow | undefined> {
	const row = await db
		.prepare(
			`SELECT ${COLUMNS} FROM news_posts WHERE id = ? AND published = 1 AND published_at <= ?`,
		)
		.bind(id, now)
		.first<NewsPostRow>();

	return row ?? undefined;
}

/** Admin listing. Includes unpublished and future-dated posts. */
export async function listAllNewsPosts(db: D1Database): Promise<NewsPostRow[]> {
	const { results } = await db
		.prepare(`SELECT ${COLUMNS} FROM news_posts ${PUBLIC_ORDER}`)
		.all<NewsPostRow>();

	return results;
}

export async function getNewsPost(db: D1Database, id: string): Promise<NewsPostRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM news_posts WHERE id = ?`)
		.bind(id)
		.first<NewsPostRow>();

	return row ?? undefined;
}

/** Creates or replaces a post without touching `published` or `created_at`. */
export async function upsertNewsPost(
	db: D1Database,
	input: NewsPostInput,
	now: string,
	editor: string,
): Promise<void> {
	await db
		.prepare(
			`INSERT INTO news_posts (
				id, title, summary, body, image_key, bundled_image,
				pinned, published_at, event_at, link, link_label,
				created_at, updated_at, updated_by
			) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
			ON CONFLICT(id) DO UPDATE SET
				title = excluded.title,
				summary = excluded.summary,
				body = excluded.body,
				image_key = excluded.image_key,
				bundled_image = excluded.bundled_image,
				pinned = excluded.pinned,
				published_at = excluded.published_at,
				event_at = excluded.event_at,
				link = excluded.link,
				link_label = excluded.link_label,
				updated_at = excluded.updated_at,
				updated_by = excluded.updated_by`,
		)
		.bind(
			input.id,
			input.title,
			input.summary,
			input.body,
			input.imageKey,
			input.bundledImage,
			input.pinned ? 1 : 0,
			input.publishedAt,
			input.eventAt,
			input.link,
			input.linkLabel,
			now,
			now,
			editor,
		)
		.run();
}

export async function deleteNewsPost(db: D1Database, id: string): Promise<boolean> {
	const result = await db.prepare("DELETE FROM news_posts WHERE id = ?").bind(id).run();
	return (result.meta.changes ?? 0) > 0;
}

export async function setNewsPostPublished(
	db: D1Database,
	id: string,
	published: boolean,
	now: string,
	editor: string,
): Promise<boolean> {
	const result = await db
		.prepare("UPDATE news_posts SET published = ?, updated_at = ?, updated_by = ? WHERE id = ?")
		.bind(published ? 1 : 0, now, editor, id)
		.run();

	return (result.meta.changes ?? 0) > 0;
}
