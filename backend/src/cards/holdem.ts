/**
 * Texas Hold'em, no limit, as the card hall deals it, for play money.
 *
 * Up to six at a table, each sitting down with {@link START_STACK} chips;
 * blinds are fixed at {@link SMALL_BLIND} and {@link BIG_BLIND}. Once two
 * players with chips are seated, a hand is dealt every few seconds, for as
 * long as two can play: each player gets two cards face down, and five are
 * dealt face up between them, three on the flop, one on the turn, one on the
 * river, with a round of betting before each and after the last. The best
 * five of a player's seven cards wins, and a player who has gone all in can
 * win only what they matched, so the rest forms side pots.
 *
 * Betting is no limit: a raise is at least the size of the last one, up to
 * everything a player has. An all-in raise short of that does not reopen the
 * betting to those who have already acted on this street; they may call or
 * fold. Heads up, the button posts the small blind and acts first before the
 * flop.
 *
 * As with the other card games, everything is here: the shuffle, the deck,
 * every hand, every pot. A deck is sent its own two cards and what is face
 * up; the others' cards only when they are turned over, when everyone left in
 * is all in, or at a showdown.
 *
 * A player who is away when a hand is dealt sits it out, and is folded fast
 * if they drop in the middle of one. A player who runs out sits out until
 * they buy back in. Empty seats can be filled with bots by the table's host
 * at any time; a bot plays from the next hand, on the hall's clock, a beat
 * after its turn comes, and knows only what a player in its seat would.
 */

import { bestHand, type Card } from "./poker.js";
import type { RefusalCode } from "./protocol.js";

export const HOLDEM_SEATS = 6;
export const START_STACK = 1000;
export const SMALL_BLIND = 5;
export const BIG_BLIND = 10;

/** How long a player has to act before the hall checks for them, or folds. */
export const HOLDEM_TURN_MS = 30_000;

/** How long a player who is away gets: long enough to come back from a blip, short enough not to hold a table up. */
export const AWAY_TURN_MS = 3_000;

/** Once two can play, how long before the first hand is dealt, so a third can sit down. */
export const DEAL_DELAY_MS = 3_000;

/** With the betting over and someone all in, the pause between the streets dealt out. */
export const RUNOUT_MS = 1_600;

/** How long a showdown stays up before the next hand. */
export const SHOWDOWN_MS = 6_000;

/** How long a hand everyone folded to stays up. */
export const FOLDOUT_MS = 3_000;

/** How long a bot takes over a move, so the table can follow it: somewhere between these. */
export const BOT_THINK_MS: readonly [number, number] = [1_200, 2_600];

/** Bots' names at the table, given in order to the first not already sitting there. */
export const BOT_NAMES = ["Chrome", "Vex", "Rook", "Halo", "Jinx", "Neon"] as const;

/** Bots are named like characters, on a world of their own, so a deck shows them the way it shows anyone. */
export const BOT_WORLD = "Grid";

export type HoldemPhase = "waiting" | "betting" | "runout" | "showdown";
export type Street = "preflop" | "flop" | "turn" | "river";
export type HoldemMove = "fold" | "check" | "call" | "raise";

export interface HoldemPlayer {
	readonly name: string;
	readonly token: string;
	away: number | null;
	readonly bot: boolean;
	/** Chips in front of the player, not yet bet. */
	chips: number;
	hole: Card[];
	/** Chips bet on this street. */
	bet: number;
	/** Chips put in this hand, every street, for the pots. */
	total: number;
	/** Dealt into this hand. */
	inHand: boolean;
	folded: boolean;
	allIn: boolean;
	/** Has acted since the betting was last opened on this street. */
	acted: boolean;
	/** What they last did this street, for the table to show: "CHECK", "RAISE"... */
	last: string | null;
}

/** A pot as a showdown settled it. */
export interface HoldemPot {
	readonly amount: number;
	readonly winners: number[];
	/** The hand that took it; null when only one player could claim it, such as a bet nobody could match. */
	readonly hand: string | null;
}

/** A player's cards as turned over at a showdown, and what they made. */
export interface HoldemShown {
	readonly cards: Card[];
	readonly hand: string;
	readonly five: Card[];
}

