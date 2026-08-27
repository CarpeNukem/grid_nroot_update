import { ApiError, ErrorCode } from "./security/errors.js";
import { CACHE_CONTROL, PUBLIC_CORS_HEADERS, SECURITY_HEADERS } from "./security/headers.js";
import { LIMITS } from "./security/validate.js";

export interface JsonResponseInit {
	readonly status?: number;
	/** Defaults to `no-store`; public reads pass `CACHE_CONTROL.publicRead`. */
	readonly cacheControl?: string;
	readonly requestId?: string;
	/**
	 * CORS policy for this response. Defaults to the public wildcard; admin
	 * responses pass the exact-origin policy from `adminCorsHeaders`.
	 */
	readonly cors?: Readonly<Record<string, string>>;
	readonly headers?: Readonly<Record<string, string>>;
}

/**
 * Builds a UTF-8 JSON response with security headers and CORS applied.
 *
 * The charset is explicit because profile text contains combining marks and
 * non-Latin glyphs that must survive round-tripping into the plugin.
 */
export function jsonResponse(body: unknown, init: JsonResponseInit = {}): Response {
	return rawJsonResponse(JSON.stringify(body), init);
}

function rawJsonResponse(serialized: string, init: JsonResponseInit = {}): Response {
	const headers = new Headers({
		"content-type": "application/json; charset=utf-8",
		"cache-control": init.cacheControl ?? CACHE_CONTROL.noStore,
		...SECURITY_HEADERS,
		...(init.cors ?? PUBLIC_CORS_HEADERS),
		...init.headers,
	});

	if (init.requestId !== undefined) {
		headers.set("x-request-id", init.requestId);
	}

	return new Response(serialized, { status: init.status ?? 200, headers });
}

/** Preflight response. Public paths get the wildcard policy, admin the strict one. */
export function corsPreflightResponse(cors: Readonly<Record<string, string>>): Response {
	return new Response(null, {
		status: 204,
		headers: new Headers({
			...SECURITY_HEADERS,
			...cors,
			"cache-control": CACHE_CONTROL.noStore,
		}),
	});
}

/**
 * Strong ETag over the exact bytes returned.
 *
 * Hashing the serialised body rather than a stored revision column means the
 * tag cannot drift from the content: any change to any field, including how it
 * is rendered, produces a different tag. 16 hex characters is ample for cache
 * validation — this is a change detector, not a security boundary.
 */
export async function etagFor(serialized: string): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(serialized));
	const hex = [...new Uint8Array(digest)]
		.slice(0, 8)
		.map((byte) => byte.toString(16).padStart(2, "0"))
		.join("");

	return `"${hex}"`;
}

/**
 * Whether `If-None-Match` covers the current tag.
 *
 * Accepts the `*` wildcard and tolerates the `W/` weak prefix, since an
 * intermediary may weaken a tag it forwards.
 */
function matchesIfNoneMatch(header: string | null, etag: string): boolean {
	if (header === null) {
		return false;
	}

	const normalise = (value: string): string => value.trim().replace(/^W\//, "");
	const candidates = header.split(",").map(normalise);

	return candidates.includes("*") || candidates.includes(normalise(etag));
}

/**
 * A cacheable public read.
 *
 * Returns 304 with no body when the client's ETag still matches, which is what
 * keeps the plugin's periodic refresh cheap and lets it skip rewriting its
 * on-disk cache.
 */
export async function publicReadResponse(
	request: Request,
	body: unknown,
	requestId: string,
): Promise<Response> {
	const serialized = JSON.stringify(body);

	return conditionalReadResponse(request, serialized, await etagFor(serialized), requestId);
}

/**
 * The same conditional answer, for a body that is already serialised and tagged.
 *
 * Split out because the catalogue serves from an edge cache: the bytes and their
 * tag come back from the cache rather than from the database, and only the 304
 * decision is per-request. Every response still carries a fresh `x-request-id`,
 * which is why the cached copy is never returned directly.
 */
export function conditionalReadResponse(
	request: Request,
	serialized: string,
	etag: string,
	requestId: string,
): Response {
	if (matchesIfNoneMatch(request.headers.get("if-none-match"), etag)) {
		return new Response(null, {
			status: 304,
			headers: new Headers({
				etag,
				"cache-control": CACHE_CONTROL.publicRead,
				...SECURITY_HEADERS,
				...PUBLIC_CORS_HEADERS,
				"x-request-id": requestId,
			}),
		});
	}

	return rawJsonResponse(serialized, {
		cacheControl: CACHE_CONTROL.publicRead,
		requestId,
		headers: { etag },
	});
}

/**
 * Reads and parses a JSON request body under a hard size cap.
 *
 * The declared Content-Length is checked first as a cheap rejection, then the
 * actual bytes are counted — a lying or absent header must not let an oversized
 * body through.
 */
export async function readJsonBody(request: Request): Promise<unknown> {
	const declaredLength = Number.parseInt(request.headers.get("content-length") ?? "", 10);
	if (Number.isFinite(declaredLength) && declaredLength > LIMITS.requestBodyMaxBytes) {
		throw new ApiError(413, ErrorCode.PAYLOAD_TOO_LARGE, "That request body is too large.");
	}

	const raw = await request.text();
	if (new TextEncoder().encode(raw).length > LIMITS.requestBodyMaxBytes) {
		throw new ApiError(413, ErrorCode.PAYLOAD_TOO_LARGE, "That request body is too large.");
	}

	try {
		return JSON.parse(raw);
	} catch {
		throw new ApiError(400, ErrorCode.BAD_REQUEST, "The request body is not valid JSON.");
	}
}
