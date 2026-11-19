import { isDiscordAttachment } from "../../data/discord.js";
import {
	createLink,
	deleteLink,
	isLinkCode,
	listLinks,
	normaliseCode,
	parseTarget,
	type ShortLinkRow,
	shortUrlFor,
} from "../../data/links.js";
import { assertNoUnknownFields, assertObject } from "../../data/validation.js";
import { jsonResponse, readJsonBody } from "../../http.js";
import { requireAdmin } from "../../security/auth.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import type { Env } from "../../types.js";
import type { Route } from "../router.js";

/**
 * Making and removing short links.
 *
 * Admin only, so the domain cannot be used by strangers to disguise addresses
 * of their own. There is no edit: a link that should go somewhere else is a
 * new link, so one already posted in a channel can never quietly start
 * pointing at something different.
 */

/** The storage row, plus what the panel shows next to it. */
const toAdminLink = (env: Env, row: ShortLinkRow): Record<string, unknown> => ({
	...row,
	short_url: shortUrlFor(env, row.code),
	discord: isDiscordAttachment(new URL(row.target)),
});

/** Whether Discord links will be re-signed. The panel warns when they will not. */
const discordRefresh = (env: Env): boolean => (env.DISCORD_BOT_TOKEN?.trim() ?? "").length > 0;

const invalidCode = (): ApiError =>
	new ApiError(
		400,
		ErrorCode.INVALID_IDENTIFIER,
		"A code is 1 to 40 lowercase letters, digits or dashes, and cannot start or end with a dash.",
	);

const adminLinksListRoute: Route = {
	method: "GET",
	pattern: "/v1/admin/links",
	handler: async (request, { env, requestId, cors }) => {
		await requireAdmin(request, env);

		const rows = await listLinks(env.DB);

		return jsonResponse(
			{ items: rows.map((row) => toAdminLink(env, row)), discordRefresh: discordRefresh(env) },
			{ requestId, cors },
		);
	},
};

/**
 * `{ "url": "https://…", "code": "optional-name" }`.
 *
 * 201 with a new link, or 200 with the existing one when the address already
 * has a link and no code was asked for.
 */
const adminLinkCreateRoute: Route = {
	method: "POST",
	pattern: "/v1/admin/links",
	handler: async (request, { env, requestId, cors }) => {
		const admin = await requireAdmin(request, env);

		const body = assertObject(await readJsonBody(request));
		assertNoUnknownFields(body, ["url", "code"]);

		const target = parseTarget(body.url, env);

		let code: string | undefined;
		if (body.code !== undefined && body.code !== null && body.code !== "") {
			if (typeof body.code !== "string") {
				throw invalidCode();
			}
			code = normaliseCode(body.code);
			if (!isLinkCode(code)) {
				throw invalidCode();
			}
		}

		const { row, existing } = await createLink(env.DB, target, code, admin.email, new Date());

		return jsonResponse(
			{ ...toAdminLink(env, row), existing, discordRefresh: discordRefresh(env) },
			{ status: existing ? 200 : 201, requestId, cors },
		);
	},
};

const adminLinkDeleteRoute: Route = {
	method: "DELETE",
	pattern: "/v1/admin/links/:code",
	handler: async (request, { env, requestId, params, cors }) => {
		await requireAdmin(request, env);

		const code = normaliseCode(params.code ?? "");
		if (!isLinkCode(code)) {
			throw invalidCode();
		}

		if (!(await deleteLink(env.DB, code))) {
			throw new ApiError(404, ErrorCode.LINK_NOT_FOUND, "That short link does not exist.");
		}

		return jsonResponse({ code, deleted: true }, { requestId, cors });
	},
};

export const adminLinkRoutes: readonly Route[] = [
	adminLinksListRoute,
	adminLinkCreateRoute,
	adminLinkDeleteRoute,
];
