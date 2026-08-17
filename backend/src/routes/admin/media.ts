import { jsonResponse } from "../../http.js";
import { requireAdmin } from "../../security/auth.js";
import { ApiError, ErrorCode } from "../../security/errors.js";
import {
	ALLOWED_MEDIA_GROUPS,
	buildMediaKey,
	inspectMedia,
	MEDIA_LIMITS,
	type MediaGroup,
	sha256Hex,
} from "../../security/media.js";
import { assertIdentifier } from "../../security/validate.js";
import type { Route } from "../router.js";

/**
 * Media upload and removal.
 *
 * Images arrive as a raw body rather than multipart: there is exactly one file
 * per request, and the group and owner come from the path, so there is no form
 * to parse and no client-supplied filename to sanitise. The stored key is
 * derived from the content hash, which makes every object immutable.
 */

const mediaUploadRoute: Route = {
	method: "POST",
	pattern: "/v1/admin/media/:group/:ownerId",
	handler: async (request, { env, requestId, params, cors }) => {
		const admin = await requireAdmin(request, env);

		const group = assertIdentifier(params.group, "media group", 16) as MediaGroup;
		if (!ALLOWED_MEDIA_GROUPS.includes(group)) {
			throw new ApiError(400, ErrorCode.BAD_REQUEST, "That media group is not recognised.");
		}

		const ownerId = assertIdentifier(params.ownerId, "record id");

		// Reject on the declared length before reading anything, then validate
		// the bytes actually received — a wrong or absent header must not decide.
		const declared = Number.parseInt(request.headers.get("content-length") ?? "", 10);
		if (Number.isFinite(declared) && declared > MEDIA_LIMITS.maxAnimatedBytes) {
			throw new ApiError(413, ErrorCode.PAYLOAD_TOO_LARGE, "That image is too large.");
		}

		const bytes = new Uint8Array(await request.arrayBuffer());
		const media = inspectMedia(bytes);

		const digest = await sha256Hex(bytes);
		const key = buildMediaKey(group, ownerId, digest, media.extension);

		await env.MEDIA.put(key, bytes as BufferSource, {
			httpMetadata: {
				contentType: media.contentType,
				// Content-addressed, so it can never go stale.
				cacheControl: "public, max-age=31536000, immutable",
			},
			customMetadata: {
				uploadedBy: admin.email,
				uploadedAt: new Date().toISOString(),
			},
		});

		return jsonResponse(
			{
				key,
				url: `${env.PUBLIC_MEDIA_BASE_URL.replace(/\/+$/, "")}/${key}`,
				contentType: media.contentType,
				width: media.width,
				height: media.height,
				bytes: bytes.length,
				// The extension in the key carries this to clients too, so a
				// renderer that cannot animate can decide before downloading.
				animated: media.animated,
			},
			{ status: 201, requestId, cors },
		);
	},
};

const mediaDeleteRoute: Route = {
	method: "DELETE",
	pattern: "/v1/admin/media/:group/:ownerId/:file",
	handler: async (request, { env, requestId, params, cors }) => {
		await requireAdmin(request, env);

		const group = assertIdentifier(params.group, "media group", 16) as MediaGroup;
		if (!ALLOWED_MEDIA_GROUPS.includes(group)) {
			throw new ApiError(400, ErrorCode.BAD_REQUEST, "That media group is not recognised.");
		}

		const ownerId = assertIdentifier(params.ownerId, "record id");

		// Rebuilt from validated parts rather than trusting the path, so a key
		// can never point outside its own group and record.
		const file = params.file ?? "";
		const match = /^([a-f0-9]{64})\.(png|jpg|jpeg|webp|gif|mp4)$/.exec(file);
		if (match === null) {
			throw new ApiError(400, ErrorCode.BAD_REQUEST, "That media key is not valid.");
		}

		const key = buildMediaKey(group, ownerId, match[1] as string, match[2] as string);
		if ((await env.MEDIA.head(key)) === null) {
			throw new ApiError(404, ErrorCode.NOT_FOUND, "That image does not exist.");
		}

		await env.MEDIA.delete(key);
		return jsonResponse({ key, deleted: true }, { requestId, cors });
	},
};

export const adminMediaRoutes: readonly Route[] = [mediaUploadRoute, mediaDeleteRoute];
