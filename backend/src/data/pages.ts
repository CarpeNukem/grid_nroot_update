import type { PageInput } from "./validation.js";

/** Prose-block storage. Same parameterisation rules as profiles.ts. */

export interface PageRow {
	readonly id: string;
	readonly title: string;
	/** Markdown. Rendered as a subset by the deck; never as HTML. */
	readonly body: string;
	readonly published: 0 | 1;
	readonly sort_order: number;
	readonly created_at: string;
	readonly updated_at: string;
	/** Verified email of the last editor. Audit trail only; never public. */
	readonly updated_by: string;
}

export interface PublicPage {
	readonly id: string;
	readonly title: string;
	readonly body: string;
}

const COLUMNS = `id, title, body, published, sort_order, created_at, updated_at, updated_by`;

const PUBLIC_ORDER = "ORDER BY sort_order ASC, id ASC";

export const toPublicPage = (row: PageRow): PublicPage => ({
	id: row.id,
	title: row.title,
	body: row.body,
});

export async function listPublishedPages(db: D1Database): Promise<PageRow[]> {
	const { results } = await db
		.prepare(`SELECT ${COLUMNS} FROM pages WHERE published = 1 ${PUBLIC_ORDER}`)
		.all<PageRow>();

	return results;
}

export async function getPublishedPage(db: D1Database, id: string): Promise<PageRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM pages WHERE id = ? AND published = 1`)
		.bind(id)
		.first<PageRow>();

	return row ?? undefined;
}

/** Admin listing. Includes unpublished rows. */
export async function listAllPages(db: D1Database): Promise<PageRow[]> {
	const { results } = await db
		.prepare(`SELECT ${COLUMNS} FROM pages ${PUBLIC_ORDER}`)
		.all<PageRow>();
	return results;
}

export async function getPage(db: D1Database, id: string): Promise<PageRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM pages WHERE id = ?`)
		.bind(id)
		.first<PageRow>();

	return row ?? undefined;
}

/** Creates or replaces a page without touching `published` or `created_at`. */
export async function upsertPage(
	db: D1Database,
	input: PageInput,
	now: string,
	editor: string,
): Promise<void> {
	await db
		.prepare(
			`INSERT INTO pages (id, title, body, sort_order, created_at, updated_at, updated_by)
			 VALUES (?, ?, ?, ?, ?, ?, ?)
			 ON CONFLICT(id) DO UPDATE SET
				title = excluded.title,
				body = excluded.body,
				sort_order = excluded.sort_order,
				updated_at = excluded.updated_at,
				updated_by = excluded.updated_by`,
		)
		.bind(input.id, input.title, input.body, input.sortOrder, now, now, editor)
		.run();
}

export async function deletePage(db: D1Database, id: string): Promise<boolean> {
	const result = await db.prepare("DELETE FROM pages WHERE id = ?").bind(id).run();
	return (result.meta.changes ?? 0) > 0;
}

export async function setPagePublished(
	db: D1Database,
	id: string,
	published: boolean,
	now: string,
	editor: string,
): Promise<boolean> {
	const result = await db
		.prepare("UPDATE pages SET published = ?, updated_at = ?, updated_by = ? WHERE id = ?")
		.bind(published ? 1 : 0, now, editor, id)
		.run();

	return (result.meta.changes ?? 0) > 0;
}
