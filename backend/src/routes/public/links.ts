import { destinationFor, getLink, isLinkCode, normaliseCode } from "../../data/links.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import { SECURITY_HEADERS } from "../../security/headers.js";
import type { Route } from "../router.js";

/**
 * Following a short link: `GET /s/:code` answers 302 to wherever it points.
 *
 * What a click costs on the free plan: one Worker request and one D1 row read
 * by primary key. A Discord file adds one call to Discord and one D1 write
 * about once a day, and only when someone actually follows the link that day.
 * Clicks are metered by the public read limit like every other public route.
 *
 * The browser may reuse the redirect for {@link REDIRECT_MAX_AGE_SECONDS},
 * which saves a request each time someone opens the same link again soon.
 * That is safe for a Discord file because a link is re-signed while its
 * signature still has an hour left, and it is the most a deleted link can
 * outlive its deletion in a browser that had already followed it. `private`,
 * because a shared cache gains nothing here: the Worker runs before
 * Cloudflare's cache on a custom domain.
 *
 * `no-referrer` (from the shared security headers) means the page a link
 * lands on is not told the visitor came through here.
 */
const REDIRECT_MAX_AGE_SECONDS = 300;

const linkNotFound = (): ApiError =>
	new ApiError(404, ErrorCode.LINK_NOT_FOUND, "That short link does not exist.");

export const shortLinkRoute: Route = {
	method: "GET",
	pattern: "/s/:code",
	handler: async (_request, { env, params, requestId }) => {
		const code = normaliseCode(params.code ?? "");
		if (!isLinkCode(code)) {
			throw linkNotFound();
		}

		const link = await getLink(env.DB, code);
		if (link === null) {
			throw linkNotFound();
		}

		const destination = await destinationFor(link, env, new Date());
		if (destination.refreshFailure !== undefined) {
			// Not an error the visitor sees — they still go to the last address
			// held — but the one sign a bot token has stopped working.
			console.warn(
				JSON.stringify({
					type: "short-link-refresh-failed",
					timestamp: new Date().toISOString(),
					requestId,
					...destination.refreshFailure,
				}),
			);
		}

		return new Response(null, {
			status: 302,
			headers: new Headers({
				...SECURITY_HEADERS,
				location: destination.url,
				"cache-control": `private, max-age=${REDIRECT_MAX_AGE_SECONDS}`,
				"x-request-id": requestId,
			}),
		});
	},
};
