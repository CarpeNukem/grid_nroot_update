import {
	addBot,
	callLiar,
	type IceGame,
	icePlaying,
	iceTimeout,
	iceView,
	newIceGame,
	playCards,
	removeBot,
	removeIcePlayer,
	seatPlayer,
	startGame,
} from "./blackice.js";
import {
	act,
	type Game as BlackjackGame,
	betsChanged,
	isMove,
	movesFor,
	newGame,
	newPlayer,
	placeBet,
	removePlayer,
	SEATS,
	timeout,
	topUp,
} from "./blackjack.js";
import {
	addHoldemBot,
	type HoldemGame,
	holdemAct,
	holdemAway,
	holdemBack,
	holdemPlaying,
	holdemStakes,
	holdemTimeout,
	holdemView,
	isHoldemMove,
	newHoldemGame,
	rebuy,
	removeHoldemBot,
	removeHoldemPlayer,
	seatHoldem,
	stakesOf,
} from "./holdem.js";
import type { CardGame, RefusalCode } from "./protocol.js";

/**
 * The games the card hall deals, each behind the same few calls, so the hall
 * itself only seats players, runs the clocks and sends each seat its view.
 * Each game's rules are a module of their own (blackjack.ts, blackice.ts, holdem.ts);
 * this is where the hall meets them.
 */

/** A seat as every game keeps one: who sits there, whether they are away, and whether they are a bot. */
export interface Sitter {
	readonly name: string;
	readonly token: string;
	away: number | null;
	readonly bot?: boolean;
}

/** The part of a game's state the hall reads for itself. */
export interface GameState {
	seats: (Sitter | null)[];
	deadline: number | null;
	seq: number;
}

export interface CardRules {
	/** The messages, besides the hall's own, that this game's players send. */
	readonly messages: readonly string[];
	/** A new table, from the `open` message that asks for it: its options, or a refusal when they are not ones the game offers. */
	create(options: Record<string, unknown>): GameState | RefusalCode;
	/** What the lobby lists about a table beyond its seats, such as Hold'em's stakes. */
	listing?(state: GameState): Record<string, unknown>;
	seat(state: GameState, seat: number, name: string, token: string, now: number): void;
	/** A player gone for good: they left, or did not come back in time. */
	remove(state: GameState, seat: number, now: number): void;
	/** A player's connection dropped: their seat is kept, but the table need not wait for them. */
	away(state: GameState, now: number): void;
	/** A player who was away is back on a new connection. */
	back?(state: GameState, now: number): void;
	/** A message from a seated player: null when it was taken, a refusal when not. */
	message(
		state: GameState,
		seat: number,
		fields: Record<string, unknown>,
		now: number,
	): RefusalCode | null;
	/** The table's clock ran out; true when that changed anything. */
	timeout(state: GameState, now: number): boolean;
	/** Whether a hand is being played, for the lobby. */
	playing(state: GameState): boolean;
	/** One seat's view of the game, without anything that seat should not see. */
	view(state: GameState, seat: number | null, now: number): Record<string, unknown>;
}

/** A move made against an old view of the table is refused rather than played twice. */
const stale = (state: GameState, fields: Record<string, unknown>): boolean =>
	fields.seq !== state.seq;

