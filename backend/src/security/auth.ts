import type { Env } from "../types.js";
import { ApiError, ErrorCode } from "./errors.js";

/**
 * Admin authentication via Cloudflare Access.
 *
 * Access sits in front of the admin routes and mints a signed JWT in the
 * `Cf-Access-Jwt-Assertion` header. The Worker re-verifies that signature
 * itself rather than trusting the header's contents: a client-supplied email
 * header proves nothing, and Access can be bypassed if the Worker is ever
 * reachable on a route Access does not cover.
 *
 * Verification checks, in order: signature against the team's published keys,
 * issuer, audience, expiry, and finally membership of the staff allowlist.
 */

export interface AdminIdentity {
	/** Verified email of the signed-in editor, recorded on the rows they change. */
	readonly email: string;
}

interface JwtHeader {
	readonly alg: string;
	readonly kid: string;
}

interface AccessClaims {
	readonly aud?: string | string[];
	readonly iss?: string;
	readonly exp?: number;
	readonly nbf?: number;
	readonly email?: string;
}

interface Jwk {
	readonly kid: string;
	readonly kty: string;
	readonly alg?: string;
}

/** Cached signing keys, keyed by team domain. Access rotates these rarely. */
const KEY_CACHE = new Map<string, { keys: Map<string, CryptoKey>; expiresAt: number }>();
const KEY_CACHE_TTL_MS = 60 * 60 * 1000;

const unauthorized = (reason: string): ApiError =>
	new ApiError(401, ErrorCode.UNAUTHORIZED, "Administrator sign-in is required.", { reason });

function decodeBase64Url(value: string): Uint8Array {
	const padded = value.replaceAll("-", "+").replaceAll("_", "/");
	const binary = atob(padded.padEnd(padded.length + ((4 - (padded.length % 4)) % 4), "="));

	return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function decodeJsonSegment<T>(segment: string): T {
	try {
		return JSON.parse(new TextDecoder().decode(decodeBase64Url(segment))) as T;
	} catch {
		throw unauthorized("malformed token segment");
	}
}

async function signingKeysFor(teamDomain: string): Promise<Map<string, CryptoKey>> {
	const cached = KEY_CACHE.get(teamDomain);
	if (cached !== undefined && cached.expiresAt > Date.now()) {
		return cached.keys;
	}

	const response = await fetch(`https://${teamDomain}/cdn-cgi/access/certs`);
	if (!response.ok) {
		throw new ApiError(503, ErrorCode.INTERNAL_ERROR, "The service is temporarily unavailable.", {
			reason: "access certs unavailable",
			status: response.status,
		});
	}

	const document = (await response.json()) as { keys?: Jwk[] };
	const keys = new Map<string, CryptoKey>();

	for (const jwk of document.keys ?? []) {
		if (jwk.kty !== "RSA") {
			continue;
		}

		const key = await crypto.subtle.importKey(
			"jwk",
			jwk as JsonWebKey,
			{ name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
			false,
			["verify"],
		);
		keys.set(jwk.kid, key);
	}

	KEY_CACHE.set(teamDomain, { keys, expiresAt: Date.now() + KEY_CACHE_TTL_MS });
	return keys;
}

function allowedEmails(env: Env): string[] {
	return (env.ADMIN_ALLOWED_EMAILS ?? "")
		.split(",")
		.map((entry) => entry.trim().toLowerCase())
		.filter((entry) => entry.length > 0);
}

/**
 * Local-only escape hatch.
 *
 * `wrangler dev` cannot mint an Access token, so development accepts an
 * explicit header instead. It is gated on ENVIRONMENT — a wrangler var, not a
 * request value — *and* on the email already being in the allowlist, so it
 * cannot be reached on a deployed Worker even if the header is sent.
 */
function developmentIdentity(request: Request, env: Env): AdminIdentity | undefined {
	if (env.ENVIRONMENT !== "development") {
		return undefined;
	}

	const email = request.headers.get("x-dev-admin-email")?.trim().toLowerCase();
	if (email === undefined || email.length === 0) {
		return undefined;
	}

	if (!allowedEmails(env).includes(email)) {
		return undefined;
	}

	return { email };
}

/**
 * Verifies the caller is an authorised administrator.
 *
 * Throws 401 for a missing, malformed, expired, or wrongly-signed token, and
 * 403 for a validly signed token whose email is not on the staff allowlist —
 * the distinction matters when diagnosing access problems.
 */
export async function requireAdmin(request: Request, env: Env): Promise<AdminIdentity> {
	const development = developmentIdentity(request, env);
	if (development !== undefined) {
		return development;
	}

	// The token is checked before the configuration on purpose: an anonymous
	// caller gets a plain 401 either way and learns nothing about whether Access
	// is set up. A 503 is reserved for a caller who did present a token.
	const token = request.headers.get("cf-access-jwt-assertion");
	if (token === null || token.length === 0) {
		throw unauthorized("missing assertion header");
	}

	const teamDomain = env.CF_ACCESS_TEAM_DOMAIN?.trim();
	const audience = env.CF_ACCESS_AUD?.trim();
	if (!teamDomain || !audience) {
		throw new ApiError(503, ErrorCode.INTERNAL_ERROR, "The service is temporarily unavailable.", {
			reason: "access is not configured",
		});
	}

	const [headerSegment, payloadSegment, signatureSegment] = token.split(".");
	if (
		headerSegment === undefined ||
		payloadSegment === undefined ||
		signatureSegment === undefined
	) {
		throw unauthorized("malformed token");
	}

	const header = decodeJsonSegment<JwtHeader>(headerSegment);
	if (header.alg !== "RS256") {
		throw unauthorized("unexpected signing algorithm");
	}

	const keys = await signingKeysFor(teamDomain);
	const key = keys.get(header.kid);
	if (key === undefined) {
		throw unauthorized("unknown signing key");
	}

	const verified = await crypto.subtle.verify(
		"RSASSA-PKCS1-v1_5",
		key,
		decodeBase64Url(signatureSegment),
		new TextEncoder().encode(`${headerSegment}.${payloadSegment}`),
	);
	if (!verified) {
		throw unauthorized("bad signature");
	}

	const claims = decodeJsonSegment<AccessClaims>(payloadSegment);
	const nowSeconds = Math.floor(Date.now() / 1000);

	if (claims.iss !== `https://${teamDomain}`) {
		throw unauthorized("unexpected issuer");
	}

	const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
	if (!audiences.includes(audience)) {
		throw unauthorized("unexpected audience");
	}

	if (claims.exp === undefined || claims.exp <= nowSeconds) {
		throw unauthorized("expired token");
	}

	if (claims.nbf !== undefined && claims.nbf > nowSeconds) {
		throw unauthorized("token not yet valid");
	}

	const email = claims.email?.trim().toLowerCase();
	if (email === undefined || email.length === 0) {
		throw unauthorized("token carries no email");
	}

	if (!allowedEmails(env).includes(email)) {
		throw new ApiError(403, ErrorCode.FORBIDDEN, "That account cannot edit this content.", {
			reason: "email not on allowlist",
		});
	}

	return { email };
}
