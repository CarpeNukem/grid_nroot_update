/**
 * Structured request logging.
 *
 * One JSON line per request. Deliberately excluded: request and response
 * bodies, query strings, authorization headers, and anything derived from
 * profile request messages — those can carry private text.
 */

export interface RequestLogEntry {
	readonly requestId: string;
	readonly method: string;
	/** Matched route pattern, not the raw path, so ids stay out of the logs. */
	readonly route: string;
	readonly status: number;
	readonly durationMs: number;
	readonly errorCode?: string;
	readonly context?: Readonly<Record<string, unknown>>;
}

export function logRequest(entry: RequestLogEntry): void {
	console.log(
		JSON.stringify({
			type: "request",
			timestamp: new Date().toISOString(),
			...entry,
		}),
	);
}

/** Logs an unexpected failure alongside its request. Never returned to clients. */
export function logUnhandledError(requestId: string, route: string, error: unknown): void {
	const detail =
		error instanceof Error
			? { name: error.name, message: error.message, stack: error.stack }
			: { name: "NonError", message: String(error) };

	console.error(
		JSON.stringify({
			type: "error",
			timestamp: new Date().toISOString(),
			requestId,
			route,
			...detail,
		}),
	);
}

/**
 * Correlation id for one request.
 *
 * Prefers Cloudflare's `cf-ray` so Worker logs line up with the dashboard, and
 * falls back to a UUID locally and in tests.
 */
export function requestIdFor(request: Request): string {
	return request.headers.get("cf-ray") ?? crypto.randomUUID();
}
