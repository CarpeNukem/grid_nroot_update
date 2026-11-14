/**
 * The pool hall's wire protocol: what a deck and the hall say to each other
 * over one WebSocket.
 *
 * Every frame is a JSON object with a `t` field naming it. The hall never
 * simulates a shot. The player whose turn it is plays it on their own machine
 * and sends two things: the shot itself, so the opponent can play the same
 * shot at the same moment, and the table once the balls have stopped, which
 * is what both decks keep. The hall checks whose turn it is and that the two
 * arrive in order, stores the settled table, and passes both on.
 *
 * That shape is what keeps the hall cheap on the free plan. Incoming messages
 * are what a Durable Object is billed for (twenty to a request), so a game is
 * about two messages a shot plus a few cue previews, not a stream of frames.
 * Outgoing messages cost nothing, so the hall tells everyone everything.
 */

/** Bumped when a change would confuse a deck built against the previous one. */
export const PROTOCOL_VERSION = 1;

/** The one hall every deck at the venue shares. */
export const HALL_NAME = "venue";

/** Sockets the hall holds at once. Far more than the venue holds guests. */
export const MAX_SOCKETS = 200;

/** Tables that may exist at once, open, in play or finished. */
export const MAX_TABLES = 64;

/** Public tables listed in the lobby. */
export const MAX_LISTED = 20;

/** The longest frame the hall reads. A settled table is well under half of it. */
export const MAX_MESSAGE_CHARS = 4096;

/** The longest settled-table state the hall stores and passes on. */
export const MAX_STATE_CHARS = 2500;

/**
 * Messages one socket may send in {@link MESSAGE_WINDOW_MS}. A player sends a
 * shot, a settled table and at most three cue previews a second, so this is a
 * ceiling a game never reaches. Past it the socket is closed: a client that
 * floods is spending the hall's request budget, not playing.
 */
export const MESSAGES_PER_WINDOW = 40;
export const MESSAGE_WINDOW_MS = 10_000;

/** Cue previews closer together than this are dropped rather than passed on. */
export const AIM_INTERVAL_MS = 250;

/** How long a seat is kept for a player whose connection dropped. */
export const RECONNECT_GRACE_MS = 60_000;

/** A table nobody has touched for this long is cleared away. */
export const IDLE_TABLE_MS = 2 * 60 * 60 * 1000;

/** Table codes: four letters, without I and O, which read as 1 and 0. */
export const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ";
export const CODE_LENGTH = 4;

/** The same names the arcade boards take: up to six capitals, digits or dashes. */
const NAME = /^[A-Z0-9-]{1,6}$/;
const CODE = new RegExp(`^[${CODE_ALPHABET}]{${CODE_LENGTH}}$`);
const TOKEN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export const isPlayerName = (value: unknown): value is string =>
	typeof value === "string" && NAME.test(value);

export const isTableCode = (value: unknown): value is string =>
	typeof value === "string" && CODE.test(value);

/** A seat token is the hall's own random UUID, never one a client made up. */
export const isSeatToken = (value: unknown): value is string =>
	typeof value === "string" && TOKEN.test(value);

export type Seat = 0 | 1;

export const isSeat = (value: unknown): value is Seat => value === 0 || value === 1;

const isFiniteNumber = (value: unknown): value is number =>
	typeof value === "number" && Number.isFinite(value);

const inRange = (value: unknown, min: number, max: number): value is number =>
	isFiniteNumber(value) && value >= min && value <= max;

/** The table is 2.54 by 1.27 in its own units; a cue ball placed in hand stays on it. */
export const TABLE_WIDTH = 2.54;
export const TABLE_HEIGHT = 1.27;

/** A cue preview: the angle, how far back the cue is drawn, and a cue ball being placed. */
export interface Aim {
	readonly a: number;
	readonly p: number;
	readonly cx?: number;
	readonly cy?: number;
}

/** A shot: aim, power, spin across and up the ball, and where a ball in hand was placed. */
export interface Shot {
	readonly a: number;
	readonly p: number;
	readonly sx: number;
	readonly sy: number;
	readonly cx?: number;
	readonly cy?: number;
}

/** Copies a cue ball position, if there is a valid one. Both or neither. */
function placement(input: Record<string, unknown>): Pick<Shot, "cx" | "cy"> | undefined {
	const { cx, cy } = input;
	if (cx === undefined && cy === undefined) {
		return {};
	}

	return inRange(cx, 0, TABLE_WIDTH) && inRange(cy, 0, TABLE_HEIGHT) ? { cx, cy } : undefined;
}

export function parseAim(input: Record<string, unknown>): Aim | undefined {
	const { a, p } = input;
	const cue = placement(input);
	if (!inRange(a, -Math.PI * 4, Math.PI * 4) || !inRange(p, 0, 1) || cue === undefined) {
		return undefined;
	}

	return { a, p, ...cue };
}

export function parseShot(input: unknown): Shot | undefined {
	if (typeof input !== "object" || input === null || Array.isArray(input)) {
		return undefined;
	}

	const fields = input as Record<string, unknown>;
	const { a, p, sx, sy } = fields;
	const cue = placement(fields);
	if (
		!inRange(a, -Math.PI * 4, Math.PI * 4) ||
		!inRange(p, 0, 1) ||
		!inRange(sx, -1, 1) ||
		!inRange(sy, -1, 1) ||
		cue === undefined
	) {
		return undefined;
	}

	return { a, p, sx, sy, ...cue };
}

/**
 * The settled table, as the shooter's deck saw it: the hall keeps it and
 * passes it on without reading inside. Only its size is checked, so the
 * decks can change what they record without a change here.
 */
export function parseState(input: unknown): unknown | undefined {
	if (typeof input !== "object" || input === null || Array.isArray(input)) {
		return undefined;
	}

	return JSON.stringify(input).length <= MAX_STATE_CHARS ? input : undefined;
}

/** Why the hall refused something, as a deck can show it. */
export type RefusalCode =
	| "bad-message"
	| "unknown-message"
	| "already-seated"
	| "not-seated"
	| "no-table"
	| "table-full"
	| "hall-full"
	| "not-your-turn"
	| "out-of-order"
	| "not-over"
	| "no-opponent";

export const REFUSALS: Readonly<Record<RefusalCode, string>> = {
	"bad-message": "That message could not be read.",
	"unknown-message": "The hall does not know that message.",
	"already-seated": "You are already at a table.",
	"not-seated": "You are not at a table.",
	"no-table": "There is no table with that code.",
	"table-full": "That table already has two players.",
	"hall-full": "Every table in the hall is taken. Try again shortly.",
	"not-your-turn": "It is not your turn.",
	"out-of-order": "That shot is out of step with the table.",
	"not-over": "The game is still being played.",
	"no-opponent": "Your opponent has left the table.",
};
