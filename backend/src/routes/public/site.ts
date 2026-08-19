import siteHtml from "../../../site/index.html";
import { CACHE_CONTROL } from "../../security/headers.js";
import type { Route } from "../router.js";

/**
 * The public venue site.
 *
 * A read-only shell over `/v1/catalog`, served by this Worker rather than a
 * separate Pages project so the page and the data it reads share an origin:
 * no CORS, no configured API base, and the media the page shows comes from the
 * same host it was loaded from. It answers on every hostname bound to this
 * Worker, which means it can be checked on the API hostname before any new DNS
 * record exists.
 *
 * Nothing here is authenticated and nothing here writes. The admin surfaces are
 * held to the opposite rule — see `isAdminHostname`.
 */

/**
 * Same shape as the admin policy: nothing external, inline script and style
 * because the whole page ships as one file. `img-src 'self'` is enough because
 * the page rewrites catalogue media URLs onto its own origin before setting
 * them, so no second host has to be named here.
 */
const SITE_CSP = [
	"default-src 'none'",
	"script-src 'unsafe-inline'",
	"style-src 'unsafe-inline'",
	"img-src 'self' data:",
	"connect-src 'self'",
	"form-action 'none'",
	"base-uri 'none'",
	"frame-ancestors 'none'",
].join("; ");

export const siteRoute: Route = {
	method: "GET",
	pattern: "/",
	handler: () =>
		new Response(siteHtml, {
			status: 200,
			headers: new Headers({
				"content-type": "text/html; charset=utf-8",
				"content-security-policy": SITE_CSP,
				"x-content-type-options": "nosniff",
				"referrer-policy": "no-referrer",
				"x-frame-options": "DENY",
				"x-robots-tag": "noindex, nofollow",
				// The shell is static; the content it renders has its own ETag.
				"cache-control": CACHE_CONTROL.publicRead,
			}),
		}),
};

/**
 * Crawling is permitted on purpose.
 *
 * The site must not be indexed, and the way to achieve that is `noindex` — not
 * a `Disallow`. They are not interchangeable: a crawler refused the page never
 * reads the `noindex` on it, and can still list the bare URL from a link found
 * somewhere else. Letting it fetch and be told not to index is what actually
 * keeps the page out of results.
 *
 * The admin paths are not named here either. They are already 404 on this
 * hostname, and listing them would only advertise where to look.
 */
const ROBOTS_TXT = [
	"# Fetching is allowed so that the noindex is seen.",
	"# Every response from this host carries: X-Robots-Tag: noindex, nofollow",
	"User-agent: *",
	"Disallow:",
	"",
].join("\n");

export const robotsRoute: Route = {
	method: "GET",
	pattern: "/robots.txt",
	handler: () =>
		new Response(ROBOTS_TXT, {
			status: 200,
			headers: new Headers({
				"content-type": "text/plain; charset=utf-8",
				"x-content-type-options": "nosniff",
				"cache-control": CACHE_CONTROL.publicRead,
			}),
		}),
};