const blackjack: CardRules = {
	messages: ["bet", "act", "topup"],
	create: () => newGame(),
	seat: (state, seat, name, token) => {
		(state as BlackjackGame).seats[seat] = newPlayer(name, token);
	},
	remove: (state, seat, now) => removePlayer(state as BlackjackGame, seat, now),
	away: (state, now) => betsChanged(state as BlackjackGame, now),
	message: (state, seat, fields, now) => {
		const game = state as BlackjackGame;
		switch (fields.t) {
			case "bet":
				return placeBet(game, seat, fields.amount, now);
			case "act":
				if (!isMove(fields.move)) {
					return "bad-message";
				}
				return stale(state, fields) ? "out-of-order" : act(game, seat, fields.move, now);
			default:
				return topUp(game, seat);
		}
	},
	timeout: (state, now) => timeout(state as BlackjackGame, now),
	playing: (state) => (state as BlackjackGame).phase === "playing",
	view: (state, seat, now) => {
		// Every card face up, the dealer's hole card as null while it is down, and the shoe as a count.
		const game = state as BlackjackGame;
		return {
			phase: game.phase,
			round: game.round,
			seq: game.seq,
			left: game.deadline === null ? null : Math.max(0, game.deadline - now),
			shuffled: game.shuffled,
			shoe: game.shoe.length,
			dealer: game.hole ? [game.dealer[0] ?? null, null] : game.dealer,
			turn: game.turn,
			moves: seat === null ? [] : movesFor(game, seat),
			seats: game.seats.map((player) =>
				player === null
					? null
					: {
							name: player.name,
							away: player.away !== null,
							chips: player.chips,
							bet: player.bet,
							hands: player.hands,
						},
			),
		};
	},
};

const blackice: CardRules = {
	messages: ["start", "addbot", "removebot", "play", "call"],
	create: () => newIceGame(),
	seat: (state, seat, name, token) => seatPlayer(state as IceGame, seat, name, token),
	remove: (state, seat, now) => removeIcePlayer(state as IceGame, seat, now),
	// Nothing waits on a player who is away: their turn runs on its clock like anyone's.
	away: () => {},
	message: (state, seat, fields, now) => {
		const game = state as IceGame;
		switch (fields.t) {
			case "start":
				return startGame(game, seat, now);
			case "addbot":
				return addBot(game, seat);
			case "removebot":
				return removeBot(game, seat, fields.seat);
			case "play":
				return stale(state, fields) ? "out-of-order" : playCards(game, seat, fields.cards, now);
			default:
				return stale(state, fields) ? "out-of-order" : callLiar(game, seat, now);
		}
	},
	timeout: (state, now) => iceTimeout(state as IceGame, now),
	playing: (state) => icePlaying(state as IceGame),
	view: (state, seat, now) => iceView(state as IceGame, seat, now),
};

const holdem: CardRules = {
	messages: ["act", "rebuy", "addbot", "removebot"],
	create: (options) => {
		const stakes = holdemStakes(options.blinds, options.start);
		return stakes === null ? "bad-stakes" : newHoldemGame(stakes);
	},
	listing: (state) => {
		const stakes = stakesOf(state as HoldemGame);
		return { blinds: [stakes.small, stakes.big], start: stakes.start };
	},
	seat: (state, seat, name, token, now) => seatHoldem(state as HoldemGame, seat, name, token, now),
	remove: (state, seat, now) => removeHoldemPlayer(state as HoldemGame, seat, now),
	away: (state, now) => holdemAway(state as HoldemGame, now),
	back: (state, now) => holdemBack(state as HoldemGame, now),
	message: (state, seat, fields, now) => {
		const game = state as HoldemGame;
		switch (fields.t) {
			case "act":
				if (!isHoldemMove(fields.move)) {
					return "bad-message";
				}
				return stale(state, fields)
					? "out-of-order"
					: holdemAct(game, seat, fields.move, fields.to, now);
			case "rebuy":
				return rebuy(game, seat, now);
			case "addbot":
				return addHoldemBot(game, seat, now);
			default:
				return removeHoldemBot(game, seat, fields.seat, now);
		}
	},
	timeout: (state, now) => holdemTimeout(state as HoldemGame, now),
	playing: (state) => holdemPlaying(state as HoldemGame),
	view: (state, seat, now) => holdemView(state as HoldemGame, seat, now),
};

export const RULES: Readonly<Record<CardGame, CardRules>> = { blackjack, blackice, holdem };

/** Every message any game takes, so one meant for no game at all is told apart from one meant for another table's. */
export const GAME_MESSAGES: ReadonlySet<string> = new Set(
	Object.values(RULES).flatMap((rules) => rules.messages),
);

/** Seats at a blackjack table, for the welcome's limits. */
export { SEATS as BLACKJACK_SEATS };