export interface HoldemResult {
	readonly pots: HoldemPot[];
	/** By seat: the cards each player still in turned over. */
	readonly shown: (HoldemShown | null)[];
	/** By seat: what each took from the pots. */
	readonly won: number[];
	/** Everyone else folded: nothing is turned over. */
	readonly uncontested: boolean;
}

export interface HoldemGame {
	phase: HoldemPhase;
	seats: (HoldemPlayer | null)[];
	/** Hands dealt at this table. */
	hands: number;
	/** Bumped on every change a move depends on, so a move made on an old view is refused. */
	seq: number;
	deadline: number | null;
	button: number | null;
	small: number | null;
	big: number | null;
	street: Street | null;
	board: Card[];
	/** The rest of the deck. Never sent. */
	deck: Card[];
	turn: number | null;
	/** The bet to match on this street. */
	current: number;
	/** How much the next raise must add, at least: the size of the last full one. */
	minRaise: number;
	/** What players who left during this hand had put in: it stays in the pots. */
	gone: number[];
	result: HoldemResult | null;
}

/** What the player on turn may do, and for how much. Raise amounts are what their bet on this street would come to. */
export interface HoldemMoves {
	readonly fold: boolean;
	readonly check: boolean;
	/** Chips a call costs; 0 when there is nothing to call. */
	readonly call: number;
	readonly raise: { readonly min: number; readonly max: number } | null;
}

export const isHoldemMove = (value: unknown): value is HoldemMove =>
	value === "fold" || value === "check" || value === "call" || value === "raise";

// ------------------------------------------------------------------ table

export function newHoldemGame(): HoldemGame {
	return {
		phase: "waiting",
		seats: Array.from({ length: HOLDEM_SEATS }, () => null),
		hands: 0,
		seq: 0,
		deadline: null,
		button: null,
		small: null,
		big: null,
		street: null,
		board: [],
		deck: [],
		turn: null,
		current: 0,
		minRaise: BIG_BLIND,
		gone: [],
		result: null,
	};
}

function player(name: string, token: string, bot: boolean): HoldemPlayer {
	return {
		name,
		token,
		away: null,
		bot,
		chips: START_STACK,
		hole: [],
		bet: 0,
		total: 0,
		inHand: false,
		folded: false,
		allIn: false,
		acted: false,
		last: null,
	};
}

/** Seats a person, with a full stack. During a hand they watch it from their seat and are dealt in from the next. */
export function seatHoldem(
	game: HoldemGame,
	seat: number,
	name: string,
	token: string,
	now: number,
): void {
	game.seats[seat] = player(name, token, false);
	game.seq += 1;
	refreshWaiting(game, now);
}

/** The table's host: the person in the lowest seat. It passes on when they leave. Null with nobody but bots. */
export function holdemHost(game: HoldemGame): number | null {
	const seat = game.seats.findIndex((sitting) => sitting !== null && !sitting.bot);
	return seat < 0 ? null : seat;
}

/** The host fills an empty seat with a bot, which plays from the next hand. */
export function addHoldemBot(game: HoldemGame, by: number, now: number): RefusalCode | null {
	if (holdemHost(game) !== by) {
		return "not-host";
	}

	const seat = game.seats.indexOf(null);
	if (seat < 0) {
		return "table-full";
	}

	const taken = new Set(game.seats.map((sitting) => sitting?.name));
	const name =
		BOT_NAMES.map((bot) => `${bot}@${BOT_WORLD}`).find((bot) => !taken.has(bot)) ??
		`Bot ${seat + 1}@${BOT_WORLD}`;
	game.seats[seat] = player(name, `bot-${seat}-${game.seq}`, true);
	game.seq += 1;
	refreshWaiting(game, now);
	return null;
}

/** The host sends a bot away; in the middle of a hand it folds and goes, as a player leaving would. */
export function removeHoldemBot(
	game: HoldemGame,
	by: number,
	seat: unknown,
	now: number,
): RefusalCode | null {
	if (holdemHost(game) !== by) {
		return "not-host";
	}
	if (typeof seat !== "number" || game.seats[seat]?.bot !== true) {
		return "no-bot";
	}

	removeHoldemPlayer(game, seat, now);
	return null;
}

