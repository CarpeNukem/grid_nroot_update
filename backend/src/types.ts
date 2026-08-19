/**
 * Bindings and configuration available to the Worker.
 *
 * Values declared in wrangler.jsonc are always present. The Access settings
 * are optional here because they arrive as secrets (`.dev.vars` locally,
 * `wrangler secret put` when deployed) and are absent until admin routes are
 * configured — `requireAdmin` refuses to authenticate anyone when they are.
 */
export interface Env {
	readonly DB: D1Database;
	readonly MEDIA: R2Bucket;

	readonly ENVIRONMENT: string;
	readonly SCHEMA_VERSION: string;
	readonly PUBLIC_MEDIA_BASE_URL: string;
	readonly ADMIN_ALLOWED_ORIGIN: string;

	/**
	 * The only hostname on which the admin page and admin API are served.
	 *
	 * Optional in the type because development does not set it, but deployed it
	 * must match the hostname Cloudflare Access protects. Unset in production
	 * denies admin everywhere — see `isAdminHostname`.
	 */
	readonly ADMIN_HOSTNAME?: string;

	readonly CF_ACCESS_AUD?: string;
	readonly CF_ACCESS_TEAM_DOMAIN?: string;
	readonly ADMIN_ALLOWED_EMAILS?: string;

	/**
	 * Public-read throttle. Optional because local development and the test
	 * pool do not always provide one; production always does.
	 */
	readonly PUBLIC_READ_LIMITER?: RateLimit;
}

/** Per-request state threaded through route handlers. */
export interface RequestContext {
	readonly env: Env;
	readonly requestId: string;
	/** Matched `:param` values from the route pattern. */
	readonly params: Readonly<Record<string, string>>;
	/**
	 * CORS headers appropriate to this path — wildcard for public reads, exact
	 * origin for admin. Resolved once in the entry point so a handler cannot
	 * accidentally answer an admin request with the public policy.
	 */
	readonly cors: Readonly<Record<string, string>>;
}
