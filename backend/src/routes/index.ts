import { adminRoutes } from "./admin/index.js";
import { adminMediaRoutes } from "./admin/media.js";
import { adminUiRoute } from "./admin/ui.js";
import { healthRoute } from "./health.js";
import { siteAssetRoute } from "./public/assets.js";
import { catalogRoute } from "./public/catalog.js";
import { mediaObjectRoute } from "./public/media.js";
import { menuDetailRoute, menuListRoute } from "./public/menu.js";
import { newsDetailRoute, newsListRoute } from "./public/news.js";
import { pageDetailRoute, pageListRoute } from "./public/pages.js";
import { profileDetailRoute, profileListRoute } from "./public/profiles.js";
import { robotsRoute, siteRoute } from "./public/site.js";
import type { Route } from "./router.js";

/**
 * The route table.
 *
 * Public reads serve only published records. Admin routes live under
 * `/v1/admin/` — the prefix the entry point uses to pick the strict CORS policy
 * — and every one of them authenticates before touching the database.
 *
 * Routing is by path alone, so a path is reachable on every hostname bound to
 * this Worker. That is fine for the site and the public reads and is not fine
 * for admin, which the entry point restricts to the hostname Access covers.
 */
export const routes: readonly Route[] = [
	healthRoute,

	siteRoute,
	robotsRoute,
	siteAssetRoute,

	catalogRoute,
	profileListRoute,
	profileDetailRoute,
	menuListRoute,
	menuDetailRoute,
	newsListRoute,
	newsDetailRoute,
	pageListRoute,
	pageDetailRoute,
	mediaObjectRoute,

	adminUiRoute,
	...adminRoutes,
	...adminMediaRoutes,
];
