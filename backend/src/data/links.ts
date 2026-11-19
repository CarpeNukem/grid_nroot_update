import { ApiError, badRequest, ErrorCode } from "../security/errors.js";
import type { Env } from "../types.js";
import {
	isDiscordAttachment,
	type RefreshFailure,
	type RefreshResult,
	refreshAttachmentUrl,
	signatureExpiry,
	withoutSignature,
} from "./discord.js";

/**
 * Short links: `grid.nroot.io/s/<code>` redirects to an address staff chose.
 *
 * Made only through the admin API, so this is not an open shortener — nobody
 * outside the venue can use the domain to dress up an address of their own.
 * Following a link is public and metered like any other public read.
 *
 * Built mostly for Discord attachments, whose addresses stop working a day
 * after they are copied (see `discord.ts`). A link to one is re-signed on the
 * way through, so it keeps working for as long as the file is on Discord.
 */

export interface ShortLinkRow {
	readonly code: string;
	readonly target: string;
	readonly canonical: string;
	readonly refreshed_url: string | null;
	readonly created_by: string;
	readonly created_at: string;
}

/**
 * Generated codes use lowercase letters and digits without the four that are
 * easy to misread — `l`, `1`, `o`, `0` — so a code read off a screen or typed
 * from a screenshot still works. 32 characters, so each random byte maps to
 * one without bias.
 */
const CODE_ALPHABET = "abcdefghijkmnpqrstuvwxyz23456789";

/** Six characters is about a billion codes: no collisions to speak of, and too many to walk. */
export const GENERATED_CODE_LENGTH = 6;

/**
 * Chosen codes: lowercase letters, digits and inner dashes, up to 40.
 * Codes are matched without regard to case, so `/s/Flyer` finds `flyer`.
 */
const CODE_PATTERN = /^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$/;

/** Longest address accepted. Discord's own are a few hundred characters. */
export const MAX_TARGET_LENGTH = 2048;

/**
 * How long before a Discord signature runs out the link asks for a new one.
 * An hour leaves room for a browser or Discord's embed proxy to sit on the
 * redirect for a while and still land on an address that works.
 */
const REFRESH_MARGIN_MS = 60 * 60 * 1000;

export const normaliseCode = (raw: string): string => raw.trim().toLowerCase();

export const isLinkCode = (value: unknown): value is string =>
	typeof value === "string" && CODE_PATTERN.test(value);

export function generateCode(): string {
	const bytes = crypto.getRandomValues(new Uint8Array(GENERATED_CODE_LENGTH));

	return Array.from(bytes, (byte) => CODE_ALPHABET[byte % CODE_ALPHABET.length]).join("");
}

/** The configured base, e.g. `https://grid.nroot.io/s`, without a trailing slash. */
export const shortLinkBase = (env: Env): string =>
	env.SHORT_LINK_BASE_URL.trim().replace(/\/+$/, "");

export const shortUrlFor = (env: Env, code: string): string => `${shortLinkBase(env)}/${code}`;

export interface ParsedTarget {
	readonly target: string;
	readonly canonical: string;
	readonly discord: boolean;
}

/**
 * Validates an address to shorten.
 *
 * Absolute http or https only: anything else (`javascript:`, `data:`, a bare
 * path) would make the short link a way to run or smuggle something rather
 * than a way to reach a page. No embedded credentials, and no pointing a link
 * at another short link, which could only ever loop or hide where it goes.
 */
export function parseTarget(raw: unknown, env: Env): ParsedTarget {
	if (typeof raw !== "string" || raw.trim().length === 0) {
		throw badRequest("Paste the address to shorten.");
	}

	const text = raw.trim();
	if (text.length > MAX_TARGET_LENGTH) {
		throw badRequest(`That address is longer than ${MAX_TARGET_LENGTH} characters.`);
	}

	let url: URL;
	try {
		url = new URL(text);
	} catch {
		throw badRequest("That is not a full web address. It should start with https://");
	}

	if (url.protocol !== "https:" && url.protocol !== "http:") {
		throw badRequest("Only http and https addresses can be shortened.");
	}

	if (url.username.length > 0 || url.password.length > 0) {
		throw badRequest("That address has a username or password in it.");
	}

	const base = new URL(`${shortLinkBase(env)}/`);
	if (url.host === base.host && url.pathname.startsWith(base.pathname)) {
		throw badRequest("That is already a short link.");
	}

	const discord = isDiscordAttachment(url);

	return { target: url.href, canonical: discord ? withoutSignature(url) : url.href, discord };
}

export async function getLink(db: D1Database, code: string): Promise<ShortLinkRow | null> {
	return db
		.prepare(
			"SELECT code, target, canonical, refreshed_url, created_by, created_at FROM short_links WHERE code = ?1",
		)
		.bind(code)
		.first<ShortLinkRow>();
}

/** The oldest link already made for this address, if any. */
export async function findLinkFor(db: D1Database, canonical: string): Promise<ShortLinkRow | null> {
	return db
		.prepare(
			`SELECT code, target, canonical, refreshed_url, created_by, created_at FROM short_links
			WHERE canonical = ?1 ORDER BY created_at ASC, code ASC LIMIT 1`,
		)
		.bind(canonical)
		.first<ShortLinkRow>();
}

/** Newest first: the link just made is the one most likely wanted again. */
export async function listLinks(db: D1Database): Promise<ShortLinkRow[]> {
	const { results } = await db
		.prepare(
			`SELECT code, target, canonical, refreshed_url, created_by, created_at FROM short_links
			ORDER BY created_at DESC, code ASC`,
		)
		.all<ShortLinkRow>();

	return results;
}

