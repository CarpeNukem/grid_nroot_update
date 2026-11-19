/**
 * BLACK ICE, the Grid's liar's game, as the card hall deals it.
 *
 * Twenty cards: six queens, six kings, six aces and two jokers, which are
 * wild. Each round every player still in is dealt five and the hall names a
 * table rank. On their turn a player lays one to three cards face down and
 * claims they are all that rank, or calls LIAR on the player before them. A
 * call turns the cards over: if every one was the rank or a joker, the caller
 * was wrong; if not, the liar was caught. Whoever lost the call jacks into the
 * ICE. Every player's ICE has six slots and one of them is black ICE; each
 * jack-in tries the next slot, so the odds of flatlining climb from one in six
 * to certainty. A flatlined player is out. The last one standing wins.
 *
 * A player with no cards left is passed over. When nobody but the player on
 * turn has cards, the only thing left for them to do is call.
 *
 * As with blackjack, everything happens here: the shuffle, the deal, every
 * hand, every judgement, and where each player's black ICE sits. A deck is
 * sent its own hand and nothing else: how many cards the others hold, how many
 * were laid down, and the cards of a call once it has been made.
 *
 * Seats a human has not taken can be filled with bots, by the table's host,
 * between games. Bots play on the hall's own clock, a beat after their turn
 * comes so the table can follow, and know only what a player in their seat
 * would: their own hand and what has been laid down.
 */

import type { RefusalCode } from "./protocol.js";

export const ICE_SEATS = 4;
export const HAND_SIZE = 5;
export const ICE_SLOTS = 6;

/** How long a player has for each decision before the hall makes the plainest move for them. */
export const ICE_TURN_MS = 30_000;

/** How long a call's cards stay turned over, and the jack-in plays out, before the next deal. */
export const REVEAL_MS = 5_000;

/** How long a bot takes over a move, so the table can follow it: somewhere between these. */
export const BOT_THINK_MS: readonly [number, number] = [1_400, 2_800];

export type Rank = "Q" | "K" | "A";
const RANKS: readonly Rank[] = ["Q", "K", "A"];

/** A card: Q, K or A then a suit (S H D C), or a joker, ZR or ZB. */
export type IceCard = string;

/** Bots' names at the table. The hall gives each new bot the first one not already sitting there. */
export const BOT_NAMES = ["Glitch", "Daemon", "Wraith", "Static", "Cipher", "Nyx"] as const;

/** Bots are named like characters, on a world of their own, so a deck shows them the way it shows anyone. */
export const BOT_WORLD = "Ice";

export interface IcePlayer {
	readonly name: string;
	readonly token: string;
	away: number | null;
	readonly bot: boolean;
	hand: IceCard[];
	/** ICE slots tried so far this game. */
	used: number;
	/** Which slot, counting from 0, is black ICE. Never sent. */
	deadly: number;
	/** Flatlined, or sitting out a game they joined in the middle of. */
	out: boolean;
	/** Joined while a game was on: dealt in from the next one. */
	waiting: boolean;
	/** Cards this player laid down this round, and how many of them were true: only for the player's own judgement, never sent. */
	laid: number;
	honest: number;
}

/** The last cards laid down, which the next player may call. */
export interface IcePlay {
	readonly seat: number;
	readonly cards: IceCard[];
}

/** A call, once made: the cards turned over, and what it cost whom. */
export interface IceReveal {
	readonly caller: number;
	readonly liar: number;
	readonly cards: IceCard[];
	/** Whether every card was the table rank or a joker, so the caller was wrong. */
	readonly truthful: boolean;
	readonly victim: number;
	/** Which of the victim's slots was tried, counting from 1. */
	readonly slot: number;
	readonly flatlined: boolean;
}

export type IcePhase = "waiting" | "playing" | "reveal" | "over";

export interface IceGame {
	phase: IcePhase;
	seats: (IcePlayer | null)[];
	rank: Rank | null;
	turn: number | null;
	/** Rounds dealt this game, and games started at this table. */
	round: number;
	games: number;
	/** Bumped on every change a move depends on, so a move made on an old view is refused. */
	seq: number;
	deadline: number | null;
	last: IcePlay | null;
	/** Cards laid face down this round. */
	pile: number;
	reveal: IceReveal | null;
	winner: number | null;
	/** Who leads the next round. */
	starter: number | null;
}

export type IceMove = "play" | "call";

// ------------------------------------------------------------------ table

