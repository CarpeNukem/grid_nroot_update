import adminHtml from "../../../admin/index.html";
import type { Route } from "../router.js";

/**
 * Serves the admin page.
 *
 * It is served from this Worker rather than a separate Pages site so the tool
 * and the API share an origin: no CORS, no configured API base, and one thing
 * for Cloudflare Access to sit in front of. Deployed, Access must cover both
 * `/admin` and `/v1/admin/*` — the API does not become safe just because the
 * page in front of it is protected.
 *
 * The JSON routes send a deny-all CSP, which would stop this page running its
 * own script. It gets a policy of its own instead: nothing external at all,
 * requests only to this origin, and inline script and style allowed because the
 * whole page is authored here and ships as one file.
 */
const ADMIN_CSP = [
	"default-src 'none'",
	"script-src 'unsafe-inline'",
	"style-src 'unsafe-inline'",
	"img-src 'self' data:",
	"media-src 'self'",
	"connect-src 'self'",
	"form-action 'none'",
	"base-uri 'none'",
	"frame-ancestors 'none'",
].join("; ");

export const adminUiRoute: Route = {
	method: "GET",
	pattern: "/admin",
	handler: () =>
		new Response(adminHtml, {
			status: 200,
			headers: new Headers({
				"content-type": "text/html; charset=utf-8",
				"content-security-policy": ADMIN_CSP,
				"x-content-type-options": "nosniff",
				"referrer-policy": "no-referrer",
				"x-frame-options": "DENY",
				// The page is a thin shell over the API; never let it go stale.
				"cache-control": "no-store",
			}),
		}),
};
