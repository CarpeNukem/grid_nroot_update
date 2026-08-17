import { ApiError, ErrorCode } from "../security/errors.js";
import { isIdentifier, LIMITS } from "../security/validate.js";
import { fromDiscordTimestamp } from "./schema.js";

/**
 * Write-payload validation, shared by the admin routes and the seed importer.
 *
 * Deliberately dependency-free and free of Worker globals so the importer can
 * run under plain Node and reject bad seed data before it reaches D1 — one set
 * of rules, not two that drift.
 *
 * Two rules shape everything here:
 *  - Unknown fields are rejected, so a typo'd key fails loudly instead of being
 *    silently dropped on save.
 *  - `published` is never accepted on create or update. Publishing is its own
 *    explicit route, so editing a profile cannot expose it by accident.
 */

export const FIELD_LIMITS = {
	name: 200,
	title: 200,
	category: LIMITS.categoryMaxLength,
	characterName: 128,
	shortText: 400,
	mediumText: 2_000,
	longText: 8_000,
	bundledImage: 128,
	imageKey: 256,
	link: 500,
	maxPriceGil: 100_000_000,
	maxSortOrder: 100_000,
} as const;

/** Bundled art filename. No directories, no traversal, image extensions only. */
const BUNDLED_IMAGE_PATTERN = /^[a-z0-9][a-z0-9_-]*\.(?:png|jpg|jpeg|webp)$/;

/**
 * R2 object key: `<group>/<slug>/<sha256>.<ext>`.
 *
 * Content-hashed and immutable. Admin edits normally leave this alone — media
 * upload sets it — but the shape is enforced here so a hand-written key can
 * never contain `..` or an absolute URL.
 */
const IMAGE_KEY_PATTERN =
	/^(?:profiles|menu|news)\/[a-z0-9]+(?:-[a-z0-9]+)*\/[a-f0-9]{64}\.(?:png|jpg|jpeg|webp|gif|mp4)$/;

/**
 * C0 controls other than tab, newline, and carriage return.
 *
 * Combining marks, emoji, and non-Latin scripts are explicitly not rejected —
 * the existing profile text stacks combining marks as a visual effect.
 */
// biome-ignore lint/suspicious/noControlCharactersInRegex: rejecting control characters is the point
const CONTROL_CHARACTERS = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/;

/**
 * A field-level rejection.
 *
 * `reason` completes the sentence "The <field> field ...", so it reads as a
 * clause with its own verb — "is required", "must be text". These strings are
 * shown to whoever is editing, so they have to be sentences.
 */
const invalid = (field: string, reason: string, context?: Record<string, unknown>): ApiError =>
	new ApiError(400, ErrorCode.BAD_REQUEST, `The ${field} field ${reason}.`, {
		field,
		...context,
	});

/** Narrows an unknown parsed body to a plain object. */
export function assertObject(value: unknown): Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) {
		throw new ApiError(400, ErrorCode.BAD_REQUEST, "A JSON object body is required.");
	}

	return value as Record<string, unknown>;
}

/** Rejects keys outside the allowlist so a misspelled field is never ignored. */
export function assertNoUnknownFields(
	input: Record<string, unknown>,
	allowed: readonly string[],
): void {
	const unknown = Object.keys(input).filter((key) => !allowed.includes(key));
	if (unknown.length > 0) {
		throw new ApiError(
			400,
			ErrorCode.BAD_REQUEST,
			`Unrecognised field: ${unknown.slice(0, 3).join(", ")}.`,
			{ unknownFields: unknown },
		);
	}
}

function text(value: unknown, field: string, maxLength: number): string {
	if (typeof value !== "string") {
		throw invalid(field, "must be text");
	}

	const trimmed = value.trim();
	if (trimmed.length > maxLength) {
		throw invalid(field, `is longer than ${maxLength} characters`, { length: trimmed.length });
	}

	if (CONTROL_CHARACTERS.test(trimmed)) {
		throw invalid(field, "contains control characters");
	}

	return trimmed;
}

