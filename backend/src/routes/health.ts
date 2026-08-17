import { jsonResponse } from "../http.js";
import { CACHE_CONTROL } from "../security/headers.js";
import type { Route } from "./router.js";

/**
 * Liveness check.
 *
 * Confirms the Worker is running and reports the schema version the plugin
 * should expect. It deliberately does not touch D1 or R2: the plugin treats a
 * failed health check as "stay on cached data", and a probe that fails because
 * a binding is briefly slow would be a worse signal than no probe at all.
 * Binding names and ids are never included.
 */
export const healthRoute: Route = {
	method: "GET",
	pattern: "/v1/health",
	handler: (_request, { env, requestId }) =>
		jsonResponse(
			{
				status: "ok",
				environment: env.ENVIRONMENT,
				schemaVersion: Number.parseInt(env.SCHEMA_VERSION, 10),
				timestamp: new Date().toISOString(),
			},
			{ cacheControl: CACHE_CONTROL.noStore, requestId },
		),
};