export function newIceGame(): IceGame {
	return {
		phase: "waiting",
		seats: Array.from({ length: ICE_SEATS }, () => null),
		rank: null,
		turn: null,
		round: 0,
		games: 0,
		seq: 0,
		deadline: null,
		last: null,
		pile: 0,
		reveal: null,
		winner: null,
		starter: null,
	};
}

function player(name: string, token: string, bot: boolean, waiting: boolean): IcePlayer {
	return {
		name,
		token,
		away: null,
		bot,
		hand: [],
		used: 0,
		deadly: 0,
		out: waiting,
		waiting,
		laid: 0,
		honest: 0,
	};
}

/** Seats a person. Between games they are in the next one; during one they watch it from their seat and are dealt in from the next. */
export function seatPlayer(game: IceGame, seat: number, name: string, token: string): void {
	const during = game.phase === "playing" || game.phase === "reveal";
	game.seats[seat] = player(name, token, false, during);
	game.seq += 1;
}

/** The table's host: the person in the lowest seat. It passes on when they leave. Null with nobody but bots. */
export function hostOf(game: IceGame): number | null {
	const seat = game.seats.findIndex((sitting) => sitting !== null && !sitting.bot);
	return seat < 0 ? null : seat;
}

const between = (game: IceGame): boolean => game.phase === "waiting" || game.phase === "over";

/** The host fills an empty seat with a bot, between games. */
export function addBot(game: IceGame, by: number): RefusalCode | null {
	if (hostOf(game) !== by) {
		return "not-host";
	}
	if (!between(game)) {
		return "not-waiting";
	}

	const seat = game.seats.indexOf(null);
	if (seat < 0) {
		return "table-full";
	}

	const taken = new Set(game.seats.map((sitting) => sitting?.name));
	const name =
		BOT_NAMES.map((bot) => `${bot}@${BOT_WORLD}`).find((bot) => !taken.has(bot)) ??
		`Bot ${seat + 1}@${BOT_WORLD}`;
	game.seats[seat] = player(name, `bot-${seat}-${game.seq}`, true, false);
	game.seq += 1;
	return null;
}

/** The host takes a bot away again, between games. */
export function removeBot(game: IceGame, by: number, seat: unknown): RefusalCode | null {
	if (hostOf(game) !== by) {
		return "not-host";
	}
	if (!between(game)) {
		return "not-waiting";
	}
	if (typeof seat !== "number" || game.seats[seat]?.bot !== true) {
		return "no-bot";
	}

	game.seats[seat] = null;
	game.seq += 1;
	return null;
}

/** The host starts a game, or the next one, once at least two are seated: everyone's ICE fresh, a black slot hidden in each. */
export function startGame(game: IceGame, by: number, now: number): RefusalCode | null {
	if (hostOf(game) !== by) {
		return "not-host";
	}
	if (!between(game)) {
		return "not-waiting";
	}

	const seated = seatsWhere(game, () => true);
	if (seated.length < 2) {
		return "too-few";
	}

	for (const seat of seated) {
		const sitting = game.seats[seat] as IcePlayer;
		sitting.out = false;
		sitting.waiting = false;
		sitting.used = 0;
		sitting.deadly = randomBelow(ICE_SLOTS);
		sitting.hand = [];
	}

	game.games += 1;
	game.round = 0;
	game.winner = null;
	game.reveal = null;
	game.starter = seated[randomBelow(seated.length)] as number;
	dealRound(game, now);
	return null;
}

/** A fresh deal: the whole deck shuffled, five cards to each player still in, a table rank, and the starter to lead. */
function dealRound(game: IceGame, now: number): void {
	const deck = shuffled();
	game.phase = "playing";
	game.round += 1;
	game.rank = RANKS[randomBelow(RANKS.length)] as Rank;
	game.last = null;
	game.pile = 0;
	game.reveal = null;
	for (const seat of living(game)) {
		const sitting = game.seats[seat] as IcePlayer;
		sitting.hand = deck.splice(0, HAND_SIZE);
		sitting.laid = 0;
		sitting.honest = 0;
	}

	const starter =
		game.starter !== null && isLiving(game, game.starter)
			? game.starter
			: (living(game)[0] as number);
	setTurn(game, starter, now);
}

// ------------------------------------------------------------------ moves

