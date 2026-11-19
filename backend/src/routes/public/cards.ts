import { HALL_NAME } from "../../cards/hall.js";
import { isPlayerName, isSeatToken } from "../../cards/protocol.js";
import { ApiError, badRequest, ErrorCode } from "../../security/errors.js";
import { enforceCardConnectLimit } from "../../security/ratelimit.js";
import type { Route } from "../router.js";

/**
 * The way into the card hall: `GET /v1/cards?name=Forename%20Surname%40World[&resume=<seat token>]`
 * with a WebSocket upgrade.
 *
 * The same shape as the way into the pool hall (pool.ts): this is the only
 * Worker request a visit to the card tables costs, it is metered on a limit of
 * its own, and a malformed attempt is turned away before it wakes the object.
 */
export const cardsConnectRoute: Route = {
	method: "GET",
	pattern: "/v1/cards",
	handler: async (request, { env }) => {
		if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
			throw new ApiError(
				426,
				ErrorCode.UPGRADE_REQUIRED,
				"The card hall is reached over a WebSocket.",
			);
		}

		const url = new URL(request.url);
		if (!isPlayerName(url.searchParams.get("name"))) {
			throw badRequest("A character name and home world, as Forename Surname@World, are required.");
		}

		const resume = url.searchParams.get("resume");
		if (resume !== null && !isSeatToken(resume)) {
			throw badRequest("That seat token is not one the hall issued.");
		}

		await enforceCardConnectLimit(request, env);

		return env.CARD_HALL.getByName(HALL_NAME).fetch(request);
	},
};