export function requiredText(
	input: Record<string, unknown>,
	field: string,
	maxLength: number,
): string {
	// Absent is reported as missing, not as the wrong type — "is required" is
	// what an editor needs to see when they simply left a field out.
	if (input[field] === undefined || input[field] === null) {
		throw invalid(field, "is required");
	}

	const value = text(input[field], field, maxLength);
	if (value.length === 0) {
		throw invalid(field, "is required");
	}

	return value;
}

export function optionalText(
	input: Record<string, unknown>,
	field: string,
	maxLength: number,
): string {
	if (input[field] === undefined || input[field] === null) {
		return "";
	}

	return text(input[field], field, maxLength);
}

export function requiredSlug(input: Record<string, unknown>, field: string): string {
	const value = requiredText(input, field, LIMITS.identifierMaxLength);
	if (!isIdentifier(value)) {
		throw new ApiError(
			400,
			ErrorCode.INVALID_IDENTIFIER,
			`That ${field} is not valid. Use lowercase words separated by hyphens.`,
			{ field },
		);
	}

	return value;
}

export function optionalInt(
	input: Record<string, unknown>,
	field: string,
	min: number,
	max: number,
	fallback: number,
): number {
	if (input[field] === undefined || input[field] === null) {
		return fallback;
	}

	const value = input[field];
	if (typeof value !== "number" || !Number.isInteger(value)) {
		throw invalid(field, "must be a whole number");
	}

	if (value < min || value > max) {
		throw invalid(field, `is outside the allowed range ${min}–${max}`);
	}

	return value;
}

export function optionalBool(
	input: Record<string, unknown>,
	field: string,
	fallback: boolean,
): boolean {
	if (input[field] === undefined || input[field] === null) {
		return fallback;
	}

	if (typeof input[field] !== "boolean") {
		throw invalid(field, "must be true or false");
	}

	return input[field];
}

export function optionalBundledImage(input: Record<string, unknown>, field: string): string {
	const value = optionalText(input, field, FIELD_LIMITS.bundledImage);
	if (value.length > 0 && !BUNDLED_IMAGE_PATTERN.test(value)) {
		throw invalid(field, "is not a valid image filename");
	}

	return value;
}

export function optionalImageKey(input: Record<string, unknown>, field: string): string {
	const value = optionalText(input, field, FIELD_LIMITS.imageKey);
	if (value.length > 0 && !IMAGE_KEY_PATTERN.test(value)) {
		throw invalid(field, "is not a valid media key");
	}

	return value;
}

/**
 * Normalises a date to a UTC instant.
 *
 * Accepts an ISO 8601 date or timestamp, or a Discord `<t:seconds:style>`
 * string so an announcement can be written by pasting the same value that went
 * into the Discord post.
 *
 * Round-tripping through Date rejects both unparseable text and impossible
 * dates such as `2026-02-30`, which Date would otherwise roll forward.
 */
function toInstant(value: string, field: string): string {
	const discord = fromDiscordTimestamp(value);
	if (discord !== undefined) {
		return discord;
	}

	// A bare `<t:...>` that failed to parse is a malformed Discord timestamp,
	// not a date to be guessed at.
	if (value.startsWith("<t:")) {
		throw invalid(field, "is not a valid Discord timestamp");
	}

	const parsed = new Date(value);
	if (Number.isNaN(parsed.getTime())) {
		throw invalid(field, "is not a valid date");
	}

	const isDateOnly = /^\d{4}-\d{2}-\d{2}$/.test(value);
	if (isDateOnly && parsed.toISOString().slice(0, 10) !== value) {
		throw invalid(field, "is not a valid date");
	}

	return parsed.toISOString();
}

export function requiredInstant(input: Record<string, unknown>, field: string): string {
	return toInstant(requiredText(input, field, 64), field);
}

/** Optional date. Empty string means "no date", not an error. */
export function optionalInstant(input: Record<string, unknown>, field: string): string {
	const value = optionalText(input, field, 64);
	return value.length === 0 ? "" : toInstant(value, field);
}

/**
 * An outbound link.
 *
 * https only. A client may hand this to the operating system to open, so a
 * stored `javascript:`, `data:`, or `file:` URI would be a real hazard —
 * scheme is an allowlist, not a denylist. Embedded credentials are rejected
 * too, since a `user:pass@host` link is a phishing shape.
 */
