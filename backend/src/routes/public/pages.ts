import { getPublishedPage, listPublishedPages, toPublicPage } from "../../data/pages.js";
import { publicReadResponse } from "../../http.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import { assertIdentifier } from "../../security/validate.js";
import type { Route } from "../router.js";
import { collectionBody } from "./shared.js";

/** Published prose blocks — the Wi-Fi screen and anything like it. */

export const pageListRoute: Route = {
	method: "GET",
	pattern: "/v1/pages",
	handler: async (request, { env, requestId }) => {
		const rows = await listPublishedPages(env.DB);

		return publicReadResponse(
			request,
			collectionBody("pages", rows.map(toPublicPage), rows),
			requestId,
		);
	},
};

export const pageDetailRoute: Route = {
	method: "GET",
	pattern: "/v1/pages/:id",
	handler: async (request, { env, requestId, params }) => {
		const id = assertIdentifier(params.id, "page id");
		const row = await getPublishedPage(env.DB, id);

		if (row === undefined) {
			throw new ApiError(404, ErrorCode.PAGE_NOT_FOUND, "The requested page is unavailable.");
		}

		return publicReadResponse(request, { page: toPublicPage(row) }, requestId);
	},
};
