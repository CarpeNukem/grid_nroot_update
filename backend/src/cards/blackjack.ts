/**
 * Blackjack, as the card hall deals it.
 *
 * Everything that decides a hand happens here, on the hall: the shuffle, the
 * deal, every move, the dealer's play and the payouts. A deck is sent the
 * cards that are face up and nothing else, so the shoe's order and the
 * dealer's hole card never reach a machine at the table. That is the whole
 * difference from pool, where both decks play every shot themselves: there is
 * nothing hidden on a pool table, and here there is.
 *
 * The rules are a common casino table's: six decks, dealer stands on all 17s
 * and peeks for blackjack under an ace or a ten, blackjack pays 3 to 2, double
 * on any first two cards and after a split, split to four hands, split aces
 * take one card each. No insurance and no surrender. Chips are play money:
 * every player sits down with {@link START_CHIPS} and can top up once they run
 * out, and nothing at the table is worth anything off it.
 *
 * These are plain functions over a {@link Game}, with the clock passed in, so
 * the tests can stack a shoe and play a round without a socket.
 */

/** Seats at one table. Each plays the dealer alone, so a seat empty or sitting out changes nobody else's hand. */
export const SEATS = 5;

export const DECKS = 6;

/** A fresh shoe is shuffled before a round once fewer cards than this are left: about a quarter of it. */
export const CUT_CARDS = 78;

export const MIN_BET = 10;
export const MAX_BET = 500;

/** Bets go in tens, so a blackjack's 3 to 2 is always a whole number of chips. */
export const BET_STEP = 10;

export const START_CHIPS = 1000;

/** Hands one seat can hold in a round, by splitting. */
export const MAX_HANDS = 4;

/**
 * Once someone has bet, how long the others have to. A table where every
 * seated player has bet deals at once, so a player alone never waits.
 */
export const BET_WINDOW_MS = 15_000;

/** How long a player has for each decision before their hand stands. */
export const TURN_MS = 30_000;

/** A card: rank (A 2-9 T J Q K) then suit (S H D C), e.g. "AS", "TD". */
export type Card = string;

const RANKS = "A23456789TJQK";
const SUITS = "SHDC";

export type Move = "hit" | "stand" | "double" | "split";

export const isMove = (value: unknown): value is Move =>
	value === "hit" || value === "stand" || value === "double" || value === "split";

/** How a hand finished. A bust is a loss, kept apart so a deck can say which. */
export type Result = "blackjack" | "win" | "push" | "lose" | "bust";

export interface Hand {
	cards: Card[];
	/** The stake on this hand, doubled included. */
	bet: number;
	doubled: boolean;
	/** Made by splitting: then 21 in two cards is 21, not blackjack. */
	split: boolean;
	/** No more cards for this hand: stood, doubled, bust, 21, or split aces. */
	done: boolean;
	result: Result | null;
	/** Chips handed back when the round settled, stake included: 0 for a loss. */
	paid: number;
}

export interface Player {
	readonly name: string;
	readonly token: string;
	/** When their connection dropped, while the seat is being kept for them. */
	away: number | null;
	/** Chips in front of them, not counting {@link bet}. */
	chips: number;
	/** The stake on the next round, already taken from chips; 0 for none. */
	bet: number;
	/** This round's hands, or the last round's until the next deal, so its results stay on the table. */
	hands: Hand[];
}

export type Phase = "betting" | "playing";

export interface Turn {
	readonly seat: number;
	readonly hand: number;
}

/** The part of a table this file plays: who sits where, the cards and the clock. */
export interface Game {
	phase: Phase;
	seats: (Player | null)[];
	/** The dealer's cards. While {@link hole} is set the second is face down and is never sent. */
	dealer: Card[];
	hole: boolean;
	turn: Turn | null;
	/** Rounds dealt at this table. */
	round: number;
	/** Bumped on every change a move depends on, so a move made on an old view of the table is refused. */
	seq: number;
	/** When the bets close, or the player on turn stands: null when nothing is waiting on the clock. */
	deadline: number | null;
	/** Cards still to come, the next one first. Never sent. */
	shoe: Card[];
	/** Whether the shoe was shuffled for the round now on the table. */
	shuffled: boolean;
}