/** What the seat on turn may do: lay cards unless nobody else has any left, call unless nothing is down or it was their own. */
export function iceMovesFor(game: IceGame, seat: number | null): IceMove[] {
	if (seat === null || game.phase !== "playing" || game.turn !== seat) {
		return [];
	}

	const moves: IceMove[] = [];
	if (!mustCall(game, seat) && (game.seats[seat]?.hand.length ?? 0) > 0) {
		moves.push("play");
	}
	if (canCall(game, seat)) {
		moves.push("call");
	}

	return moves;
}

const canCall = (game: IceGame, seat: number): boolean =>
	game.last !== null && game.last.seat !== seat;

/** When nobody but this player has cards left, laying more would go unanswered: they must call. */
function mustCall(game: IceGame, seat: number): boolean {
	return (
		canCall(game, seat) &&
		!living(game).some((other) => other !== seat && (game.seats[other]?.hand.length ?? 0) > 0)
	);
}

/** Lays one to three of the player's own cards face down, by where they are in their hand. */
export function playCards(
	game: IceGame,
	seat: number,
	picks: unknown,
	now: number,
): RefusalCode | null {
	if (game.phase !== "playing" || game.turn !== seat) {
		return "not-your-turn";
	}

	const sitting = game.seats[seat] as IcePlayer;
	if (
		!Array.isArray(picks) ||
		picks.length < 1 ||
		picks.length > 3 ||
		new Set(picks).size !== picks.length ||
		!picks.every((pick) => Number.isInteger(pick) && pick >= 0 && pick < sitting.hand.length) ||
		mustCall(game, seat)
	) {
		return "bad-play";
	}

	const indices = [...(picks as number[])].sort((a, b) => b - a);
	const cards = indices.map((index) => sitting.hand.splice(index, 1)[0] as IceCard).reverse();
	sitting.laid += cards.length;
	sitting.honest += cards.filter((card) => isTrue(game, card)).length;
	game.last = { seat, cards };
	game.pile += cards.length;
	passTurn(game, seat, now);
	return null;
}

/** Calls LIAR on the last cards laid down: they are turned over, and whoever was wrong jacks in. */
export function callLiar(game: IceGame, seat: number, now: number): RefusalCode | null {
	if (game.phase !== "playing" || game.turn !== seat) {
		return "not-your-turn";
	}

	const last = game.last;
	if (last === null || last.seat === seat) {
		return "cannot-call";
	}

	const truthful = last.cards.every((card) => isTrue(game, card));
	const victim = truthful ? seat : last.seat;
	const { slot, flatlined } = jackIn(game, victim);
	game.reveal = {
		caller: seat,
		liar: last.seat,
		cards: last.cards,
		truthful,
		victim,
		slot,
		flatlined,
	};
	game.last = null;
	game.turn = null;
	game.phase = "reveal";
	game.deadline = now + REVEAL_MS;
	game.starter = isLiving(game, victim) ? victim : nextLiving(game, victim);
	game.seq += 1;
	return null;
}

/** Tries the next of a player's ICE slots. */
function jackIn(game: IceGame, seat: number): { slot: number; flatlined: boolean } {
	const sitting = game.seats[seat] as IcePlayer;
	const tried = sitting.used;
	sitting.used += 1;
	const flatlined = tried === sitting.deadly;
	if (flatlined) {
		sitting.out = true;
		sitting.hand = [];
	}

	return { slot: sitting.used, flatlined };
}

const isTrue = (game: IceGame, card: IceCard): boolean => card[0] === "Z" || card[0] === game.rank;

/**
 * After a player lays cards: the turn goes to the next player still in who
 * has cards. If nobody else has any, it goes to the next player still in,
 * whose only move is then to call.
 */
function passTurn(game: IceGame, from: number, now: number): void {
	const order = livingAfter(game, from).filter((seat) => seat !== from);
	const next = order.find((seat) => (game.seats[seat]?.hand.length ?? 0) > 0) ?? order[0];
	if (next === undefined) {
		// Nobody else is in: the game is over.
		finish(game);
		return;
	}

	setTurn(game, next, now);
}

function setTurn(game: IceGame, seat: number, now: number): void {
	game.turn = seat;
	const bot = game.seats[seat]?.bot === true;
	game.deadline =
		now + (bot ? BOT_THINK_MS[0] + randomBelow(BOT_THINK_MS[1] - BOT_THINK_MS[0]) : ICE_TURN_MS);
	game.seq += 1;
}

function finish(game: IceGame): void {
	const standing = living(game);
	game.phase = "over";
	game.winner = standing.length === 1 ? (standing[0] as number) : null;
	game.turn = null;
	game.last = null;
	game.deadline = null;
	game.seq += 1;
}

