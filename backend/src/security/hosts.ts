import type { Env } from "../types.js";
import { ADMIN_PATH_PREFIX } from "./headers.js";

/**
 * Which hostname a request arrived on, and what that permits.
 *
 * Cloudflare Access is bound to a hostname, not to this Worker. It covers
 * `api.nroot.io/admin*` and `api.nroot.io/v1/admin/*`. The router matches on
 * path alone, so the moment a second hostname points at this Worker — the
 * public site, a preview alias, workers.dev — the same admin code answers
 * there with no Access in front of it. Nothing is bypassed; the policy simply
 * does not apply to a hostname it was never written for.
 *
 * So the Worker enforces the same rule itself, where it cannot be undone by a
 * dashboard edit: admin exists on the admin hostname and nowhere else.
 */

/** The admin page. The API beneath it lives under {@link ADMIN_PATH_PREFIX}. */
export const ADMIN_UI_PATH = "/admin";

/** Every path that must stay behind Access, page and API alike. */
export const isAdminSurface = (pathname: string): boolean =>
	pathname === ADMIN_UI_PATH || pathname.startsWith(ADMIN_PATH_PREFIX);

/**
 * Whether admin may be served on this hostname.
 *
 * Deployed, this is an allowlist of exactly one name, and an unset
 * `ADMIN_HOSTNAME` denies every host rather than allowing them: a missing
 * setting should lock the editor out of the panel, never open it to the web.
 * That matches how `requireAdmin` treats absent Access configuration.
 *
 * Development is exempt because there is no stable hostname to match — the
 * Worker answers on 127.0.0.1, localhost, and whatever the LAN address is.
 * The environment already accepts a header in place of an Access token, so
 * this grants nothing that ENVIRONMENT=development has not already granted.
 */
export function isAdminHostname(env: Env, hostname: string): boolean {
	if (env.ENVIRONMENT === "development") {
		return true;
	}

	const allowed = (env.ADMIN_HOSTNAME ?? "").trim().toLowerCase();

	return allowed.length > 0 && hostname.toLowerCase() === allowed;
}
