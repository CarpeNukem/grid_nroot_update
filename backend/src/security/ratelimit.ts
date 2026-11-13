import type { Env } from "../types.js";
import { ApiError, ErrorCode } from "./errors.js";
import { isAdminPath } from "./headers.js";

/**
 * Per-caller limits on the public API.
 *
 * Editing is behind Cloudflare Access, so the exposure that remains is
 * anonymous: the reads, and the arcade's score submission, which is the one
 * anonymous write and has a tighter limit of its own further down. Every
 * public route costs a D1 query, and an unthrottled client can burn the
 * account's read quota until the venue screens go blank. That is the failure
 * this prevents — not a breach, an outage.
 *
 * Media is deliberately exempt. Objects are immutable and edge-cached for a
 * year, so repeat fetches rarely reach the Worker at all, and throttling them
 * would break image loading in the deck rather than protecting anything.
 */

/**
 * Generous next to real use: a deck polls once a minute, so it spends a
 * sixtieth of its own budget. This is a guard against a runaway client, not a
 * throttle anybody legitimate should ever meet.
 */
const REQUESTS_PER_MINUTE = 60;

const isMediaPath = (pathname: string): boolean => pathname.startsWith("/media/");

/** Public read routes are the only ones metered. */
export const isRateLimited = (pathname: string): boolean =>
	!isAdminPath(pathname) && !isMediaPath(pathname) && pathname !== "/admin";

/**
 * Identifies the caller.
 *
 * `CF-Connecting-IP` is set by Cloudflare itself and cannot be spoofed by the
 * client — unlike `X-Forwarded-For`, which is why that one is not used. Absent
 * an IP everything shares one bucket, which fails safe: unidentifiable traffic
 * gets throttled together rather than escaping the limit.
 */
const callerKey = (request: Request): string =>
	request.headers.get("cf-connecting-ip") ?? "unidentified";

/**
 * Applies the limit, throwing 429 once a caller exceeds it.
 *
 * A missing binding is not an error: local development and the test pool do not
 * always provide one, and the limiter is a safeguard rather than a correctness
 * requirement. It is wired in production, which is where the quota lives.
 */
export async function enforceRateLimit(request: Request, env: Env): Promise<void> {
	const limiter = env.PUBLIC_READ_LIMITER;
	if (limiter === undefined) {
		return;
	}

	const { success } = await limiter.limit({ key: callerKey(request) });
	if (success) {
		return;
	}

	throw new ApiError(429, ErrorCode.RATE_LIMITED, "Too many requests. Try again shortly.", {
		limit: REQUESTS_PER_MINUTE,
	});
}

/**
 * A deck posts a score only when it would make a board, and batches anything
 * it could not send, so even a run of record-breaking games is a request a
 * game. Six a minute is still a ceiling no honest player meets — a game lasts
 * longer than ten seconds — while capping what a script can write.
 */
const SUBMISSIONS_PER_MINUTE = 6;

/**
 * The tighter limit on arcade score submissions, on top of the public one.
 *
 * Writes are what the free plan has least of, and this is the only route an
 * anonymous caller can write through. Same keying and the same fail-open when
 * no binding is present, for the same reasons.
 */
export async function enforceSubmitLimit(request: Request, env: Env): Promise<void> {
	const limiter = env.ARCADE_SUBMIT_LIMITER;
	if (limiter === undefined) {
		return;
	}

	const { success } = await limiter.limit({ key: callerKey(request) });
	if (success) {
		return;
	}

	throw new ApiError(429, ErrorCode.RATE_LIMITED, "Too many scores. Try again shortly.", {
		limit: SUBMISSIONS_PER_MINUTE,
	});
}