/** A player who has run out buys back in, to a full stack, once they are no longer in a hand. */
export function rebuy(game: HoldemGame, seat: number, now: number): RefusalCode | null {
	const sitting = game.seats[seat];
	if (sitting === null || sitting === undefined) {
		return "not-seated";
	}
	if (sitting.chips > 0) {
		return "has-chips";
	}
	if (sitting.inHand && !sitting.folded && (game.phase === "betting" || game.phase === "runout")) {
		return "not-waiting";
	}

	sitting.chips = START_STACK;
	game.seq += 1;
	refreshWaiting(game, now);
	return null;
}

/** Seats that can be dealt in: a stack, and someone there to play it. A bot that ran out buys back in by itself. */
function dealable(game: HoldemGame): number[] {
	return game.seats.flatMap((sitting, seat) =>
		sitting !== null && sitting.away === null && (sitting.chips > 0 || sitting.bot) ? [seat] : [],
	);
}

/** Between hands, the clock to the next deal runs only while two can play. */
function refreshWaiting(game: HoldemGame, now: number): void {
	if (game.phase !== "waiting") {
		return;
	}

	const enough = dealable(game).length >= 2;
	if (enough && game.deadline === null) {
		game.deadline = now + DEAL_DELAY_MS;
		game.seq += 1;
	} else if (!enough && game.deadline !== null) {
		game.deadline = null;
		game.seq += 1;
	}
}

// ------------------------------------------------------------------ dealing

/** Deals the next hand, or waits for players if fewer than two can play. */
function startHand(game: HoldemGame, now: number): void {
	for (const sitting of game.seats) {
		if (sitting === null) {
			continue;
		}
		if (sitting.bot && sitting.chips === 0) {
			sitting.chips = START_STACK;
		}
		sitting.hole = [];
		sitting.bet = 0;
		sitting.total = 0;
		sitting.inHand = false;
		sitting.folded = false;
		sitting.allIn = false;
		sitting.acted = false;
		sitting.last = null;
	}

	game.board = [];
	game.deck = [];
	game.gone = [];
	game.result = null;
	game.turn = null;
	game.current = 0;
	game.minRaise = BIG_BLIND;
	game.seq += 1;

	const players = dealable(game);
	if (players.length < 2) {
		game.phase = "waiting";
		game.street = null;
		game.small = null;
		game.big = null;
		game.deadline = null;
		return;
	}

	game.hands += 1;
	const button =
		game.button === null
			? (players[randomBelow(players.length)] as number)
			: nextOf(players, game.button);
	game.button = button;
	if (players.length === 2) {
		game.small = button;
		game.big = nextOf(players, button);
	} else {
		game.small = nextOf(players, button);
		game.big = nextOf(players, game.small);
	}

	game.deck = shuffled();
	for (const seat of players) {
		const sitting = game.seats[seat] as HoldemPlayer;
		sitting.inHand = true;
		sitting.hole = game.deck.splice(0, 2);
	}

	game.phase = "betting";
	game.street = "preflop";
	post(game, game.small, SMALL_BLIND, "SB");
	post(game, game.big, BIG_BLIND, "BB");
	game.current = BIG_BLIND;
	game.minRaise = BIG_BLIND;
	openRound(game, game.big, now);
}

function post(game: HoldemGame, seat: number, amount: number, label: string): void {
	const sitting = game.seats[seat] as HoldemPlayer;
	put(sitting, amount);
	sitting.last = sitting.allIn ? "ALL IN" : label;
}

/** Moves chips from a player's stack to their bet, as many as they have. */
function put(sitting: HoldemPlayer, amount: number): void {
	const paid = Math.max(0, Math.min(amount, sitting.chips));
	sitting.chips -= paid;
	sitting.bet += paid;
	sitting.total += paid;
	if (sitting.chips === 0) {
		sitting.allIn = true;
	}
}

/** The next card or cards face up, one burned first. */
function dealStreet(game: HoldemGame): void {
	game.deck.shift();
	if (game.board.length === 0) {
		game.board.push(...game.deck.splice(0, 3));
		game.street = "flop";
	} else {
		game.board.push(...game.deck.splice(0, 1));
		game.street = game.board.length === 4 ? "turn" : "river";
	}
	game.seq += 1;
}