// ------------------------------------------------------------------ clock

/**
 * The clock ran out: a call's reveal ends and the next round is dealt, or the
 * game is over; or the player on turn moves. A bot makes its move; a person
 * who let the clock run out lays their first card, or calls if that is all
 * they can do.
 */
export function iceTimeout(game: IceGame, now: number): boolean {
	if (game.deadline === null || now < game.deadline) {
		return false;
	}

	if (game.phase === "reveal") {
		if (living(game).length <= 1) {
			finish(game);
		} else {
			dealRound(game, now);
		}
		return true;
	}

	if (game.phase !== "playing" || game.turn === null) {
		game.deadline = null;
		return false;
	}

	const seat = game.turn;
	const sitting = game.seats[seat] as IcePlayer;
	const moves = iceMovesFor(game, seat);
	if (moves.length === 0) {
		unstick(game, seat, now);
	} else if (sitting.bot) {
		botMove(game, seat, now);
	} else if (moves.includes("play")) {
		playCards(game, seat, [0], now);
	} else {
		callLiar(game, seat, now);
	}

	return true;
}

/**
 * A player has gone for good. Between games their seat simply empties.
 * During one they are out: their cards go with them, a play of theirs can no
 * longer be called, and if it was their turn the next player takes it.
 */
export function removeIcePlayer(game: IceGame, seat: number, now: number): void {
	if (game.seats[seat] === null || game.seats[seat] === undefined) {
		return;
	}

	const wasTurn = game.turn === seat;
	game.seats[seat] = null;
	game.seq += 1;
	if (between(game)) {
		return;
	}

	if (game.last?.seat === seat) {
		game.last = null;
	}

	if (living(game).length <= 1) {
		finish(game);
		return;
	}

	if (game.starter === seat) {
		game.starter = nextLiving(game, seat);
	}

	if (wasTurn) {
		game.turn = null;
	}
	unstick(game, seat, now);
}

/**
 * Makes sure the player on turn has something to do, after someone left took
 * the turn or the play it would have answered with them. The turn goes to the
 * next player still in who can lay a card; when nobody can lay one or call,
 * the round is dealt again.
 */
function unstick(game: IceGame, from: number, now: number): void {
	if (game.phase !== "playing") {
		return;
	}
	if (game.turn !== null && isLiving(game, game.turn) && iceMovesFor(game, game.turn).length > 0) {
		return;
	}

	const next = livingAfter(game, game.turn ?? from).find(
		(seat) => (game.seats[seat]?.hand.length ?? 0) > 0,
	);
	if (next !== undefined) {
		setTurn(game, next, now);
		if (iceMovesFor(game, next).length > 0) {
			return;
		}
	}

	game.starter = nextLiving(game, from) ?? game.starter;
	dealRound(game, now);
}

// ------------------------------------------------------------------ bots

/**
 * A bot's move, from what its seat knows. It calls a play it can prove is a
 * lie (more of the rank claimed than it cannot see), and otherwise calls with
 * a chance that grows the less likely the claim is. When it lays cards it
 * lays true ones when it has them, now and then slipping in one that is not,
 * and bluffs with one or two when it has none.
 */
function botMove(game: IceGame, seat: number, now: number): void {
	const moves = iceMovesFor(game, seat);
	if (moves.includes("call") && (!moves.includes("play") || botCalls(game, seat))) {
		callLiar(game, seat, now);
		return;
	}

	const hand = (game.seats[seat] as IcePlayer).hand;
	const truths = hand.flatMap((card, index) => (isTrue(game, card) ? [index] : []));
	const lies = hand.flatMap((card, index) => (isTrue(game, card) ? [] : [index]));
	let picks: number[];
	if (truths.length > 0) {
		// Ranks before jokers: a joker is worth keeping for a round that goes badly.
		truths.sort((a, b) => Number(hand[a]?.[0] === "Z") - Number(hand[b]?.[0] === "Z"));
		const count = Math.min(truths.length, 1 + randomBelow(3));
		picks = truths.slice(0, count);
		if (picks.length < 3 && lies.length > 0 && Math.random() < 0.18) {
			picks.push(lies[randomBelow(lies.length)] as number);
		}
	} else {
		const count = Math.min(lies.length, Math.random() < 0.7 ? 1 : 2);
		picks = shuffleIndices(lies).slice(0, count);
	}

	playCards(game, seat, picks, now);
}