export function optionalLink(input: Record<string, unknown>, field: string): string {
	const value = optionalText(input, field, FIELD_LIMITS.link);
	if (value.length === 0) {
		return "";
	}

	let url: URL;
	try {
		url = new URL(value);
	} catch {
		throw invalid(field, "is not a valid URL");
	}

	if (url.protocol !== "https:") {
		throw invalid(field, "must be an https link");
	}

	if (url.hostname.length === 0 || url.username.length > 0 || url.password.length > 0) {
		throw invalid(field, "is not a valid URL");
	}

	return url.toString();
}

// ------------------------------------------------------------------ inputs

export interface ProfileInput {
	readonly id: string;
	readonly category: string;
	readonly name: string;
	readonly characterName: string;
	readonly age: string;
	readonly affiliation: string;
	readonly occupation: string;
	readonly bio: string;
	readonly optional: Record<string, string> | null;
	readonly imageKey: string;
	readonly bundledImage: string;
	readonly logoKey: string;
	readonly logoImage: string;
	readonly requestLabel: string;
	readonly requestMessage: string;
	readonly sortOrder: number;
}

const PROFILE_FIELDS = [
	"id",
	"category",
	"name",
	"characterName",
	"age",
	"affiliation",
	"occupation",
	"bio",
	"optional",
	"image",
	"imageKey",
	"bundledImage",
	"logoKey",
	"logoImage",
	"requestLabel",
	"requestMessage",
	"sortOrder",
] as const;

/** Keys allowed inside the nested `optional` block. */
const PROFILE_OPTIONAL_FIELDS = [
	"pronunciation",
	"pronouns",
	"race",
	"availability",
	"quote",
] as const;

function parseProfileOptional(value: unknown): Record<string, string> | null {
	if (value === undefined || value === null) {
		return null;
	}

	const block = assertObject(value);
	assertNoUnknownFields(block, PROFILE_OPTIONAL_FIELDS);

	const parsed: Record<string, string> = {};
	for (const field of PROFILE_OPTIONAL_FIELDS) {
		const entry = optionalText(block, field, FIELD_LIMITS.mediumText);
		if (entry.length > 0) {
			parsed[field] = entry;
		}
	}

	return Object.keys(parsed).length === 0 ? null : parsed;
}

/**
 * Validates a profile.
 *
 * `image` is accepted as an alias for `bundledImage` because that is the key
 * staff_profiles.json uses; the importer reads that file directly rather than
 * maintaining a second, divergent format.
 */
export function parseProfileInput(body: unknown, id?: string): ProfileInput {
	const input = assertObject(body);
	assertNoUnknownFields(input, PROFILE_FIELDS);

	const bundled =
		input.bundledImage === undefined
			? optionalBundledImage(input, "image")
			: optionalBundledImage(input, "bundledImage");

	return {
		id: id ?? requiredSlug(input, "id"),
		category: requiredSlug(input, "category"),
		name: requiredText(input, "name", FIELD_LIMITS.name),
		characterName: requiredText(input, "characterName", FIELD_LIMITS.characterName),
		age: optionalText(input, "age", FIELD_LIMITS.shortText),
		affiliation: optionalText(input, "affiliation", FIELD_LIMITS.shortText),
		occupation: optionalText(input, "occupation", FIELD_LIMITS.shortText),
		bio: optionalText(input, "bio", FIELD_LIMITS.longText),
		optional: parseProfileOptional(input.optional),
		imageKey: optionalImageKey(input, "imageKey"),
		bundledImage: bundled,
		logoKey: optionalImageKey(input, "logoKey"),
		logoImage: optionalBundledImage(input, "logoImage"),
		requestLabel: optionalText(input, "requestLabel", FIELD_LIMITS.shortText),
		requestMessage: optionalText(input, "requestMessage", FIELD_LIMITS.mediumText),
		sortOrder: optionalInt(input, "sortOrder", 0, FIELD_LIMITS.maxSortOrder, 0),
	};
}

export interface MenuItemInput {
	readonly id: string;
	readonly name: string;
	readonly priceGil: number;
	readonly ingredients: string;
	readonly description: string;
	readonly taste: string;
	readonly imageKey: string;
	readonly bundledImage: string;
	readonly sortOrder: number;
}