// ------------------------------------------------------------------ betting

/** Whether a player still has to act on this street: in, with chips, and either not yet acted or short of the bet. */
function needsToAct(game: HoldemGame, sitting: HoldemPlayer | null): boolean {
	return (
		sitting?.inHand === true &&
		!sitting.folded &&
		!sitting.allIn &&
		(!sitting.acted || sitting.bet < game.current)
	);
}

/** Seats still in the hand: dealt in, not folded. */
function contenders(game: HoldemGame): number[] {
	return game.seats.flatMap((sitting, seat) => (sitting?.inHand && !sitting.folded ? [seat] : []));
}

/** The first seat after `after`, going round, that has to act; `after` itself last. */
function nextToAct(game: HoldemGame, after: number): number | null {
	for (let step = 1; step <= game.seats.length; step += 1) {
		const seat = (after + step) % game.seats.length;
		if (needsToAct(game, game.seats[seat] ?? null)) {
			return seat;
		}
	}
	return null;
}

function openRound(game: HoldemGame, after: number, now: number): void {
	const next = nextToAct(game, after);
	if (next === null) {
		closeRound(game, now);
	} else {
		setTurn(game, next, now);
	}
}

function setTurn(game: HoldemGame, seat: number, now: number): void {
	const sitting = game.seats[seat] as HoldemPlayer;
	game.turn = seat;
	game.deadline =
		now +
		(sitting.bot
			? BOT_THINK_MS[0] + randomBelow(BOT_THINK_MS[1] - BOT_THINK_MS[0])
			: sitting.away !== null
				? AWAY_TURN_MS
				: HOLDEM_TURN_MS);
	game.seq += 1;
}

/** What a seat may do now. Null when it is not their turn. */
export function holdemMovesFor(game: HoldemGame, seat: number | null): HoldemMoves | null {
	if (seat === null || game.phase !== "betting" || game.turn !== seat) {
		return null;
	}

	const sitting = game.seats[seat] as HoldemPlayer;
	const owed = Math.max(0, game.current - sitting.bet);
	const most = sitting.bet + sitting.chips;
	const least = game.current === 0 ? BIG_BLIND : game.current + game.minRaise;
	// Raising needs someone left to bet against, the betting open to them, and more than a call.
	const against = contenders(game).some(
		(other) => other !== seat && !(game.seats[other] as HoldemPlayer).allIn,
	);
	return {
		fold: true,
		check: owed === 0,
		call: Math.min(owed, sitting.chips),
		raise:
			against && !sitting.acted && sitting.chips > owed
				? { min: Math.min(least, most), max: most }
				: null,
	};
}

/**
 * A move by the player on turn. A raise names what their bet on this street
 * comes to, from the least a raise may be up to all they have; all in for
 * less than the least is allowed too.
 */
export function holdemAct(
	game: HoldemGame,
	seat: number,
	move: HoldemMove,
	to: unknown,
	now: number,
): RefusalCode | null {
	const moves = holdemMovesFor(game, seat);
	if (moves === null) {
		return "not-your-turn";
	}

	const sitting = game.seats[seat] as HoldemPlayer;
	switch (move) {
		case "fold":
			sitting.folded = true;
			sitting.last = "FOLD";
			break;
		case "check":
			if (!moves.check) {
				return "cannot";
			}
			sitting.acted = true;
			sitting.last = "CHECK";
			break;
		case "call":
			if (moves.call <= 0) {
				return "cannot";
			}
			put(sitting, moves.call);
			sitting.acted = true;
			sitting.last = sitting.allIn ? "ALL IN" : "CALL";
			break;
		default: {
			const range = moves.raise;
			if (range === null) {
				return "cannot";
			}
			if (
				typeof to !== "number" ||
				!Number.isInteger(to) ||
				to > range.max ||
				(to < range.min && to !== range.max)
			) {
				return "bad-raise";
			}

			const opening = game.current === 0;
			const added = to - game.current;
			const full = opening ? to >= BIG_BLIND : added >= game.minRaise;
			put(sitting, to - sitting.bet);
			if (full) {
				game.minRaise = opening ? to : added;
				// A full raise reopens the betting to everyone else.
				for (const other of game.seats) {
					if (other !== null && other !== sitting) {
						other.acted = false;
					}
				}
			}
			game.current = Math.max(game.current, to);
			sitting.acted = true;
			sitting.last = sitting.allIn ? "ALL IN" : opening ? "BET" : "RAISE";
		}
	}

	advance(game, seat, now);
	return null;
}