/** Why a bet or a move was refused. */
export type Refusal = "not-betting" | "bad-bet" | "not-your-turn" | "cannot" | "has-chips";

// ------------------------------------------------------------------ cards

export const isCard = (value: unknown): value is Card =>
	typeof value === "string" &&
	value.length === 2 &&
	RANKS.includes(value[0] as string) &&
	SUITS.includes(value[1] as string);

/** A card's points, an ace counting 1. */
export function pointsOf(card: Card): number {
	const rank = card[0] as string;
	if (rank === "A") {
		return 1;
	}

	return "TJQK".includes(rank) ? 10 : Number(rank);
}

/** A hand's best total, and whether an ace in it is counting 11. */
export function totalOf(cards: readonly Card[]): { total: number; soft: boolean } {
	let total = 0;
	let aces = false;
	for (const card of cards) {
		const points = pointsOf(card);
		total += points;
		aces ||= points === 1;
	}

	const soft = aces && total + 10 <= 21;
	return { total: soft ? total + 10 : total, soft };
}

const isNatural = (hand: Pick<Hand, "cards" | "split">): boolean =>
	!hand.split && hand.cards.length === 2 && totalOf(hand.cards).total === 21;

const dealerNatural = (game: Game): boolean =>
	game.dealer.length === 2 && totalOf(game.dealer).total === 21;

/** Six decks, shuffled with the platform's cryptographic randomness. */
export function freshShoe(): Card[] {
	const shoe: Card[] = [];
	for (let deck = 0; deck < DECKS; deck += 1) {
		for (const suit of SUITS) {
			for (const rank of RANKS) {
				shoe.push(rank + suit);
			}
		}
	}

	// Fisher-Yates, each index drawn without modulo bias.
	for (let index = shoe.length - 1; index > 0; index -= 1) {
		const pick = randomBelow(index + 1);
		[shoe[index], shoe[pick]] = [shoe[pick] as Card, shoe[index] as Card];
	}

	return shoe;
}

