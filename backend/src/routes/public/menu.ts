import { getPublishedMenuItem, listPublishedMenuItems, toPublicMenuItem } from "../../data/menu.js";
import { publicReadResponse } from "../../http.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import { assertIdentifier } from "../../security/validate.js";
import type { Route } from "../router.js";
import { collectionBody } from "./shared.js";

/** The published drinks card. */

export const menuListRoute: Route = {
	method: "GET",
	pattern: "/v1/menu",
	handler: async (request, { env, requestId }) => {
		const rows = await listPublishedMenuItems(env.DB);
		const menu = rows.map((row) => toPublicMenuItem(row, env.PUBLIC_MEDIA_BASE_URL));

		return publicReadResponse(request, collectionBody("menu", menu, rows), requestId);
	},
};

export const menuDetailRoute: Route = {
	method: "GET",
	pattern: "/v1/menu/:id",
	handler: async (request, { env, requestId, params }) => {
		const id = assertIdentifier(params.id, "menu item id");
		const row = await getPublishedMenuItem(env.DB, id);

		if (row === undefined) {
			throw new ApiError(
				404,
				ErrorCode.MENU_ITEM_NOT_FOUND,
				"The requested menu item is unavailable.",
			);
		}

		return publicReadResponse(
			request,
			{ item: toPublicMenuItem(row, env.PUBLIC_MEDIA_BASE_URL) },
			requestId,
		);
	},
};
