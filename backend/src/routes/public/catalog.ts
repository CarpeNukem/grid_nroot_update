import { listPublishedMenuItems, toPublicMenuItem } from "../../data/menu.js";
import { listPublishedNewsPosts, toPublicNewsPost } from "../../data/news.js";
import { listPublishedPages, toPublicPage } from "../../data/pages.js";
import { listPublishedProfiles, toPublicProfile } from "../../data/profiles.js";
import { SCHEMA_VERSION } from "../../data/schema.js";
import { publicReadResponse } from "../../http.js";
import type { Route } from "../router.js";
import { latestUpdatedAt } from "./shared.js";

/**
 * Everything the plugin needs in one request.
 *
 * The plugin refreshes on a slow timer, so one round trip that can answer 304
 * is cheaper than three that each might not. The per-collection routes remain
 * available for the admin site and for anything that wants a narrower read.
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
		const now = new Date().toISOString();
		const [profileRows, menuRows, newsRows, pageRows] = await Promise.all([
			listPublishedProfiles(env.DB),
			listPublishedMenuItems(env.DB),
			listPublishedNewsPosts(env.DB, now),
			listPublishedPages(env.DB),
		]);

		const mediaRevision = await mediaRevisionFor([
			...profileRows.map((row) => row.image_key),
			...menuRows.map((row) => row.image_key),
			...newsRows.map((row) => row.image_key),
		]);

		return publicReadResponse(
			request,
			{
				schemaVersion: SCHEMA_VERSION,
				updatedAt: latestUpdatedAt([...profileRows, ...menuRows, ...newsRows, ...pageRows]),
				mediaRevision,
				profiles: profileRows.map((row) => toPublicProfile(row, env.PUBLIC_MEDIA_BASE_URL)),
				menu: menuRows.map((row) => toPublicMenuItem(row, env.PUBLIC_MEDIA_BASE_URL)),
				news: newsRows.map((row) => toPublicNewsPost(row, env.PUBLIC_MEDIA_BASE_URL)),
				pages: pageRows.map(toPublicPage),
			},
			requestId,
		);
	},
};
