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
 * Outgoing messages cost nothing, so the hall tells everyone everything, and
 * anyone watching a table is sent what the opponent is.
 */

/**
 * Bumped when a change would confuse a deck built against the previous one.
 * A deck refuses to play when the version in `welcome` is not its own, and
 * asks to be updated. Version 2 added 9-ball, called pockets, watching and
 * tables without aim assist. Version 3 added free pyramid, and with it the
 * ball a shot strikes, which in that game is any ball.
 *
 * A deck says its version when it connects (`v=3`); one that says nothing is
 * on version 2, from before decks did. The hall speaks each deck's own
 * version back, down to {@link OLDEST_PROTOCOL}, so a deck from before free
 * pyramid keeps playing 8-ball and 9-ball: it is never shown a pyramid table,
 * and is told to update if it tries one's code.
 */
export const PROTOCOL_VERSION = 3;

/** The oldest version the hall still speaks. A deck older than that is welcomed with it, sees a version not its own and asks to be updated. */
export const OLDEST_PROTOCOL = 2;

/** The version a deck asked for with `v`, as the hall will speak it: its own, or the hall's newest if it is newer still. */
export function spokenVersion(asked: string | null): number {
	const version = asked !== null && /^[0-9]{1,4}$/.test(asked) ? Number(asked) : OLDEST_PROTOCOL;
	return Math.min(Math.max(version, OLDEST_PROTOCOL), PROTOCOL_VERSION);
}

/** The one hall every deck at the venue shares. */
export const HALL_NAME = "venue";

/** Sockets the hall holds at once. Far more than the venue holds guests. */
export const MAX_SOCKETS = 200;

/** Tables that may exist at once, open, in play or finished. */
export const MAX_TABLES = 64;

/** Public tables listed in the lobby. */
export const MAX_LISTED = 20;

/**
 * Decks watching one table at once. Every shot is sent to each of them, so
 * this keeps a popular game from filling the hall. They count toward
 * {@link MAX_SOCKETS} like any other socket.
 */
export const MAX_WATCHERS = 8;

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

/**
 * A character as the game names one, with its home world: "Forename
 * Surname@World". Each name is 2 to 15 letters, apostrophes or hyphens,
 * capitalised, as the game makes them; a world is one capitalised word.
 * Decks seat players at the tables as the character they are logged in as.
 */
const CHARACTER = /^[A-Z][A-Za-z'-]{1,14} [A-Z][A-Za-z'-]{1,14}@[A-Z][A-Za-z]{1,15}$/;

/**
 * What decks sent before they sent characters: the arcade boards' initials,
 * up to six capitals, digits or dashes. The pool hall still takes them, so a
 * deck from before the change keeps playing until it is updated.
 */
const INITIALS = /^[A-Z0-9-]{1,6}$/;
const CODE = new RegExp(`^[${CODE_ALPHABET}]{${CODE_LENGTH}}$`);
const TOKEN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export const isCharacterName = (value: unknown): value is string =>
	typeof value === "string" && CHARACTER.test(value);

/** Who may sit at a pool table: a character, or initials from an older deck. */
export const isPlayerName = (value: unknown): value is string =>
	typeof value === "string" && (CHARACTER.test(value) || INITIALS.test(value));

export const isTableCode = (value: unknown): value is string =>
	typeof value === "string" && CODE.test(value);

/** A seat token is the hall's own random UUID, never one a client made up. */
export const isSeatToken = (value: unknown): value is string =>
	typeof value === "string" && TOKEN.test(value);

export type Seat = 0 | 1;

export const isSeat = (value: unknown): value is Seat => value === 0 || value === 1;

/**
 * The game a table plays: 8-ball, 9-ball or free pyramid, which has its own
 * twelve-foot table. It is chosen when the table is opened and kept for the
 * table's whole life, rematches included. A deck that names none is opening
 * an 8-ball table, which is all there was before.
 */
export type Rules = "eight" | "nine" | "pyramid";

export const isRules = (value: unknown): value is Rules =>
	value === "eight" || value === "nine" || value === "pyramid";

/** Whether a deck speaking `version` knows a game: free pyramid came with version 3. */
export const knowsRules = (version: number, rules: Rules): boolean =>
	rules !== "pyramid" || version >= 3;

const isFiniteNumber = (value: unknown): value is number =>
	typeof value === "number" && Number.isFinite(value);

const inRange = (value: unknown, min: number, max: number): value is number =>
	isFiniteNumber(value) && value >= min && value <= max;

