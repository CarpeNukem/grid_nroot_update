import { getPublishedNewsPost, listPublishedNewsPosts, toPublicNewsPost } from "../../data/news.js";
import { publicReadResponse } from "../../http.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import { assertIdentifier } from "../../security/validate.js";
import type { Route } from "../router.js";
import { collectionBody } from "./shared.js";

/**
 * Published announcements.
 *
 * A post dated in the future stays hidden until that moment arrives, so an
 * announcement can be written and published ahead of the night it belongs to.
 */

export const newsListRoute: Route = {
	method: "GET",
	pattern: "/v1/news",
	handler: async (request, { env, requestId }) => {
		const rows = await listPublishedNewsPosts(env.DB, new Date().toISOString());
		const news = rows.map((row) => toPublicNewsPost(row, env.PUBLIC_MEDIA_BASE_URL));

		return publicReadResponse(request, collectionBody("news", news, rows), requestId);
	},
};

export const newsDetailRoute: Route = {
	method: "GET",
	pattern: "/v1/news/:id",
	handler: async (request, { env, requestId, params }) => {
		const id = assertIdentifier(params.id, "news post id");
		const row = await getPublishedNewsPost(env.DB, id, new Date().toISOString());

		if (row === undefined) {
			throw new ApiError(
				404,
				ErrorCode.NEWS_POST_NOT_FOUND,
				"The requested announcement is unavailable.",
			);
		}

		return publicReadResponse(
			request,
			{ post: toPublicNewsPost(row, env.PUBLIC_MEDIA_BASE_URL) },
			requestId,
		);
	},
};