/** After a move: the hand is won if everyone else is out, else the next player acts, else the street is over. */
function advance(game: HoldemGame, from: number, now: number): void {
	game.seq += 1;
	if (contenders(game).length === 1) {
		foldout(game, now);
		return;
	}

	const next = nextToAct(game, from);
	if (next !== null) {
		setTurn(game, next, now);
	} else {
		closeRound(game, now);
	}
}

/**
 * The betting on a street is done. The bets go in, and the next street is
 * dealt, or, with nobody left to bet against, the rest of the board is dealt
 * out one street at a time, or it is the showdown.
 */
function closeRound(game: HoldemGame, now: number): void {
	for (const sitting of game.seats) {
		if (sitting !== null) {
			sitting.bet = 0;
			sitting.acted = false;
		}
	}
	game.current = 0;
	game.minRaise = BIG_BLIND;
	game.turn = null;
	game.seq += 1;

	if (game.board.length === 5) {
		showdown(game, now);
		return;
	}

	const able = contenders(game).filter((seat) => !(game.seats[seat] as HoldemPlayer).allIn);
	if (able.length <= 1) {
		game.phase = "runout";
		game.deadline = now + RUNOUT_MS;
		return;
	}

	dealStreet(game);
	for (const sitting of game.seats) {
		if (sitting !== null) {
			sitting.last = sitting.folded ? "FOLD" : sitting.allIn ? "ALL IN" : null;
		}
	}
	openRound(game, game.button ?? 0, now);
}

// ------------------------------------------------------------------ settling

/**
 * The pots, from what everyone put in: a main pot all the players still in
 * share a claim to, and a side pot for each all-in level above it, which only
 * those who matched it can win. What folded players and players who left put
 * in stays in whichever pots it reached.
 */
export function buildPots(game: HoldemGame): { amount: number; eligible: number[] }[] {
	const live = contenders(game);
	const paid: number[] = [
		...game.seats.flatMap((sitting) => (sitting?.inHand ? [sitting.total] : [])),
		...game.gone,
	];
	const levels = [...new Set(live.map((seat) => (game.seats[seat] as HoldemPlayer).total))].sort(
		(a, b) => a - b,
	);
	const pots: { amount: number; eligible: number[] }[] = [];
	let below = 0;
	for (const level of levels) {
		const amount = paid.reduce(
			(sum, chips) => sum + Math.min(chips, level) - Math.min(chips, below),
			0,
		);
		if (amount > 0) {
			pots.push({
				amount,
				eligible: live.filter((seat) => (game.seats[seat] as HoldemPlayer).total >= level),
			});
		}
		below = level;
	}

	// Anything past the highest live level, from someone who put in more and is gone or folded, joins the last pot.
	const total = paid.reduce((sum, chips) => sum + chips, 0);
	const counted = pots.reduce((sum, pot) => sum + pot.amount, 0);
	const last = pots[pots.length - 1];
	if (last !== undefined && total > counted) {
		pots[pots.length - 1] = { amount: last.amount + total - counted, eligible: last.eligible };
	}
	return pots;
}

/** Everyone but one folded: they take it all, and nobody shows. */
function foldout(game: HoldemGame, now: number): void {
	const winner = contenders(game)[0] as number;
	const amount =
		game.seats.reduce((sum, sitting) => sum + (sitting?.inHand ? sitting.total : 0), 0) +
		game.gone.reduce((sum, chips) => sum + chips, 0);
	const won = game.seats.map(() => 0);
	won[winner] = amount;
	(game.seats[winner] as HoldemPlayer).chips += amount;
	finishHand(
		game,
		{
			pots: [{ amount, winners: [winner], hand: null }],
			shown: game.seats.map(() => null),
			won,
			uncontested: true,
		},
		now + FOLDOUT_MS,
	);
}

