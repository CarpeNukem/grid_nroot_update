import {
	isInitials,
	isOpenBoard,
	isScore,
	isSubmissionId,
	MAX_SUBMISSIONS,
	submitScores,
	type ValidScore,
} from "../../data/arcade.js";
import { SCHEMA_VERSION } from "../../data/schema.js";
import { assertObject } from "../../data/validation.js";
import { jsonResponse, readJsonBody } from "../../http.js";
import { badRequest } from "../../security/errors.js";
import { enforceSubmitLimit } from "../../security/ratelimit.js";
import type { Route } from "../router.js";

/**
 * Posting a score to the arcade's Grid-wide boards.
 *
 * The only arcade route a deck calls, and the only anonymous write this
 * Worker has. There is no matching read: the boards are part of `/v1/catalog`,
 * so showing them costs no request, and a deck posts here only when a score
 * would make a board.
 */

/** What became of one submitted score. Every outcome is final; a deck never resends an answered score. */
type SubmissionOutcome =
	/** On its board now, whether this request put it there or an earlier one did. */
	| "placed"
	/** A valid score that did not make the board. */
	| "not-placed"
	/** Not a score this relay takes: an unknown or expired board, a bad name or number. */
	| "rejected";

/**
 * `{ "scores": [{ "id", "board", "initials", "score" }] }`, up to
 * {@link MAX_SUBMISSIONS} at once, so a deck that was offline sends its whole
 * backlog in one request.
 *
 * The envelope and every id must be well formed or the request is refused.
 * Past that each score is judged on its own, so a board this relay does not
 * know yet — a deck newer than the Worker — fails that one score and not the
 * batch. Unknown fields are ignored for the same reason.
 */
export const arcadeSubmitRoute: Route = {
	method: "POST",
	pattern: "/v1/arcade/scores",
	handler: async (request, { env, requestId }) => {
		await enforceSubmitLimit(request, env);

		const submitted = assertObject(await readJsonBody(request)).scores;
		if (!Array.isArray(submitted) || submitted.length === 0 || submitted.length > MAX_SUBMISSIONS) {
			throw badRequest(`Send between 1 and ${MAX_SUBMISSIONS} scores.`);
		}

		const now = new Date();
		const ids: string[] = [];
		const accepted: ValidScore[] = [];

		for (const item of submitted) {
			const { id, board, initials, score } = assertObject(item);
			if (!isSubmissionId(id)) {
				throw badRequest("Every score needs a submission id.");
			}

			// The same id twice in one request is one score sent twice.
			if (ids.includes(id)) {
				continue;
			}

			ids.push(id);
			if (
				typeof board === "string" &&
				isOpenBoard(board, now) &&
				isInitials(initials) &&
				isScore(score)
			) {
				accepted.push({ id, board, initials, score });
			}
		}

		// A request with nothing acceptable in it never reaches the database.
		const placed =
			accepted.length > 0 ? await submitScores(env.DB, accepted, now) : new Set<string>();
		const valid = new Set(accepted.map((entry) => entry.id));

		const results = ids.map((id) => {
			const outcome: SubmissionOutcome = !valid.has(id)
				? "rejected"
				: placed.has(id)
					? "placed"
					: "not-placed";

			return { id, outcome };
		});

		return jsonResponse({ schemaVersion: SCHEMA_VERSION, results }, { requestId });
	},
};