/** The table is 2.54 by 1.27 in its own units; a cue ball placed in hand stays on it. */
export const TABLE_WIDTH = 2.54;
export const TABLE_HEIGHT = 1.27;

/** The table's six pockets, which the decks number from 0. */
export const POCKETS = 6;

/** The balls on a table, numbered from 0: the cue ball, or free pyramid's coloured ball, and fifteen more. */
export const BALLS = 16;

/** A cue preview: the angle, how far back the cue is drawn, a cue ball being placed, the pocket called, and the ball the cue is behind. */
export interface Aim {
	readonly a: number;
	readonly p: number;
	readonly cx?: number;
	readonly cy?: number;
	readonly k?: number;
	readonly s?: number;
}

/**
 * A shot: aim, power, spin across and up the ball, where a ball in hand was
 * placed, the pocket called, and the ball struck. A shot that names no ball
 * strikes the cue ball; only free pyramid strikes any other.
 */
export interface Shot {
	readonly a: number;
	readonly p: number;
	readonly sx: number;
	readonly sy: number;
	readonly cx?: number;
	readonly cy?: number;
	readonly k?: number;
	readonly s?: number;
}

/** Copies a cue ball position, if there is a valid one. Both or neither. */
function placement(input: Record<string, unknown>): Pick<Shot, "cx" | "cy"> | undefined {
	const { cx, cy } = input;
	if (cx === undefined && cy === undefined) {
		return {};
	}

	return inRange(cx, 0, TABLE_WIDTH) && inRange(cy, 0, TABLE_HEIGHT) ? { cx, cy } : undefined;
}

/** Copies a called pocket, if there is a valid one. A shot that calls none leaves it out. */
function calledPocket(input: Record<string, unknown>): Pick<Shot, "k"> | undefined {
	const { k } = input;
	if (k === undefined) {
		return {};
	}

	return inRange(k, 0, POCKETS - 1) && Number.isInteger(k) ? { k } : undefined;
}

/** Copies the ball struck, if there is a valid one. A shot at the cue ball leaves it out. */
function struckBall(input: Record<string, unknown>): Pick<Shot, "s"> | undefined {
	const { s } = input;
	if (s === undefined) {
		return {};
	}

	return inRange(s, 0, BALLS - 1) && Number.isInteger(s) ? { s } : undefined;
}

export function parseAim(input: Record<string, unknown>): Aim | undefined {
	const { a, p } = input;
	const cue = placement(input);
	const pocket = calledPocket(input);
	const ball = struckBall(input);
	if (
		!inRange(a, -Math.PI * 4, Math.PI * 4) ||
		!inRange(p, 0, 1) ||
		cue === undefined ||
		pocket === undefined ||
		ball === undefined
	) {
		return undefined;
	}

	return { a, p, ...cue, ...pocket, ...ball };
}

export function parseShot(input: unknown): Shot | undefined {
	if (typeof input !== "object" || input === null || Array.isArray(input)) {
		return undefined;
	}

	const fields = input as Record<string, unknown>;
	const { a, p, sx, sy } = fields;
	const cue = placement(fields);
	const pocket = calledPocket(fields);
	const ball = struckBall(fields);
	if (
		!inRange(a, -Math.PI * 4, Math.PI * 4) ||
		!inRange(p, 0, 1) ||
		!inRange(sx, -1, 1) ||
		!inRange(sy, -1, 1) ||
		cue === undefined ||
		pocket === undefined ||
		ball === undefined
	) {
		return undefined;
	}

	return { a, p, sx, sy, ...cue, ...pocket, ...ball };
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
	| "no-room"
	| "hall-full"
	| "not-your-turn"
	| "out-of-order"
	| "not-over"
	| "no-opponent"
	| "needs-update";

export const REFUSALS: Readonly<Record<RefusalCode, string>> = {
	"bad-message": "That message could not be read.",
	"unknown-message": "The hall does not know that message.",
	"already-seated": "You are already at a table.",
	"not-seated": "You are not at a table.",
	"no-table": "There is no table with that code.",
	"table-full": "That table already has two players.",
	"no-room": "That table has as many watchers as it can take.",
	"hall-full": "Every table in the hall is taken. Try again shortly.",
	"not-your-turn": "It is not your turn.",
	"out-of-order": "That shot is out of step with the table.",
	"not-over": "The game is still being played.",
	"no-opponent": "Your opponent has left the table.",
	"needs-update": "That table plays free pyramid. Update the deck to play it.",
};
