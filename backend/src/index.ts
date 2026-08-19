import { corsPreflightResponse, jsonResponse } from "./http.js";
import { logRequest, logUnhandledError, requestIdFor } from "./observability/log.js";
import { routes } from "./routes/index.js";
import { matchRoute } from "./routes/router.js";
import { ApiError, internalErrorBody, methodNotAllowed, notFound } from "./security/errors.js";
import { CACHE_CONTROL, corsHeadersFor } from "./security/headers.js";
import { isAdminHostname, isAdminSurface } from "./security/hosts.js";
import { enforceRateLimit, isRateLimited } from "./security/ratelimit.js";
import type { Env, RequestContext } from "./types.js";

/**
 * Entry point.
 *
 * Owns dispatch, the error envelope, CORS selection, and request logging so no
 * individual route can leak an internal failure or answer an admin request with
 * the public CORS policy. Anything thrown that is not an ApiError becomes a
 * generic 500 with the real cause written only to the log.
 */
export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const startedAt = Date.now();
		const requestId = requestIdFor(request);
		const url = new URL(request.url);

		// Resolved from the path, before dispatch, so even a 404 or a 405 on an
		// admin path answers with the strict policy rather than the wildcard.
		const cors = corsHeadersFor(url.pathname, env, request.headers.get("origin"));

		// HEAD is served by the GET handler with the body stripped.
		const isHead = request.method === "HEAD";
		const method = isHead ? "GET" : request.method;

		if (method === "OPTIONS") {
			return corsPreflightResponse(cors);
		}

		const match = matchRoute(routes, method, url.pathname);
		const route =
			match.kind === "matched"
				? match.route.pattern
				: match.kind === "method-not-allowed"
					? match.pattern
					: "unmatched";

		let response: Response;
		let errorCode: string | undefined;
		let logContext: Readonly<Record<string, unknown>> | undefined;

		try {
			// Before anything else, and before the rate limiter spends a token on
			// it: admin is served only on the hostname Access protects. A 404
			// rather than a 403, because a visitor to the public site has no
			// business learning that an admin panel is there to be found.
			if (isAdminSurface(url.pathname) && !isAdminHostname(env, url.hostname)) {
				throw notFound();
			}

			// Metered before dispatch so a refused request costs no database work.
			if (isRateLimited(url.pathname)) {
				await enforceRateLimit(request, env);
			}

			if (match.kind === "not-found") {
				throw notFound();
			}

			if (match.kind === "method-not-allowed") {
				const error = methodNotAllowed();
				errorCode = error.code;
				response = jsonResponse(error.toBody(), {
					status: error.status,
					requestId,
					cors,
					headers: { allow: match.allow.join(", ") },
				});
			} else {
				const context: RequestContext = { env, requestId, params: match.params, cors };
				response = await match.route.handler(request, context);
			}
		} catch (error) {
			if (error instanceof ApiError) {
				errorCode = error.code;
				logContext = error.logContext;
				response = jsonResponse(error.toBody(), {
					status: error.status,
					requestId,
					cors,
					// Tell a throttled caller when to come back rather than leaving
					// it to guess and retry in a tight loop.
					...(error.status === 429 ? { headers: { "retry-after": "60" } } : {}),
				});
			} else {
				errorCode = "INTERNAL_ERROR";
				logUnhandledError(requestId, route, error);
				response = jsonResponse(internalErrorBody(), {
					status: 500,
					cacheControl: CACHE_CONTROL.noStore,
					requestId,
					cors,
				});
			}
		}

		logRequest({
			requestId,
			method: request.method,
			route,
			status: response.status,
			durationMs: Date.now() - startedAt,
			...(errorCode === undefined ? {} : { errorCode }),
			...(logContext === undefined ? {} : { context: logContext }),
		});

		return isHead
			? new Response(null, { status: response.status, headers: response.headers })
			: response;
	},
} satisfies ExportedHandler<Env>;
