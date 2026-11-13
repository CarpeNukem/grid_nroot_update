import { deleteScore, isSubmissionId, listAllScores } from "../../data/arcade.js";
import { jsonResponse } from "../../http.js";
import { requireAdmin } from "../../security/auth.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import type { Route } from "../router.js";

/**
 * Moderation for the arcade boards.
 *
 * Scores come from clients the venue does not control, so a forged score or a
 * rude name will turn up eventually. An administrator lists the boards and
 * removes the entry; the next score down moves up, since every board keeps
 * more than the deck shows. Removal reaches readers within the edge window.
 */

const adminScoresListRoute: Route = {
	method: "GET",
	pattern: "/v1/admin/arcade/scores",
	handler: async (request, { env, requestId, cors }) => {
		await requireAdmin(request, env);

		return jsonResponse({ items: await listAllScores(env.DB) }, { requestId, cors });
	},
};

const adminScoreDeleteRoute: Route = {
	method: "DELETE",
	pattern: "/v1/admin/arcade/scores/:id",
	handler: async (request, { env, requestId, params, cors }) => {
		await requireAdmin(request, env);

		const id = params.id;
		if (!isSubmissionId(id)) {
			throw new ApiError(400, ErrorCode.INVALID_IDENTIFIER, "That score id is not valid.");
		}

		if (!(await deleteScore(env.DB, id))) {
			throw new ApiError(404, ErrorCode.SCORE_NOT_FOUND, "That score is not on any board.");
		}

		return jsonResponse({ id, deleted: true }, { requestId, cors });
	},
};

export const adminArcadeRoutes: readonly Route[] = [adminScoresListRoute, adminScoreDeleteRoute];
