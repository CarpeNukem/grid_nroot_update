import { SCHEMA_VERSION } from "../../data/schema.js";

/**
 * Pieces shared by the public read routes.
 *
 * The important rule here concerns `updatedAt`. Public reads are validated with
 * an ETag over the response bytes, so the body must be a pure function of the
 * data — stamping the current time into it would change the hash on every
 * request and no client would ever get a 304. `updatedAt` is therefore derived
 * from the rows themselves, which is also the more useful signal: it says when
 * the content last changed, not when this request happened.
 */

const EPOCH = "1970-01-01T00:00:00.000Z";

export function latestUpdatedAt(rows: readonly { readonly updated_at: string }[]): string {
	let latest = EPOCH;
	for (const row of rows) {
		if (row.updated_at > latest) {
			latest = row.updated_at;
		}
	}

	return latest;
}

/** Envelope every public collection response shares. */
export function collectionBody<T>(
	key: string,
	items: readonly T[],
	rows: readonly { readonly updated_at: string }[],
): Record<string, unknown> {
	return {
		schemaVersion: SCHEMA_VERSION,
		updatedAt: latestUpdatedAt(rows),
		[key]: items,
	};
}
