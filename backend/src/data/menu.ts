import { formatGil, type MenuItemRow, mediaUrlFor, type PublicMenuItem } from "./schema.js";
import type { MenuItemInput } from "./validation.js";

/** Drinks-card storage. Same parameterisation rules as profiles.ts. */

const COLUMNS = `id, name, price_gil, ingredients, description, taste,
	image_key, bundled_image, published, sort_order, created_at, updated_at, updated_by`;

/** The drinks card is a curated running order, so explicit order wins. */
const PUBLIC_ORDER = "ORDER BY sort_order ASC, id ASC";

export function toPublicMenuItem(row: MenuItemRow, mediaBaseUrl: string): PublicMenuItem {
	const imageUrl = mediaUrlFor(mediaBaseUrl, row.image_key);

	return {
		id: row.id,
		name: row.name,
		priceGil: row.price_gil,
		priceLabel: formatGil(row.price_gil),
		ingredients: row.ingredients,
		description: row.description,
		taste: row.taste,
		...(imageUrl === undefined ? {} : { imageUrl }),
		bundledImage: row.bundled_image,
	};
}

export async function listPublishedMenuItems(db: D1Database): Promise<MenuItemRow[]> {
	const { results } = await db
		.prepare(`SELECT ${COLUMNS} FROM menu_items WHERE published = 1 ${PUBLIC_ORDER}`)
		.all<MenuItemRow>();

	return results;
}

export async function getPublishedMenuItem(
	db: D1Database,
	id: string,
): Promise<MenuItemRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM menu_items WHERE id = ? AND published = 1`)
		.bind(id)
		.first<MenuItemRow>();

	return row ?? undefined;
}

/** Admin listing. Includes unpublished rows. */
export async function listAllMenuItems(db: D1Database): Promise<MenuItemRow[]> {
	const { results } = await db
		.prepare(`SELECT ${COLUMNS} FROM menu_items ${PUBLIC_ORDER}`)
		.all<MenuItemRow>();

	return results;
}

export async function getMenuItem(db: D1Database, id: string): Promise<MenuItemRow | undefined> {
	const row = await db
		.prepare(`SELECT ${COLUMNS} FROM menu_items WHERE id = ?`)
		.bind(id)
		.first<MenuItemRow>();

	return row ?? undefined;
}

/** Creates or replaces an item without touching `published` or `created_at`. */
export async function upsertMenuItem(
	db: D1Database,
	input: MenuItemInput,
	now: string,
	editor: string,
): Promise<void> {
	await db
		.prepare(
			`INSERT INTO menu_items (
				id, name, price_gil, ingredients, description, taste,
				image_key, bundled_image, sort_order, created_at, updated_at, updated_by
			) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
			ON CONFLICT(id) DO UPDATE SET
				name = excluded.name,
				price_gil = excluded.price_gil,
				ingredients = excluded.ingredients,
				description = excluded.description,
				taste = excluded.taste,
				image_key = excluded.image_key,
				bundled_image = excluded.bundled_image,
				sort_order = excluded.sort_order,
				updated_at = excluded.updated_at,
				updated_by = excluded.updated_by`,
		)
		.bind(
			input.id,
			input.name,
			input.priceGil,
			input.ingredients,
			input.description,
			input.taste,
			input.imageKey,
			input.bundledImage,
			input.sortOrder,
			now,
			now,
			editor,
		)
		.run();
}

export async function deleteMenuItem(db: D1Database, id: string): Promise<boolean> {
	const result = await db.prepare("DELETE FROM menu_items WHERE id = ?").bind(id).run();
	return (result.meta.changes ?? 0) > 0;
}

export async function setMenuItemPublished(
	db: D1Database,
	id: string,
	published: boolean,
	now: string,
	editor: string,
): Promise<boolean> {
	const result = await db
		.prepare("UPDATE menu_items SET published = ?, updated_at = ?, updated_by = ? WHERE id = ?")
		.bind(published ? 1 : 0, now, editor, id)
		.run();

	return (result.meta.changes ?? 0) > 0;
}
