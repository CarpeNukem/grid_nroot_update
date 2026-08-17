import { ApiError, ErrorCode } from "../../security/errors.js";
import { PUBLIC_CORS_HEADERS, SECURITY_HEADERS } from "../../security/headers.js";
import { ALLOWED_MEDIA_GROUPS, buildMediaKey, type MediaGroup } from "../../security/media.js";
import { assertIdentifier } from "../../security/validate.js";
import type { Route } from "../router.js";

/**
 * Serves stored images.
 *
 * The Worker streams from R2 rather than the bucket being public, which keeps
 * one origin, one cache policy, and one place to change if the objects ever
 * move. It also means media works identically against `wrangler dev`, with no
 * public bucket or custom domain needed to test the whole path.
 *
 * Objects are content-addressed, so they are immutable and cached for a year.
 */
export const mediaObjectRoute: Route = {
	method: "GET",
	pattern: "/media/:group/:ownerId/:file",
	handler: async (request, { env, params }) => {
		const group = assertIdentifier(params.group, "media group", 16) as MediaGroup;
		if (!ALLOWED_MEDIA_GROUPS.includes(group)) {
			throw new ApiError(404, ErrorCode.NOT_FOUND, "That image is unavailable.");
		}

		const ownerId = assertIdentifier(params.ownerId, "record id");

		// The key is rebuilt from validated pieces; the raw path never reaches R2.
		const match = /^([a-f0-9]{64})\.(png|jpg|jpeg|webp|gif|mp4)$/.exec(params.file ?? "");
		if (match === null) {
			throw new ApiError(404, ErrorCode.NOT_FOUND, "That image is unavailable.");
		}

		const key = buildMediaKey(group, ownerId, match[1] as string, match[2] as string);
		const object = await env.MEDIA.get(key);
		if (object === null) {
			throw new ApiError(404, ErrorCode.NOT_FOUND, "That image is unavailable.");
		}

		const headers = new Headers({
			...SECURITY_HEADERS,
			...PUBLIC_CORS_HEADERS,
			"content-type": object.httpMetadata?.contentType ?? "application/octet-stream",
			"cache-control": "public, max-age=31536000, immutable",
			etag: object.httpEtag,
		});

		// The digest is in the key, so a client can verify what it downloaded
		// without trusting the transport.
		headers.set("x-content-sha256", match[1] as string);

		if (request.headers.get("if-none-match") === object.httpEtag) {
			return new Response(null, { status: 304, headers });
		}

		return new Response(object.body, { status: 200, headers });
	},
};