const MENU_FIELDS = [
	"id",
	"name",
	"priceGil",
	"ingredients",
	"description",
	"taste",
	"imageKey",
	"bundledImage",
	"sortOrder",
] as const;

export function parseMenuItemInput(body: unknown, id?: string): MenuItemInput {
	const input = assertObject(body);
	assertNoUnknownFields(input, MENU_FIELDS);

	return {
		id: id ?? requiredSlug(input, "id"),
		name: requiredText(input, "name", FIELD_LIMITS.name),
		priceGil: optionalInt(input, "priceGil", 0, FIELD_LIMITS.maxPriceGil, 0),
		ingredients: optionalText(input, "ingredients", FIELD_LIMITS.mediumText),
		description: optionalText(input, "description", FIELD_LIMITS.mediumText),
		taste: optionalText(input, "taste", FIELD_LIMITS.mediumText),
		imageKey: optionalImageKey(input, "imageKey"),
		bundledImage: optionalBundledImage(input, "bundledImage"),
		sortOrder: optionalInt(input, "sortOrder", 0, FIELD_LIMITS.maxSortOrder, 0),
	};
}

export interface PageInput {
	readonly id: string;
	readonly title: string;
	readonly body: string;
	readonly sortOrder: number;
}

const PAGE_FIELDS = ["id", "title", "body", "sortOrder"] as const;

/**
 * Validates a prose block.
 *
 * `body` is markdown and gets the long-text budget. Newlines and tabs survive
 * the control-character check — they are the formatting — while the rest of the
 * C0 range is still refused.
 */
export function parsePageInput(body: unknown, id?: string): PageInput {
	const input = assertObject(body);
	assertNoUnknownFields(input, PAGE_FIELDS);

	return {
		id: id ?? requiredSlug(input, "id"),
		title: requiredText(input, "title", FIELD_LIMITS.title),
		body: optionalText(input, "body", FIELD_LIMITS.longText),
		sortOrder: optionalInt(input, "sortOrder", 0, FIELD_LIMITS.maxSortOrder, 0),
	};
}

export interface NewsPostInput {
	readonly id: string;
	readonly title: string;
	readonly summary: string;
	readonly body: string;
	/** R2 key of the flyer. */
	readonly imageKey: string;
	/** Bundled flyer filename, used when there is no R2 object. */
	readonly bundledImage: string;
	readonly pinned: boolean;
	readonly publishedAt: string;
	/** Empty when the announcement is not tied to an event. */
	readonly eventAt: string;
	readonly link: string;
	readonly linkLabel: string;
}

const NEWS_FIELDS = [
	"id",
	"title",
	"summary",
	"body",
	"flyerKey",
	"imageKey",
	"flyerImage",
	"bundledImage",
	"pinned",
	"publishedAt",
	"eventAt",
	"link",
	"linkLabel",
] as const;

/**
 * Validates an announcement.
 *
 * `flyerKey` and `flyerImage` are the names the news API uses publicly;
 * `imageKey` and `bundledImage` are accepted as aliases so the shape stays
 * interchangeable with the other two collections.
 */
export function parseNewsPostInput(body: unknown, id?: string): NewsPostInput {
	const input = assertObject(body);
	assertNoUnknownFields(input, NEWS_FIELDS);

	const flyerKey =
		input.flyerKey === undefined
			? optionalImageKey(input, "imageKey")
			: optionalImageKey(input, "flyerKey");
	const flyerImage =
		input.flyerImage === undefined
			? optionalBundledImage(input, "bundledImage")
			: optionalBundledImage(input, "flyerImage");

	return {
		id: id ?? requiredSlug(input, "id"),
		title: requiredText(input, "title", FIELD_LIMITS.title),
		summary: optionalText(input, "summary", FIELD_LIMITS.shortText),
		body: optionalText(input, "body", FIELD_LIMITS.longText),
		imageKey: flyerKey,
		bundledImage: flyerImage,
		pinned: optionalBool(input, "pinned", false),
		publishedAt: requiredInstant(input, "publishedAt"),
		eventAt: optionalInstant(input, "eventAt"),
		link: optionalLink(input, "link"),
		linkLabel: optionalText(input, "linkLabel", FIELD_LIMITS.shortText),
	};
}