/** Inserts the link unless its code is taken. Says whether it went in. */
async function insertLink(db: D1Database, row: ShortLinkRow): Promise<boolean> {
	const result = await db
		.prepare(
			`INSERT INTO short_links (code, target, canonical, refreshed_url, created_by, created_at)
			VALUES (?1, ?2, ?3, NULL, ?4, ?5) ON CONFLICT (code) DO NOTHING`,
		)
		.bind(row.code, row.target, row.canonical, row.created_by, row.created_at)
		.run();

	return result.meta.changes > 0;
}

/** Tries a few random codes. Running out is a near-impossibility, reported rather than looped on. */
const GENERATION_ATTEMPTS = 5;

export interface CreatedLink {
	readonly row: ShortLinkRow;
	/** True when the address already had a link and that one is being handed back. */
	readonly existing: boolean;
}

/**
 * Makes a short link.
 *
 * With a chosen code, that code or a 409. Without one, an address that
 * already has a link gets the same link back — the same Discord file pasted
 * on two different days is one link, not two.
 */
export async function createLink(
	db: D1Database,
	target: ParsedTarget,
	requestedCode: string | undefined,
	createdBy: string,
	now: Date,
): Promise<CreatedLink> {
	const base = {
		target: target.target,
		canonical: target.canonical,
		refreshed_url: null,
		created_by: createdBy,
		created_at: now.toISOString(),
	};

	if (requestedCode !== undefined) {
		const row = { ...base, code: requestedCode };
		if (!(await insertLink(db, row))) {
			throw new ApiError(409, ErrorCode.ALREADY_EXISTS, `The code "${requestedCode}" is taken.`);
		}

		return { row, existing: false };
	}

	const previous = await findLinkFor(db, target.canonical);
	if (previous !== null) {
		return { row: previous, existing: true };
	}

	for (let attempt = 0; attempt < GENERATION_ATTEMPTS; attempt += 1) {
		const row = { ...base, code: generateCode() };
		if (await insertLink(db, row)) {
			return { row, existing: false };
		}
	}

	throw new ApiError(503, ErrorCode.INTERNAL_ERROR, "Could not find a free code. Try again.", {
		reason: "short link codes exhausted",
	});
}

export async function deleteLink(db: D1Database, code: string): Promise<boolean> {
	const result = await db.prepare("DELETE FROM short_links WHERE code = ?1").bind(code).run();

	return result.meta.changes > 0;
}

/** What following a link resolved to, and what it cost to get there. */
export interface Destination {
	readonly url: string;
	/** Set when Discord was asked for a fresh signature and did not give one. */
	readonly refreshFailure?: RefreshFailure;
	/** Set when Discord re-signed the address on this request. */
	readonly refreshed?: true;
}

/**
 * Where a link sends its visitor right now.
 *
 * Anything but a Discord attachment goes where it was pointed. A Discord
 * attachment goes to the freshest signed address held, which is asked for
 * again once it is within an hour of running out. Without a bot token, or if
 * Discord will not re-sign it (the message was deleted, the token revoked),
 * the visitor is sent to the last address held: Discord's own "no longer
 * available" page says what happened better than an error from here would.
 */
export async function destinationFor(
	link: ShortLinkRow,
	env: Env,
	now: Date,
): Promise<Destination> {
	if (!isDiscordAttachment(new URL(link.target))) {
		return { url: link.target };
	}

	const current = link.refreshed_url ?? link.target;
	const expiry = signatureExpiry(current);
	if (expiry !== undefined && expiry.getTime() - now.getTime() > REFRESH_MARGIN_MS) {
		return { url: current };
	}

	const token = env.DISCORD_BOT_TOKEN?.trim() ?? "";
	if (token.length === 0) {
		return { url: current };
	}

	const quietUntil = refreshBackoff.get(link.code);
	if (quietUntil !== undefined && quietUntil > now.getTime()) {
		return { url: current };
	}

	// Clicks that arrive together while a refresh is out share its answer
	// instead of each asking Discord and each writing the row.
	let pending = refreshesInFlight.get(link.code);
	if (pending === undefined) {
		pending = refreshAndStore(link, token, env);
		refreshesInFlight.set(link.code, pending);
		pending.finally(() => refreshesInFlight.delete(link.code)).catch(() => {});
	}

	const result = await pending;
	if (!result.ok) {
		refreshBackoff.set(link.code, now.getTime() + REFRESH_BACKOFF_MS);
		const { ok: _ok, ...refreshFailure } = result;
		return { url: current, refreshFailure };
	}

	refreshBackoff.delete(link.code);
	return { url: result.url, refreshed: true };
}

/**
 * How long a link waits after Discord refuses to re-sign it before asking
 * again. A deleted message or a revoked token fails every time, and every
 * refusal counts toward the invalid-request allowance Discord gives the
 * address the Worker calls from, so retrying on each click would only hurt.
 *
 * Held in memory, not D1: it costs no writes, and losing it when the isolate
 * is recycled only means one extra call.
 */
const REFRESH_BACKOFF_MS = 10 * 60 * 1000;
const refreshBackoff = new Map<string, number>();

const refreshesInFlight = new Map<string, Promise<RefreshResult>>();

/** One call to Discord and, if it worked, the one D1 write a refresh costs. */
async function refreshAndStore(
	link: ShortLinkRow,
	token: string,
	env: Env,
): Promise<RefreshResult> {
	const result = await refreshAttachmentUrl(link.target, token, new URL(shortLinkBase(env)).origin);
	if (result.ok) {
		await env.DB.prepare("UPDATE short_links SET refreshed_url = ?2 WHERE code = ?1")
			.bind(link.code, result.url)
			.run();
	}

	return result;
}
