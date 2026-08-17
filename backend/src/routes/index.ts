import { adminRoutes } from "./admin/index.js";
import { adminMediaRoutes } from "./admin/media.js";
import { adminUiRoute } from "./admin/ui.js";
import { healthRoute } from "./health.js";
import { catalogRoute } from "./public/catalog.js";
import { mediaObjectRoute } from "./public/media.js";
import { menuDetailRoute, menuListRoute } from "./public/menu.js";
import { newsDetailRoute, newsListRoute } from "./public/news.js";
import { profileDetailRoute, profileListRoute } from "./public/profiles.js";
import type { Route } from "./router.js";

/**
 * The route table.
 *
 * Public reads serve only published records. Admin routes live under
 * `/v1/admin/` — the prefix the entry point uses to pick the strict CORS policy
 * — and every one of them authenticates before touching the database.
 */
export const routes: readonly Route[] = [
	healthRoute,

	catalogRoute,
	profileListRoute,
	profileDetailRoute,
	menuListRoute,
	menuDetailRoute,
	newsListRoute,
	newsDetailRoute,
	mediaObjectRoute,

	adminUiRoute,
	...adminRoutes,
	...adminMediaRoutes,
];