/** The cards are turned over and every pot goes to the best hand that can claim it; a tie splits it, the odd chip first left of the button. */
function showdown(game: HoldemGame, now: number): void {
	const live = contenders(game);
	const hands = new Map(
		live.map((seat) => [
			seat,
			bestHand([...(game.seats[seat] as HoldemPlayer).hole, ...game.board]),
		]),
	);
	const won = game.seats.map(() => 0);
	const pots: HoldemPot[] = [];
	for (const pot of buildPots(game)) {
		if (pot.eligible.length === 1) {
			// A pot only one player can claim, such as a bet nobody could match: theirs, with no hand to show for it.
			const only = pot.eligible[0] as number;
			won[only] = (won[only] ?? 0) + pot.amount;
			pots.push({ amount: pot.amount, winners: [only], hand: null });
			continue;
		}

		const best = Math.max(...pot.eligible.map((seat) => hands.get(seat)?.score ?? 0));
		const winners = leftOfButton(
			game,
			pot.eligible.filter((seat) => hands.get(seat)?.score === best),
		);
		const share = Math.floor(pot.amount / winners.length);
		let odd = pot.amount - share * winners.length;
		for (const seat of winners) {
			won[seat] = (won[seat] ?? 0) + share + (odd > 0 ? 1 : 0);
			odd -= 1;
		}
		pots.push({ amount: pot.amount, winners, hand: hands.get(winners[0] as number)?.name ?? null });
	}

	won.forEach((chips, seat) => {
		const sitting = game.seats[seat];
		if (sitting !== null && sitting !== undefined) {
			sitting.chips += chips;
		}
	});

	const shown = game.seats.map((_, seat) => {
		const hand = hands.get(seat);
		return hand === undefined
			? null
			: { cards: (game.seats[seat] as HoldemPlayer).hole, hand: hand.name, five: hand.five };
	});
	finishHand(game, { pots, shown, won, uncontested: false }, now + SHOWDOWN_MS);
}

function finishHand(game: HoldemGame, result: HoldemResult, until: number): void {
	for (const sitting of game.seats) {
		if (sitting !== null) {
			sitting.bet = 0;
		}
	}
	game.result = result;
	game.phase = "showdown";
	game.turn = null;
	game.current = 0;
	game.deadline = until;
	game.seq += 1;
}

/** Seats in order round the table from the one after the button. */
function leftOfButton(game: HoldemGame, seats: number[]): number[] {
	const button = game.button ?? 0;
	const size = game.seats.length;
	return [...seats].sort(
		(a, b) => ((a - button - 1 + size) % size) - ((b - button - 1 + size) % size),
	);
}

// ------------------------------------------------------------------ clock and leaving

/**
 * The clock ran out: the next hand is dealt, the next street dealt out, or
 * the player on turn moves. A bot makes its move; a person who let the clock
 * run out checks if they can and folds if not.
 */
export function holdemTimeout(game: HoldemGame, now: number): boolean {
	if (game.deadline === null || now < game.deadline) {
		return false;
	}

	switch (game.phase) {
		case "waiting":
		case "showdown":
			startHand(game, now);
			if (game.phase === "waiting") {
				refreshWaiting(game, now);
			}
			return true;
		case "runout":
			dealStreet(game);
			if (game.board.length === 5) {
				showdown(game, now);
			} else {
				game.deadline = now + RUNOUT_MS;
			}
			return true;
		default: {
			if (game.turn === null) {
				game.deadline = null;
				return false;
			}

			const seat = game.turn;
			const sitting = game.seats[seat] as HoldemPlayer;
			if (sitting.bot) {
				botMove(game, seat, now);
			} else {
				holdemAct(game, seat, holdemMovesFor(game, seat)?.check ? "check" : "fold", null, now);
			}
			return true;
		}
	}
}

/** A player's connection dropped: if it is their turn, the table waits only a moment for them; the next hand deals them out. */
export function holdemAway(game: HoldemGame, now: number): void {
	const turn = game.turn === null ? null : game.seats[game.turn];
	if (game.phase === "betting" && turn?.away != null && game.deadline !== null) {
		game.deadline = Math.min(game.deadline, now + AWAY_TURN_MS);
	}
	refreshWaiting(game, now);
}