function botCalls(game: IceGame, seat: number): boolean {
	const me = game.seats[seat] as IcePlayer;
	const last = game.last as IcePlay;
	const trueInHand = me.hand.filter((card) => isTrue(game, card)).length;
	// Eight cards in the deck are true this round: six of the rank and two jokers.
	const unseenTrue = 8 - trueInHand - me.honest;
	const unseen = 20 - me.hand.length - me.laid;
	const claimedByOthers = game.pile - me.laid;
	if (claimedByOthers > unseenTrue) {
		return true;
	}

	let likely = 1;
	for (let card = 0; card < last.cards.length; card += 1) {
		likely *= Math.max(0, unseenTrue - card) / Math.max(1, unseen - card);
	}

	// What was claimed before this play already used up some of the true cards.
	likely *= Math.max(
		0.15,
		1 - ((claimedByOthers - last.cards.length) / Math.max(1, unseenTrue)) * 0.5,
	);
	return Math.random() < (1 - likely) * 0.7;
}

// ------------------------------------------------------------------ seats

function seatsWhere(game: IceGame, test: (sitting: IcePlayer) => boolean): number[] {
	return game.seats.flatMap((sitting, seat) => (sitting !== null && test(sitting) ? [seat] : []));
}

/** Seats of players still in the game. */
function living(game: IceGame): number[] {
	return seatsWhere(game, (sitting) => !sitting.out);
}

const isLiving = (game: IceGame, seat: number): boolean =>
	game.seats[seat] != null && !(game.seats[seat] as IcePlayer).out;

/** Players still in, in turn order from the seat after `from`, round to `from` itself if it is still in. */
function livingAfter(game: IceGame, from: number): number[] {
	const order: number[] = [];
	for (let step = 1; step <= game.seats.length; step += 1) {
		const seat = (from + step) % game.seats.length;
		if (isLiving(game, seat)) {
			order.push(seat);
		}
	}
	return order;
}

function nextLiving(game: IceGame, from: number): number | null {
	return livingAfter(game, from).find((seat) => seat !== from) ?? null;
}

/** Whether a hand is being played: a deal or a reveal. */
export const icePlaying = (game: IceGame): boolean =>
	game.phase === "playing" || game.phase === "reveal";

// ------------------------------------------------------------------ cards

/** The deck: six of each rank across the suits, and the two jokers, shuffled with the platform's cryptographic randomness. */
function shuffled(): IceCard[] {
	const suits = ["S", "H", "D", "C", "S", "H"];
	const deck: IceCard[] = [];
	for (const rank of RANKS) {
		for (const suit of suits) {
			deck.push(rank + suit);
		}
	}
	deck.push("ZR", "ZB");
	for (let index = deck.length - 1; index > 0; index -= 1) {
		const pick = randomBelow(index + 1);
		[deck[index], deck[pick]] = [deck[pick] as IceCard, deck[index] as IceCard];
	}
	return deck;
}

function shuffleIndices(indices: number[]): number[] {
	const copy = [...indices];
	for (let index = copy.length - 1; index > 0; index -= 1) {
		const pick = randomBelow(index + 1);
		[copy[index], copy[pick]] = [copy[pick] as number, copy[index] as number];
	}
	return copy;
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
 * What one seat sees: its own hand and nothing of anyone else's but how many
 * cards they hold, how many slots of ICE they have tried, and whether they are
 * still in. Nobody's black slot, and no laid card until a call turns it over.
 */
export function iceView(game: IceGame, seat: number | null, now: number) {
	const mine = seat === null ? null : game.seats[seat];
	return {
		phase: game.phase,
		round: game.round,
		games: game.games,
		seq: game.seq,
		left: game.deadline === null ? null : Math.max(0, game.deadline - now),
		host: hostOf(game),
		rank: game.rank,
		turn: game.turn,
		pile: game.pile,
		last: game.last === null ? null : { seat: game.last.seat, count: game.last.cards.length },
		reveal: game.reveal,
		winner: game.winner,
		hand: mine?.hand ?? [],
		moves: iceMovesFor(game, seat),
		slots: ICE_SLOTS,
		seats: game.seats.map((sitting) =>
			sitting === null
				? null
				: {
						name: sitting.name,
						away: sitting.away !== null,
						bot: sitting.bot,
						cards: sitting.hand.length,
						used: sitting.used,
						out: sitting.out,
						waiting: sitting.waiting,
					},
		),
	};
}
