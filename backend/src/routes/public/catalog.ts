import { BOARD_DEPTH, listAllScores, toPublicBoards } from "../../data/arcade.js";
import { listPublishedMenuItems, toPublicMenuItem } from "../../data/menu.js";
import { listPublishedNewsPosts, toPublicNewsPost } from "../../data/news.js";
import { listPublishedPages, toPublicPage } from "../../data/pages.js";
import { listPublishedProfiles, toPublicProfile } from "../../data/profiles.js";
import { SCHEMA_VERSION } from "../../data/schema.js";
import { cachedRead } from "../../cache.js";
import { conditionalReadResponse } from "../../http.js";
import { logUnhandledError } from "../../observability/log.js";
import type { Route } from "../router.js";
import { latestUpdatedAt } from "./shared.js";

/**
 * Everything the plugin needs in one request.
 *
 * The plugin refreshes on a slow timer, so one round trip that can answer 304
 * is cheaper than three that each might not. The per-collection routes remain
 * available for the admin site and for anything that wants a narrower read.
 *
 * The arcade's high-score boards ride along for the same reason, and have no
 * route of their own: a deck that shows them makes no request it was not
 * already making. The price is that a new score changes these bytes, so each
 * deck's next check is a full response rather than a 304 — the same number of
 * requests, a few more kilobytes, and only when a board actually moved.
 */

/**
 * A short digest of every media key currently referenced.
 *
 * Lets a client decide whether its downloaded-asset cache is still current
 * without diffing individual records. It changes only when a media key is
 * added, removed, or repointed — the keys are content-hashed, so a replaced
 * image is a new key.
 */
async function mediaRevisionFor(keys: readonly string[]): Promise<string> {
	const present = keys.filter((key) => key.length > 0).sort();
	if (present.length === 0) {
		return "none";
	}

	const digest = await crypto.subtle.digest(
		"SHA-256",
		new TextEncoder().encode(present.join("\n")),
	);
	const hex = [...new Uint8Array(digest)]
		.slice(0, 16)
		.map((byte) => byte.toString(16).padStart(2, "0"))
		.join("");

	return `sha256-${hex}`;
}

export const catalogRoute: Route = {
	method: "GET",
	pattern: "/v1/catalog",
	handler: async (request, { env, requestId }) => {
		// The five queries below run only when the edge has nothing current. A
		// conditional request that would have answered 304 used to pay for all of
		// them anyway, because the tag is derived from the body.
		const { serialized, etag, cached } = await cachedRead(request, async () => {
			const instant = new Date();
			const now = instant.toISOString();
			const [profileRows, menuRows, newsRows, pageRows, scoreRows] = await Promise.all([
				listPublishedProfiles(env.DB),
				listPublishedMenuItems(env.DB),
				listPublishedNewsPosts(env.DB, now),
				listPublishedPages(env.DB),
				// The boards are a passenger in this response and must never be
				// what stops it. If their table cannot be read — a deploy that
				// ran ahead of its migration — the menu and the profiles still
				// go out, and the catalogue simply has no boards in it.
				listAllScores(env.DB).catch((error: unknown) => {
					logUnhandledError(requestId, "/v1/catalog (arcade boards)", error);

					return undefined;
				}),
			]);

			const mediaRevision = await mediaRevisionFor([
				...profileRows.map((row) => row.image_key),
				...menuRows.map((row) => row.image_key),
				...newsRows.map((row) => row.image_key),
			]);

			return {
				schemaVersion: SCHEMA_VERSION,
				updatedAt: latestUpdatedAt([...profileRows, ...menuRows, ...newsRows, ...pageRows]),
				mediaRevision,
				profiles: profileRows.map((row) => toPublicProfile(row, env.PUBLIC_MEDIA_BASE_URL)),
				menu: menuRows.map((row) => toPublicMenuItem(row, env.PUBLIC_MEDIA_BASE_URL)),
				news: newsRows.map((row) => toPublicNewsPost(row, env.PUBLIC_MEDIA_BASE_URL)),
				pages: pageRows.map(toPublicPage),
				// Not part of `updatedAt` above: that says when the venue last
				// edited something, and a high score is not an edit.
				// The depth travels with the boards so a deck knows the bar a score
				// has to clear without a constant of its own to drift from this one.
				// Left out altogether when the boards could not be read, which a
				// deck takes to mean there is nowhere to post a score either.
				...(scoreRows === undefined
					? {}
					: { arcade: { depth: BOARD_DEPTH, boards: toPublicBoards(scoreRows, instant) } }),
			};
		});

		const response = conditionalReadResponse(request, serialized, etag, requestId);
		// Whether the edge served this is the one thing you cannot infer from the
		// outside, and it is exactly what you want to know when the cache is the
		// thing keeping the database bill down.
		response.headers.set("x-cache", cached ? "HIT" : "MISS");

		return response;
	},
};
