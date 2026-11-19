import { HALL_NAME } from "../../pool/hall.js";
import { isPlayerName, isSeatToken } from "../../pool/protocol.js";
import { ApiError, badRequest, ErrorCode } from "../../security/errors.js";
import { enforcePoolConnectLimit } from "../../security/ratelimit.js";
import type { Route } from "../router.js";

/**
 * The way into the pool hall: `GET /v1/pool?name=Forename%20Surname%40World[&v=3][&resume=<seat token>]`
 * (or a deck's old initials, `name=ABC`)
 * with a WebSocket upgrade. `v` is the protocol the deck speaks; the hall
 * reads it, and a deck that sends none is one from before free pyramid.
 *
 * This is the only part of a pool game that is a Worker request. Everything
 * after the upgrade is a message on the socket, which goes to the hall's
 * Durable Object and never through here again. So a connection is metered
 * like any request, and once more on a limit of its own: the messages it can
 * then send are what cost the hall, and the hall closes a socket that floods.
 *
 * The name and seat token are checked here as well as in the hall, so a
 * malformed attempt is turned away before it wakes the object.
 */
export const poolConnectRoute: Route = {
	method: "GET",
	pattern: "/v1/pool",
	handler: async (request, { env }) => {
		if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
			throw new ApiError(
				426,
				ErrorCode.UPGRADE_REQUIRED,
				"The pool hall is reached over a WebSocket.",
			);
		}

		const url = new URL(request.url);
		if (!isPlayerName(url.searchParams.get("name"))) {
			throw badRequest(
				"A character name and home world (Forename Surname@World), or initials, are required.",
			);
		}

		const resume = url.searchParams.get("resume");
		if (resume !== null && !isSeatToken(resume)) {
			throw badRequest("That seat token is not one the hall issued.");
		}

		await enforcePoolConnectLimit(request, env);

		return env.POOL_HALL.getByName(HALL_NAME).fetch(request);
	},
};