/** A player came back: between hands, the next can be dealt with them. */
export function holdemBack(game: HoldemGame, now: number): void {
	refreshWaiting(game, now);
}

/**
 * A player has gone for good. In the middle of a hand they fold, and what
 * they put in stays in the pot; if it was their turn the next player takes
 * it.
 */
export function removeHoldemPlayer(game: HoldemGame, seat: number, now: number): void {
	const sitting = game.seats[seat];
	if (sitting === null || sitting === undefined) {
		return;
	}

	const during = game.phase === "betting" || game.phase === "runout";
	if (during && sitting.inHand) {
		game.gone.push(sitting.total);
	}
	const wasTurn = game.turn === seat;
	game.seats[seat] = null;
	game.seq += 1;

	if (!during) {
		refreshWaiting(game, now);
		return;
	}

	const left = contenders(game);
	if (left.length === 1) {
		foldout(game, now);
	} else if (left.length === 0) {
		startHand(game, now);
		refreshWaiting(game, now);
	} else if (game.phase === "betting") {
		if (wasTurn) {
			advance(game, seat, now);
		} else if (game.turn !== null && !needsToAct(game, game.seats[game.turn] ?? null)) {
			advance(game, game.turn, now);
		}
	}
}

/** Whether a hand is being played, for the lobby. */
export const holdemPlaying = (game: HoldemGame): boolean =>
	game.phase === "betting" || game.phase === "runout";

// ------------------------------------------------------------------ bots

/**
 * A bot's move, from what its seat knows: its own cards and the board. It
 * judges its chance of winning by dealing the unseen cards out at random a
 * few hundred times against as many hands as are still in, then bets with a
 * good hand, calls when the price is right, bluffs now and then when nobody
 * has bet, and otherwise checks or folds.
 */
function botMove(game: HoldemGame, seat: number, now: number): void {
	const moves = holdemMovesFor(game, seat);
	if (moves === null) {
		return;
	}

	const me = game.seats[seat] as HoldemPlayer;
	const opponents = contenders(game).length - 1;
	const chance = equity(me.hole, game.board, opponents, unseen(game, me));
	const pot =
		game.seats.reduce((sum, sitting) => sum + (sitting?.inHand ? sitting.total : 0), 0) +
		game.gone.reduce((sum, chips) => sum + chips, 0);
	const fair = 1 / (opponents + 1);
	const roll = Math.random();

	const raiseTo = (share: number): number | null => {
		const range = moves.raise;
		if (range === null) {
			return null;
		}
		const size = Math.max(game.minRaise, Math.round((pot * share) / BIG_BLIND) * BIG_BLIND);
		return Math.min(range.max, Math.max(range.min, game.current + size));
	};

	let move: HoldemMove;
	let to: number | null = null;
	if (moves.check) {
		if (moves.raise !== null && chance > fair + 0.22) {
			move = "raise";
			to = chance > 0.85 && roll < 0.25 ? moves.raise.max : raiseTo(0.5 + roll * 0.5);
		} else if (moves.raise !== null && roll < 0.1) {
			move = "raise";
			to = raiseTo(0.5);
		} else {
			move = "check";
		}
	} else {
		const price = moves.call / (pot + moves.call);
		if (moves.raise !== null && chance > fair + 0.35 && roll < 0.75) {
			move = "raise";
			to = chance > 0.85 && roll < 0.3 ? moves.raise.max : raiseTo(0.75);
		} else if (chance * 1.25 >= price || roll < 0.05) {
			move = "call";
		} else {
			move = "fold";
		}
	}

	if (holdemAct(game, seat, move, to, now) !== null) {
		holdemAct(game, seat, moves.check ? "check" : "fold", null, now);
	}
}

/** The cards a seat cannot see: the deck and everyone else's hand. */
function unseen(game: HoldemGame, me: HoldemPlayer): Card[] {
	return [
		...game.deck,
		...game.seats.flatMap((sitting) =>
			sitting !== null && sitting !== me && sitting.inHand ? sitting.hole : [],
		),
	];
}

