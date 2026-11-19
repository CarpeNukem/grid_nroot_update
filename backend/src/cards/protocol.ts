/**
 * The card hall's wire protocol: what a deck and the hall say to each other
 * over one WebSocket.
 *
 * Every frame is a JSON object with a `t` field naming it, as in the pool
 * hall, and the lobby, table codes, seat tokens and names work the same way.
 * What differs is who plays. At the pool table each deck plays every shot
 * itself; at a card table the hall deals, so a deck only ever says what its
 * player chose (a bet, hit, stand, double, split) and is sent the table back
 * with the cards that are face up. The shoe and the dealer's hole card stay
 * in the hall.
 *
 * Every incoming message is billed, so a round costs a player a bet and a
 * move or two. Outgoing messages cost nothing, so the hall sends each seat
 * the whole table after every change.
 */

/**
 * Who may sit at a card table: a character, "Forename Surname@World", as the
 * deck reads it from the game. The card hall is newer than initials, so no
 * deck ever sent it any.
 */
export {
	CODE_ALPHABET,
	CODE_LENGTH,
	isCharacterName as isPlayerName,
	isSeatToken,
	isTableCode,
} from "../pool/protocol.js";

/**
 * Bumped when a change would confuse a deck built against the previous one.
 * A deck refuses to play when the version in `welcome` is not its own.
 */
export const PROTOCOL_VERSION = 1;

/** The one card hall every deck at the venue shares. */
export const HALL_NAME = "venue";

/** The games the hall deals: blackjack, BLACK ICE, the liar's game, and Texas Hold'em. A table plays one for its whole life. */
export type CardGame = "blackjack" | "blackice" | "holdem";

export const isCardGame = (value: unknown): value is CardGame =>
	value === "blackjack" || value === "blackice" || value === "holdem";

/** Sockets the hall holds at once. Far more than the venue holds guests. */
export const MAX_SOCKETS = 200;

/** Tables that may exist at once. */
export const MAX_TABLES = 64;

/** Public tables listed in the lobby. */
export const MAX_LISTED = 20;

/** The longest frame the hall reads. A deck's longest is a bet, well under this. */
export const MAX_MESSAGE_CHARS = 512;

/**
 * Messages one socket may send in {@link MESSAGE_WINDOW_MS}. A player alone at
 * a table can play a round in a couple of seconds, a bet and a few moves, so
 * a fast one sends around twenty in ten seconds; this is three times that.
 * Past it the socket is closed, and whatever it sent next is lost, so it has
 * to sit well clear of real play.
 */
export const MESSAGES_PER_WINDOW = 60;
export const MESSAGE_WINDOW_MS = 10_000;

/** How long a seat is kept for a player whose connection dropped. */
export const RECONNECT_GRACE_MS = 60_000;

/** A table nobody has touched for this long is cleared away. */
export const IDLE_TABLE_MS = 2 * 60 * 60 * 1000;

/** Why the hall refused something, as a deck can show it. */
export type RefusalCode =
	| "bad-message"
	| "unknown-message"
	| "already-seated"
	| "not-seated"
	| "no-table"
	| "table-full"
	| "hall-full"
	| "not-betting"
	| "bad-bet"
	| "not-your-turn"
	| "out-of-order"
	| "cannot"
	| "has-chips"
	| "not-host"
	| "not-waiting"
	| "too-few"
	| "bad-play"
	| "cannot-call"
	| "no-bot"
	| "bad-raise";

export const REFUSALS: Readonly<Record<RefusalCode, string>> = {
	"bad-message": "That message could not be read.",
	"unknown-message": "The hall does not know that message.",
	"already-seated": "You are already at a table.",
	"not-seated": "You are not at a table.",
	"no-table": "There is no table with that code.",
	"table-full": "Every seat at that table is taken.",
	"hall-full": "Every table in the hall is taken. Try again shortly.",
	"not-betting": "Bets are closed until this hand is over.",
	"bad-bet": "Bets are 10 to 500, in tens, and no more than your chips.",
	"not-your-turn": "It is not your turn.",
	"out-of-order": "The table moved on before that arrived.",
	cannot: "That hand cannot do that.",
	"has-chips": "Chips are topped up only once you run out.",
	"not-host": "Only the table's host can do that.",
	"not-waiting": "That has to wait until the game is over.",
	"too-few": "A game needs at least two at the table.",
	"bad-play": "Lay one to three of your own cards.",
	"cannot-call": "There is nothing to call.",
	"no-bot": "There is no bot in that seat.",
	"bad-raise": "Raise to at least the least raise, and no more than you have.",
};
