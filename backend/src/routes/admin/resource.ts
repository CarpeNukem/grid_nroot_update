import { assertNoUnknownFields, assertObject, optionalBool } from "../../data/validation.js";
import { jsonResponse, readJsonBody } from "../../http.js";
import { requireAdmin } from "../../security/auth.js";
import { ApiError, ErrorCode, type ErrorCode as ErrorCodeType } from "../../security/errors.js";
import { assertIdentifier } from "../../security/validate.js";
import type { Route } from "../router.js";

/**
 * The admin CRUD surface, built once and applied to each collection.
 *
 * The HTTP shape — authenticate, validate, act, answer — is identical for
 * profiles, menu, and news, so it lives here. What is *not* shared is the SQL:
 * each collection keeps its own hand-written, fully parameterised statements in
 * src/data, so no column name is ever assembled from a request.
 *
 * Two behaviours are deliberate:
 *  - `published` cannot be set through create or update. Publishing is its own
 *    route, so editing a record can never expose it as a side effect.
 *  - POST refuses to overwrite an existing id (409); PUT is the create-or-
 *    replace verb. A retried create therefore cannot silently clobber a record
 *    someone else added.
 */
export interface AdminResourceConfig<TInput, TRow> {
	/** Path segment, e.g. `profiles`. Fixed at build time, never from a request. */
	readonly segment: string;
	/** Human-readable singular, used in error messages. */
	readonly label: string;
	readonly notFoundCode: ErrorCodeType;
	readonly parse: (body: unknown, id?: string) => TInput;
	readonly list: (db: D1Database) => Promise<TRow[]>;
	readonly get: (db: D1Database, id: string) => Promise<TRow | undefined>;
	readonly upsert: (db: D1Database, input: TInput, now: string, editor: string) => Promise<void>;
	readonly remove: (db: D1Database, id: string) => Promise<boolean>;
	readonly setPublished: (
		db: D1Database,
		id: string,
		published: boolean,
		now: string,
		editor: string,
	) => Promise<boolean>;
}

export function adminRoutesFor<TInput, TRow>(
	config: AdminResourceConfig<TInput, TRow>,
): readonly Route[] {
	const base = `/v1/admin/${config.segment}`;
	const notFound = (): ApiError =>
		new ApiError(404, config.notFoundCode, `That ${config.label} does not exist.`);

	/** Admin reads return the storage row, including `published` and audit fields. */
	const listRoute: Route = {
		method: "GET",
		pattern: base,
		handler: async (request, { env, requestId, cors }) => {
			await requireAdmin(request, env);
			const items = await config.list(env.DB);

			return jsonResponse({ items }, { requestId, cors });
		},
	};

	const detailRoute: Route = {
		method: "GET",
		pattern: `${base}/:id`,
		handler: async (request, { env, requestId, params, cors }) => {
			await requireAdmin(request, env);
			const id = assertIdentifier(params.id, `${config.label} id`);
			const item = await config.get(env.DB, id);

			if (item === undefined) {
				throw notFound();
			}

			return jsonResponse({ item }, { requestId, cors });
		},
	};

	const createRoute: Route = {
		method: "POST",
		pattern: base,
		handler: async (request, { env, requestId, cors }) => {
			const admin = await requireAdmin(request, env);
			const input = config.parse(await readJsonBody(request));
			const id = (input as { id: string }).id;

			if ((await config.get(env.DB, id)) !== undefined) {
				throw new ApiError(
					409,
					ErrorCode.ALREADY_EXISTS,
					`A ${config.label} with that id already exists.`,
				);
			}

			const now = new Date().toISOString();
			await config.upsert(env.DB, input, now, admin.email);

			return jsonResponse(
				{ item: await config.get(env.DB, id) },
				{ status: 201, requestId, cors, headers: { location: `${base}/${id}` } },
			);
		},
	};

	const updateRoute: Route = {
		method: "PUT",
		pattern: `${base}/:id`,
		handler: async (request, { env, requestId, params, cors }) => {
			const admin = await requireAdmin(request, env);
			const id = assertIdentifier(params.id, `${config.label} id`);
			// The path is authoritative; an `id` in the body is ignored rather
			// than allowed to move the record.
			const input = config.parse(await readJsonBody(request), id);

			const now = new Date().toISOString();
			await config.upsert(env.DB, input, now, admin.email);

			return jsonResponse({ item: await config.get(env.DB, id) }, { requestId, cors });
		},
	};

	const deleteRoute: Route = {
		method: "DELETE",
		pattern: `${base}/:id`,
		handler: async (request, { env, requestId, params, cors }) => {
			await requireAdmin(request, env);
			const id = assertIdentifier(params.id, `${config.label} id`);

			if (!(await config.remove(env.DB, id))) {
				throw notFound();
			}

			return jsonResponse({ id, deleted: true }, { requestId, cors });
		},
	};

	const publishRoute: Route = {
		method: "POST",
		pattern: `${base}/:id/publish`,
		handler: async (request, { env, requestId, params, cors }) => {
			const admin = await requireAdmin(request, env);
			const id = assertIdentifier(params.id, `${config.label} id`);

			const body = assertObject(await readJsonBody(request));
			assertNoUnknownFields(body, ["published"]);
			const published = optionalBool(body, "published", true);

			const now = new Date().toISOString();
			if (!(await config.setPublished(env.DB, id, published, now, admin.email))) {
				throw notFound();
			}

			return jsonResponse({ item: await config.get(env.DB, id) }, { requestId, cors });
		},
	};

	return [listRoute, detailRoute, createRoute, updateRoute, deleteRoute, publishRoute];
}
