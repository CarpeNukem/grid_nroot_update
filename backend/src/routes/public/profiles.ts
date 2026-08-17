import {
	getPublishedProfile,
	listPublishedProfiles,
	toPublicProfile,
} from "../../data/profiles.js";
import { publicReadResponse } from "../../http.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import { assertIdentifier, LIMITS } from "../../security/validate.js";
import type { Route } from "../router.js";
import { collectionBody } from "./shared.js";

/**
 * Published staff profiles.
 *
 * Both routes read through the `published = 1` queries, so an unpublished
 * record cannot be reached even by guessing its id.
 */

export const profileListRoute: Route = {
	method: "GET",
	pattern: "/v1/profiles",
	handler: async (request, { env, requestId }) => {
		const requested = new URL(request.url).searchParams.get("category");
		const category =
			requested === null
				? undefined
				: assertIdentifier(requested, "category", LIMITS.categoryMaxLength);

		const rows = await listPublishedProfiles(env.DB, category);
		const profiles = rows.map((row) => toPublicProfile(row, env.PUBLIC_MEDIA_BASE_URL));

		return publicReadResponse(request, collectionBody("profiles", profiles, rows), requestId);
	},
};

export const profileDetailRoute: Route = {
	method: "GET",
	pattern: "/v1/profiles/:id",
	handler: async (request, { env, requestId, params }) => {
		const id = assertIdentifier(params.id, "profile id");
		const row = await getPublishedProfile(env.DB, id);

		if (row === undefined) {
			throw new ApiError(404, ErrorCode.PROFILE_NOT_FOUND, "The requested profile is unavailable.");
		}

		return publicReadResponse(
			request,
			{ profile: toPublicProfile(row, env.PUBLIC_MEDIA_BASE_URL) },
			requestId,
		);
	},
};
