import {
	deleteMenuItem,
	getMenuItem,
	listAllMenuItems,
	setMenuItemPublished,
	upsertMenuItem,
} from "../../data/menu.js";
import {
	deleteNewsPost,
	getNewsPost,
	listAllNewsPosts,
	setNewsPostPublished,
	upsertNewsPost,
} from "../../data/news.js";
import {
	deletePage,
	getPage,
	listAllPages,
	setPagePublished,
	upsertPage,
} from "../../data/pages.js";
import {
	deleteProfile,
	getProfile,
	listAllProfiles,
	setProfilePublished,
	upsertProfile,
} from "../../data/profiles.js";
import {
	parseMenuItemInput,
	parseNewsPostInput,
	parsePageInput,
	parseProfileInput,
} from "../../data/validation.js";
import { ErrorCode } from "../../security/errors.js";
import type { Route } from "../router.js";
import { adminRoutesFor } from "./resource.js";

/**
 * Admin routes for the three editable collections.
 *
 * Every route here is behind `requireAdmin`. Adding a collection means adding a
 * config, not writing another copy of the CRUD plumbing.
 */
export const adminRoutes: readonly Route[] = [
	...adminRoutesFor({
		segment: "profiles",
		label: "profile",
		notFoundCode: ErrorCode.PROFILE_NOT_FOUND,
		parse: parseProfileInput,
		list: listAllProfiles,
		get: getProfile,
		upsert: upsertProfile,
		remove: deleteProfile,
		setPublished: setProfilePublished,
	}),
	...adminRoutesFor({
		segment: "menu",
		label: "menu item",
		notFoundCode: ErrorCode.MENU_ITEM_NOT_FOUND,
		parse: parseMenuItemInput,
		list: listAllMenuItems,
		get: getMenuItem,
		upsert: upsertMenuItem,
		remove: deleteMenuItem,
		setPublished: setMenuItemPublished,
	}),
	...adminRoutesFor({
		segment: "pages",
		label: "page",
		notFoundCode: ErrorCode.PAGE_NOT_FOUND,
		parse: parsePageInput,
		list: listAllPages,
		get: getPage,
		upsert: upsertPage,
		remove: deletePage,
		setPublished: setPagePublished,
	}),
	...adminRoutesFor({
		segment: "news",
		label: "news post",
		notFoundCode: ErrorCode.NEWS_POST_NOT_FOUND,
		parse: parseNewsPostInput,
		list: listAllNewsPosts,
		get: getNewsPost,
		upsert: upsertNewsPost,
		remove: deleteNewsPost,
		setPublished: setNewsPostPublished,
	}),
];
