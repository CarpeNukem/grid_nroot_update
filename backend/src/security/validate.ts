import { ApiError, ErrorCode } from "./errors.js";

/**
 * Input allowlists and length limits.
 *
 * Every public route is assumed to be called by modified clients and bots, so
 * identifiers are validated against a narrow character set before they reach
 * D1 or become part of an R2 key.
 */

/** Slug identifiers: lowercase alphanumerics separated by single hyphens. */
const IDENTIFIER_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

export const LIMITS = {
	identifierMaxLength: 64,
	categoryMaxLength: 32,
	/** Largest JSON body accepted on any write route. */
	requestBodyMaxBytes: 64 * 1024,
	/** Largest image accepted by media upload. */
	mediaMaxBytes: 4 * 1024 * 1024,
} as const;

/**
 * Validates a slug identifier (profile id, category, media group).
 *
 * Rejects empty values, over-long values, uppercase, underscores, dots, and
 * path separators — which also rules out `..` traversal in derived R2 keys.
 */
export function assertIdentifier(
	value: string | undefined,
	field: string,
	maxLength: number = LIMITS.identifierMaxLength,
): string {
	if (value === undefined || value.length === 0) {
		throw new ApiError(400, ErrorCode.INVALID_IDENTIFIER, `A ${field} is required.`);
	}

	if (value.length > maxLength || !IDENTIFIER_PATTERN.test(value)) {
		throw new ApiError(400, ErrorCode.INVALID_IDENTIFIER, `That ${field} is not valid.`, {
			field,
			length: value.length,
		});
	}

	return value;
}

/** True when `value` is a valid identifier, without throwing. */
export function isIdentifier(
	value: string,
	maxLength: number = LIMITS.identifierMaxLength,
): boolean {
	return value.length > 0 && value.length <= maxLength && IDENTIFIER_PATTERN.test(value);
}