/** A hand's chance of winning, by dealing out the unseen cards at random against `opponents` hands. */
export function equity(hole: Card[], board: Card[], opponents: number, hidden: Card[]): number {
	if (opponents <= 0) {
		return 1;
	}

	const pool = [...hidden];
	const trials = Math.max(40, Math.floor(420 / (opponents + 1)));
	const needed = 5 - board.length;
	let total = 0;
	for (let trial = 0; trial < trials; trial += 1) {
		// A partial shuffle of just the cards this trial deals.
		const draw = needed + opponents * 2;
		for (let index = 0; index < draw && index < pool.length; index += 1) {
			const pick = index + Math.floor(Math.random() * (pool.length - index));
			[pool[index], pool[pick]] = [pool[pick] as Card, pool[index] as Card];
		}

		const full = [...board, ...pool.slice(0, needed)];
		const mine = bestHand([...hole, ...full]).score;
		let ties = 0;
		let beaten = false;
		for (let other = 0; other < opponents; other += 1) {
			const theirs = bestHand([
				pool[needed + other * 2] as Card,
				pool[needed + other * 2 + 1] as Card,
				...full,
			]).score;
			if (theirs > mine) {
				beaten = true;
				break;
			}
			if (theirs === mine) {
				ties += 1;
			}
		}
		total += beaten ? 0 : 1 / (ties + 1);
	}
	return total / trials;
}

// ------------------------------------------------------------------ cards

function nextOf(seats: number[], after: number): number {
	return seats.find((seat) => seat > after) ?? (seats[0] as number);
}

/** A fresh 52-card deck, shuffled with the platform's cryptographic randomness. */
function shuffled(): Card[] {
	const deck: Card[] = [];
	for (const rank of "23456789TJQKA") {
		for (const suit of "SHDC") {
			deck.push(rank + suit);
		}
	}
	for (let index = deck.length - 1; index > 0; index -= 1) {
		const pick = randomBelow(index + 1);
		[deck[index], deck[pick]] = [deck[pick] as Card, deck[index] as Card];
	}
	return deck;
}

function randomBelow(bound: number): number {
	if (bound <= 1) {
		return 0;
	}

	const limit = Math.floor(0x1_0000_0000 / bound) * bound;
	const word = new Uint32Array(1);
	for (;;) {
		crypto.getRandomValues(word);
		const value = word[0] as number;
		if (value < limit) {
			return value % bound;
		}
	}
}

/**
 * What one seat sees: its own two cards, the board, every stack and bet, and
 * nobody else's cards until they are turned over: when everyone still in is
 * all in, or at a showdown.
 */
export function holdemView(game: HoldemGame, seat: number | null, now: number) {
	const mine = seat === null ? null : game.seats[seat];
	const tabled = game.phase === "runout";
	return {
		phase: game.phase,
		hands: game.hands,
		seq: game.seq,
		left: game.deadline === null ? null : Math.max(0, game.deadline - now),
		host: holdemHost(game),
		blinds: [SMALL_BLIND, BIG_BLIND],
		start: START_STACK,
		button: game.button,
		small: game.small,
		big: game.big,
		street: game.street,
		board: game.board,
		turn: game.turn,
		current: game.current,
		// The chips gathered in the middle; this street's bets are still in front of the players.
		pot:
			game.seats.reduce(
				(sum, sitting) => sum + (sitting?.inHand ? sitting.total - sitting.bet : 0),
				0,
			) + game.gone.reduce((sum, chips) => sum + chips, 0),
		hole: mine?.hole ?? [],
		moves: holdemMovesFor(game, seat),
		result: game.result,
		seats: game.seats.map((sitting, index) =>
			sitting === null
				? null
				: {
						name: sitting.name,
						away: sitting.away !== null,
						bot: sitting.bot,
						chips: sitting.chips,
						bet: sitting.bet,
						inHand: sitting.inHand,
						folded: sitting.folded,
						allIn: sitting.allIn,
						last: sitting.last,
						cards: sitting.inHand && !sitting.folded ? 2 : 0,
						shown:
							(tabled && sitting.inHand && !sitting.folded) || game.result?.shown[index] != null
								? sitting.hole
								: null,
					},
		),
	};
}
