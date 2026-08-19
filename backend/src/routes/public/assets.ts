import address from "../../../site/img/address.webp";
import broadcast from "../../../site/img/broadcast.webp";
import logo from "../../../site/img/grid.webp";
import menu from "../../../site/img/menu.webp";
import rooftop from "../../../site/img/rooftop.webp";
import services from "../../../site/img/services.webp";
import settings from "../../../site/img/settings.webp";
import wifi from "../../../site/img/wifi.webp";
import { notFound } from "../../security/errors.js";
import type { Route } from "../router.js";

/**
 * The site's own artwork: the deck's tile art and header, bundled with the
 * Worker rather than stored in R2.
 *
 * R2 holds content the venue edits — profile portraits, drink photographs,
 * flyers. This is chrome. It changes when the plugin's art changes, which is a
 * code change, so it belongs with the code: no upload step to forget, no way
 * for the site to lose its furniture because a bucket was emptied.
 *
 * They are also small enough to make that reasonable. `scripts/build-site-art.mjs`
 * produces WebP derivatives of the plugin's PNGs — the rooftop header goes from
 * 2.4 MB to about 70 KB — and the whole set is roughly 110 KB.
 */

/**
 * An explicit map rather than a lookup by path.
 *
 * The route parameter never touches a filesystem or a key: an unknown name
 * falls off the end of this object and 404s, so there is no traversal to get
 * wrong and no way to reach anything that is not listed here.
 */
const ASSETS: Readonly<Record<string, ArrayBuffer>> = {
	"rooftop.webp": rooftop,
	"grid.webp": logo,
	"menu.webp": menu,
	"wifi.webp": wifi,
	"address.webp": address,
	"broadcast.webp": broadcast,
	"services.webp": services,
	"settings.webp": settings,
};

export const siteAssetRoute: Route = {
	method: "GET",
	pattern: "/assets/:name",
	handler: (_request, { params }) => {
		const asset = Object.hasOwn(ASSETS, params.name ?? "")
			? ASSETS[params.name as string]
			: undefined;

		if (asset === undefined) {
			throw notFound();
		}

		return new Response(asset, {
			status: 200,
			headers: new Headers({
				"content-type": "image/webp",
				"x-content-type-options": "nosniff",
				"x-robots-tag": "noindex, nofollow",
				// A day, not a year: these ship inside the Worker under fixed
				// names, so a deploy that changes the art has no new URL to point
				// at and would otherwise be invisible to anyone already holding it.
				"cache-control": "public, max-age=86400",
			}),
		});
	},
};