function randomBelow(bound: number): number {
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

function draw(game: Game): Card {
	if (game.shoe.length === 0) {
		// Five seats of four hands never get through a shoe past the cut, but the shoe never runs dry either way.
		game.shoe = freshShoe();
	}

	return game.shoe.shift() as Card;
}

// ------------------------------------------------------------------ table

export function newPlayer(name: string, token: string): Player {
	return { name, token, away: null, chips: START_CHIPS, bet: 0, hands: [] };
}

export function newGame(): Game {
	return {
		phase: "betting",
		seats: Array.from({ length: SEATS }, () => null),
		dealer: [],
		hole: false,
		turn: null,
		round: 0,
		seq: 0,
		deadline: null,
		shoe: freshShoe(),
		shuffled: true,
	};
}

/** The moves the hand on turn can make, which is also what a deck offers its player. */
export function movesFor(game: Game, seat: number): Move[] {
	const turn = game.turn;
	if (game.phase !== "playing" || turn === null || turn.seat !== seat) {
		return [];
	}

	const player = game.seats[seat];
	const hand = player?.hands[turn.hand];
	if (player === null || player === undefined || hand === undefined || hand.done) {
		return [];
	}

	const moves: Move[] = ["hit", "stand"];
	if (hand.cards.length === 2 && player.chips >= hand.bet) {
		moves.push("double");
		const [first, second] = hand.cards as [Card, Card];
		if (player.hands.length < MAX_HANDS && pointsOf(first) === pointsOf(second)) {
			moves.push("split");
		}
	}

	return moves;
}

/**
 * Places, changes or takes back a seat's stake for the next round: 0 takes it
 * back. Deals at once when that leaves every player at the table with a bet,
 * and otherwise starts the clock for the rest.
 */
export function placeBet(game: Game, seat: number, amount: unknown, now: number): Refusal | null {
	const player = game.seats[seat];
	if (player === null || player === undefined || game.phase !== "betting") {
		return "not-betting";
	}

	const available = player.chips + player.bet;
	if (
		typeof amount !== "number" ||
		!Number.isInteger(amount) ||
		(amount !== 0 && (amount < MIN_BET || amount > MAX_BET || amount % BET_STEP !== 0)) ||
		amount > available
	) {
		return "bad-bet";
	}

	player.chips = available - amount;
	player.bet = amount;
	betsChanged(game, now);
	return null;
}

/** Sets a broke player back to the starting stack. Only between rounds, and only once they cannot cover the smallest bet. */
export function topUp(game: Game, seat: number): Refusal | null {
	const player = game.seats[seat];
	if (player === null || player === undefined || game.phase !== "betting") {
		return "not-betting";
	}

	if (player.chips + player.bet >= MIN_BET) {
		return "has-chips";
	}

	player.chips = START_CHIPS;
	player.bet = 0;
	game.seq += 1;
	return null;
}

/**
 * After a bet, a seat filling or a seat emptying: deals if everyone here has
 * bet, keeps the clock running while some have, and stops it when none have.
 * A player who is away does not hold the table up, but a bet they placed
 * before they went still plays.
 */
export function betsChanged(game: Game, now: number): void {
	if (game.phase !== "betting") {
		return;
	}

	const seated = game.seats.filter((player): player is Player => player !== null);
	const betting = seated.filter((player) => player.bet > 0);
	if (betting.length === 0) {
		game.deadline = null;
		return;
	}

	if (seated.every((player) => player.bet > 0 || player.away !== null)) {
		deal(game, now);
		return;
	}

	game.deadline ??= now + BET_WINDOW_MS;
}

/** Deals a round to every seat with a bet: a card each and one up for the dealer, then a card each and the hole card. */
export function deal(game: Game, now: number): void {
	// A table's first round is dealt from the shoe it was opened with, which is just as fresh.
	game.shuffled = game.round === 0 || game.shoe.length < CUT_CARDS;
	if (game.shoe.length < CUT_CARDS) {
		game.shoe = freshShoe();
	}

	const playing: Player[] = [];
	for (const player of game.seats) {
		if (player === null) {
			continue;
		}

		player.hands = [];
		if (player.bet > 0) {
			player.hands.push({
				cards: [],
				bet: player.bet,
				doubled: false,
				split: false,
				done: false,
				result: null,
				paid: 0,
			});
			player.bet = 0;
			playing.push(player);
		}
	}

	game.dealer = [];
	for (let pass = 0; pass < 2; pass += 1) {
		for (const player of playing) {
			(player.hands[0] as Hand).cards.push(draw(game));
		}
		game.dealer.push(draw(game));
	}

	game.hole = true;
	game.phase = "playing";
	game.round += 1;
	game.seq += 1;
	game.turn = null;

	// The dealer peeks under an ace or a ten, the only up cards a blackjack can have. One there ends the round before anyone moves.
	if (dealerNatural(game)) {
		settle(game);
		return;
	}

	for (const player of playing) {
		const hand = player.hands[0] as Hand;
		hand.done = totalOf(hand.cards).total === 21;
	}

	nextTurn(game, null, now);
}

/** A move by the player on turn, on the hand on turn. `seq` is the table's, as the deck last saw it. */
export function act(game: Game, seat: number, move: Move, now: number): Refusal | null {
	const turn = game.turn;
	if (game.phase !== "playing" || turn === null || turn.seat !== seat) {
		return "not-your-turn";
	}

	if (!movesFor(game, seat).includes(move)) {
		return "cannot";
	}

	const player = game.seats[seat] as Player;
	const hand = player.hands[turn.hand] as Hand;
	switch (move) {
		case "hit":
			hand.cards.push(draw(game));
			hand.done = totalOf(hand.cards).total >= 21;
			break;
		case "stand":
			hand.done = true;
			break;
		case "double":
			player.chips -= hand.bet;
			hand.bet *= 2;
			hand.doubled = true;
			hand.cards.push(draw(game));
			hand.done = true;
			break;
		case "split": {
			player.chips -= hand.bet;
			const [first, second] = hand.cards as [Card, Card];
			const aces = pointsOf(first) === 1;
			const made = (card: Card): Hand => {
				const cards = [card, draw(game)];
				return {
					cards,
					bet: hand.bet,
					doubled: false,
					split: true,
					// Split aces take one card each and no more; any split hand on 21 has nothing to play for.
					done: aces || totalOf(cards).total === 21,
					result: null,
					paid: 0,
				};
			};
			player.hands.splice(turn.hand, 1, made(first), made(second));
			break;
		}
	}

	game.seq += 1;
	nextTurn(game, turn, now);
	return null;
}

/** The clock ran out: the bets close, or the hand on turn stands. */
export function timeout(game: Game, now: number): boolean {
	if (game.deadline === null || now < game.deadline) {
		return false;
	}

	if (game.phase === "betting") {
		game.deadline = null;
		if (game.seats.some((player) => player !== null && player.bet > 0)) {
			deal(game, now);
		}
		return true;
	}

	const turn = game.turn;
	const hand = turn === null ? undefined : game.seats[turn.seat]?.hands[turn.hand];
	if (hand !== undefined) {
		hand.done = true;
	}

	game.seq += 1;
	nextTurn(game, turn, now);
	return true;
}

/**
 * A player has gone for good, by leaving or by not coming back in time. Their
 * stake goes with them; if it was their turn, play moves on.
 */
export function removePlayer(game: Game, seat: number, now: number): void {
	const leaving = game.seats[seat];
	if (leaving === null || leaving === undefined) {
		return;
	}

	game.seats[seat] = null;
	if (game.phase === "betting") {
		betsChanged(game, now);
		return;
	}

	// Someone else on turn still has a hand to play, so only the leaver's own turn moves play on.
	game.seq += 1;
	if (game.turn?.seat === seat) {
		nextTurn(game, { seat, hand: Number.MAX_SAFE_INTEGER }, now);
	}
}

/**
 * Finds the next hand to play after `after`, or the first one when null, and
 * starts its clock. When there is none, the dealer plays and the round settles.
 */
function nextTurn(game: Game, after: Turn | null, now: number): void {
	for (let seat = after?.seat ?? 0; seat < game.seats.length; seat += 1) {
		const hands = game.seats[seat]?.hands ?? [];
		const from = after !== null && seat === after.seat ? after.hand : 0;
		for (let hand = Math.max(0, from); hand < hands.length; hand += 1) {
			if (!(hands[hand] as Hand).done) {
				game.turn = { seat, hand };
				game.deadline = now + TURN_MS;
				return;
			}
		}
	}

	dealerPlays(game);
	settle(game);
}

/** The dealer turns the hole card and draws to 17, unless no hand is left for that to matter to. */
function dealerPlays(game: Game): void {
	game.hole = false;
	const live = game.seats.some((player) =>
		player?.hands.some((hand) => totalOf(hand.cards).total <= 21 && !isNatural(hand)),
	);
	if (!live) {
		return;
	}

	while (totalOf(game.dealer).total < 17) {
		game.dealer.push(draw(game));
	}
}

/** Pays every hand and returns the table to betting. The hands stay up, with their results, until the next deal. */
function settle(game: Game): void {
	game.hole = false;
	const dealer = totalOf(game.dealer).total;
	const dealerBlackjack = dealerNatural(game);
	for (const player of game.seats) {
		for (const hand of player?.hands ?? []) {
			const total = totalOf(hand.cards).total;
			const natural = isNatural(hand);
			let result: Result;
			if (dealerBlackjack) {
				result = natural ? "push" : "lose";
			} else if (natural) {
				result = "blackjack";
			} else if (total > 21) {
				result = "bust";
			} else if (dealer > 21 || total > dealer) {
				result = "win";
			} else {
				result = total === dealer ? "push" : "lose";
			}

			hand.done = true;
			hand.result = result;
			hand.paid =
				result === "blackjack"
					? hand.bet + (hand.bet * 3) / 2
					: result === "win"
						? hand.bet * 2
						: result === "push"
							? hand.bet
							: 0;
			(player as Player).chips += hand.paid;
		}
	}

	game.phase = "betting";
	game.turn = null;
	game.deadline = null;
	game.seq += 1;
}
