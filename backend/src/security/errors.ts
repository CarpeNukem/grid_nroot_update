/**
 * Public error envelope.
 *
 * Clients only ever see `{ error: { code, message } }`. Database messages,
 * stack traces, and binding names stay in the structured log.
 */

export const ErrorCode = {
	BAD_REQUEST: "BAD_REQUEST",
	INVALID_IDENTIFIER: "INVALID_IDENTIFIER",
	METHOD_NOT_ALLOWED: "METHOD_NOT_ALLOWED",
	NOT_FOUND: "NOT_FOUND",
	PROFILE_NOT_FOUND: "PROFILE_NOT_FOUND",
	MENU_ITEM_NOT_FOUND: "MENU_ITEM_NOT_FOUND",
	NEWS_POST_NOT_FOUND: "NEWS_POST_NOT_FOUND",
	PAGE_NOT_FOUND: "PAGE_NOT_FOUND",
	ALREADY_EXISTS: "ALREADY_EXISTS",
	PAYLOAD_TOO_LARGE: "PAYLOAD_TOO_LARGE",
	UNAUTHORIZED: "UNAUTHORIZED",
	FORBIDDEN: "FORBIDDEN",
	RATE_LIMITED: "RATE_LIMITED",
	INTERNAL_ERROR: "INTERNAL_ERROR",
} as const;

export type ErrorCode = (typeof ErrorCode)[keyof typeof ErrorCode];

export interface ErrorBody {
	readonly error: {
		readonly code: ErrorCode;
		readonly message: string;
	};
}

/**
 * An error whose message is safe to return to an untrusted client.
 *
 * Anything thrown that is *not* an ApiError is reported as a generic 500, so
 * accidental detail leaks require deliberately constructing one of these.
 */
export class ApiError extends Error {
	readonly status: number;
	readonly code: ErrorCode;
	/** Extra context for the log only. Never serialised into the response. */
	readonly logContext: Readonly<Record<string, unknown>> | undefined;

	constructor(
		status: number,
		code: ErrorCode,
		message: string,
		logContext?: Readonly<Record<string, unknown>>,
	) {
		super(message);
		this.name = "ApiError";
		this.status = status;
		this.code = code;
		this.logContext = logContext;
	}

	toBody(): ErrorBody {
		return { error: { code: this.code, message: this.message } };
	}
}

export const notFound = (message = "The requested resource is unavailable."): ApiError =>
	new ApiError(404, ErrorCode.NOT_FOUND, message);

export const methodNotAllowed = (): ApiError =>
	new ApiError(405, ErrorCode.METHOD_NOT_ALLOWED, "That method is not supported on this route.");

export const badRequest = (
	message: string,
	logContext?: Readonly<Record<string, unknown>>,
): ApiError => new ApiError(400, ErrorCode.BAD_REQUEST, message, logContext);

/** Envelope for an unexpected failure. The real cause is logged, not returned. */
export const internalErrorBody = (): ErrorBody => ({
	error: {
		code: ErrorCode.INTERNAL_ERROR,
		message: "The service is temporarily unavailable.",
	},
});
