import type { Env } from "../types.js";

/**
 * Response hardening applied to every route.
 *
 * The API only ever returns JSON, so the CSP is a deny-all: if a response is
 * ever opened directly in a browser, nothing in it can load or execute.
 */
export const SECURITY_HEADERS: Readonly<Record<string, string>> = {
	"x-content-type-options": "nosniff",
	"referrer-policy": "no-referrer",
	"x-frame-options": "DENY",
	"content-security-policy": "default-src 'none'; frame-ancestors 'none'",
};

/** Cache-Control values. Public reads are cacheable; everything else is not. */
export const CACHE_CONTROL = {
	/** Public catalogue reads, revalidated by ETag. */
	publicRead: "public, max-age=60, stale-while-revalidate=300",
	/** Liveness and anything authenticated or mutating. */
	noStore: "no-store",
} as const;

/**
 * CORS for public read routes.
 *
 * These serve published, non-credentialed data to any client, so a wildcard
 * origin is correct here. Admin routes get an exact-origin policy instead and
 * must never reuse this.
 */
export const PUBLIC_CORS_HEADERS: Readonly<Record<string, string>> = {
	"access-control-allow-origin": "*",
	"access-control-allow-methods": "GET, HEAD, OPTIONS",
	"access-control-allow-headers": "if-none-match, content-type",
	"access-control-max-age": "86400",
};

/** Admin routes live under this prefix and are authenticated separately. */
export const ADMIN_PATH_PREFIX = "/v1/admin/";

export const isAdminPath = (pathname: string): boolean => pathname.startsWith(ADMIN_PATH_PREFIX);

/**
 * CORS for admin routes: the configured admin origin only, never a wildcard.
 *
 * An unrecognised or absent Origin gets no CORS headers at all, so a page on
 * any other origin cannot read an admin response. `Vary: Origin` keeps a cache
 * from serving one origin's decision to another.
 */
export function adminCorsHeaders(
	env: Env,
	origin: string | null,
): Readonly<Record<string, string>> {
	const allowed = env.ADMIN_ALLOWED_ORIGIN.trim();
	if (origin === null || allowed.length === 0 || origin !== allowed) {
		return { vary: "Origin" };
	}

	return {
		"access-control-allow-origin": allowed,
		"access-control-allow-credentials": "true",
		"access-control-allow-methods": "GET, POST, PUT, DELETE, OPTIONS",
		"access-control-allow-headers": "content-type, cf-access-jwt-assertion",
		"access-control-max-age": "600",
		vary: "Origin",
	};
}

/** Picks the CORS policy for a path. Admin never receives the public wildcard. */
export function corsHeadersFor(
	pathname: string,
	env: Env,
	origin: string | null,
): Readonly<Record<string, string>> {
	return isAdminPath(pathname) ? adminCorsHeaders(env, origin) : PUBLIC_CORS_HEADERS;
}
