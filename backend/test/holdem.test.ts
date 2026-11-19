import { describe, expect, it } from "vitest";
import {
	AWAY_TURN_MS,
	addHoldemBot,
	BIG_BLIND,
	buildPots,
	DEAL_DELAY_MS,
	equity,
	FOLDOUT_MS,
	HOLDEM_SEATS,
	HOLDEM_TURN_MS,
	type HoldemGame,
	type HoldemMove,
	holdemAct,
	holdemAway,
	holdemMovesFor,
	holdemTimeout,
	holdemView,
	newHoldemGame,
	RUNOUT_MS,
	rebuy,
	removeHoldemBot,
	removeHoldemPlayer,
	SHOWDOWN_MS,
	SMALL_BLIND,
	START_STACK,
	seatHoldem,
} from "../src/cards/holdem.js";
import { bestHand, type Card } from "../src/cards/poker.js";

/**
 * Hold'em's rules without a socket. Most tests seat players, choose the
 * button, deal on the clock, and then stack every hole card and the board, so
 * a hand plays out exactly as written.
 */

const NOW = 5_000_000;

const FULL_DECK: Card[] = [..."23456789TJQKA"].flatMap((rank) =>
	[..."SHDC"].map((suit) => rank + suit),
);

function table(stacks: number[]): HoldemGame {
	const game = newHoldemGame();
	stacks.forEach((chips, seat) => {
		seatHoldem(game, seat, `Player ${"ABCDEF"[seat]}@Phoenix`, `token-${seat}`, NOW);
		(game.seats[seat] as { chips: number }).chips = chips;
	});
	return game;
}

/** Deals the next hand with the button where asked. */
function deal(game: HoldemGame, button: number): void {
	const players = game.seats.flatMap((sitting, seat) => (sitting !== null ? [seat] : []));
	const index = players.indexOf(button);
	game.button = players[(index - 1 + players.length) % players.length] as number;
	game.deadline = NOW;
	expect(holdemTimeout(game, NOW)).toBe(true);
	expect(game.phase).toBe("betting");
}

/** Sets every hole card and the board to come, each street's burn card from what is left. */
function stack(game: HoldemGame, holes: (Card[] | null)[], board: Card[]): void {
	holes.forEach((hole, seat) => {
		const sitting = game.seats[seat];
		if (hole !== null && sitting !== null && sitting !== undefined) {
			sitting.hole = [...hole];
		}
	});
	const used = new Set([...holes.flatMap((hole) => hole ?? []), ...board]);
	const spare = FULL_DECK.filter((card) => !used.has(card));
	game.deck = [
		spare[0] as Card,
		...board.slice(0, 3),
		spare[1] as Card,
		board[3] as Card,
		spare[2] as Card,
		board[4] as Card,
		...spare.slice(3),
	];
}

const go = (game: HoldemGame, seat: number, move: HoldemMove, to: number | null = null) =>
	expect(holdemAct(game, seat, move, to, NOW)).toBeNull();

const at = (game: HoldemGame, seat: number) => {
	const sitting = game.seats[seat];
	if (sitting === null || sitting === undefined) {
		throw new Error(`Nobody in seat ${seat}.`);
	}
	return sitting;
};

/** Checks every remaining street down to the showdown, the first to act after the button first. */
function checkDown(game: HoldemGame): void {
	for (let moves = 0; moves < 40 && game.phase === "betting"; moves += 1) {
		go(game, game.turn as number, "check");
	}
	expect(game.phase).toBe("showdown");
}

describe("poker hands", () => {
	const rank = (...cards: Card[]) => bestHand(cards);

	it("orders the categories from a royal flush down to a high card", () => {
		const hands = [
			rank("AS", "KS", "QS", "JS", "TS", "2D", "3C"),
			rank("9H", "8H", "7H", "6H", "5H", "AD", "AC"),
			rank("7S", "7H", "7D", "7C", "KD", "2S", "3S"),
			rank("7S", "7H", "7D", "KC", "KD", "2S", "3S"),
			rank("2D", "7D", "9D", "JD", "KD", "AS", "AC"),
			rank("5S", "6H", "7D", "8C", "9D", "AS", "AC"),
			rank("7S", "7H", "7D", "2C", "9D", "JS", "KH"),
			rank("7S", "7H", "9D", "9C", "2D", "JS", "KH"),
			rank("7S", "7H", "2D", "9C", "4D", "JS", "KH"),
			rank("2S", "7H", "9D", "JC", "4D", "QS", "KH"),
		];
		expect(hands.map((hand) => hand.category)).toEqual([8, 8, 7, 6, 5, 4, 3, 2, 1, 0]);
		for (let index = 1; index < hands.length; index += 1) {
			expect((hands[index - 1] as { score: number }).score).toBeGreaterThan(
				(hands[index] as { score: number }).score,
			);
		}
		expect(hands.map((hand) => hand.name)).toEqual([
			"ROYAL FLUSH",
			"STRAIGHT FLUSH, NINE HIGH",
			"FOUR SEVENS",
			"SEVENS FULL OF KINGS",
			"FLUSH, KING HIGH",
			"STRAIGHT, NINE HIGH",
			"THREE SEVENS",
			"NINES AND SEVENS",
			"PAIR OF SEVENS",
			"KING HIGH",
		]);
	});

	it("counts the ace low in a five-high straight, which any higher straight beats", () => {
		const wheel = rank("AS", "2H", "3D", "4C", "5S", "KD", "KH");
		const six = rank("2H", "3D", "4C", "5S", "6D", "KD", "KH");
		expect(wheel.name).toBe("STRAIGHT, FIVE HIGH");
		expect(wheel.five).toEqual(["5S", "4C", "3D", "2H", "AS"]);
		expect(six.score).toBeGreaterThan(wheel.score);
	});

	it("settles equal hands on their kickers, and only the best five count", () => {
		const aceKicker = rank("KS", "KH", "AD", "7C", "4S", "3D", "2C");
		const queenKicker = rank("KD", "KC", "QD", "7C", "4S", "3D", "2C");
		expect(aceKicker.score).toBeGreaterThan(queenKicker.score);

		// Both play the board's straight: a tie, whatever else they hold.
		const one = rank("AS", "KD", "QH", "JC", "TS", "2C", "3D");
		const other = rank("AS", "KD", "QH", "JC", "TS", "4H", "5H");
		expect(one.score).toBe(other.score);
	});

	it("makes the best five out of seven: two pair from three, a full house from two sets", () => {
		const threePair = rank("KS", "KH", "9D", "9C", "4S", "4D", "AC");
		expect(threePair.name).toBe("KINGS AND NINES");
		expect(threePair.five).toEqual(["KS", "KH", "9D", "9C", "AC"]);
		expect(rank("7S", "7H", "7D", "5C", "5D", "5S", "2C").name).toBe("SEVENS FULL OF FIVES");
		expect(rank("6S", "6H", "2D", "9C", "4D", "JS", "KH").name).toBe("PAIR OF SIXES");
	});
});

describe("dealing", () => {
	it("deals once two are seated, after a moment for a third to sit down", () => {
		const game = newHoldemGame();
		seatHoldem(game, 0, "Player A@Phoenix", "a", NOW);
		expect(game.deadline).toBeNull();
		seatHoldem(game, 1, "Player B@Phoenix", "b", NOW);
		expect(game.deadline).toBe(NOW + DEAL_DELAY_MS);

		expect(holdemTimeout(game, NOW + DEAL_DELAY_MS)).toBe(true);
		expect(game).toMatchObject({
			phase: "betting",
			street: "preflop",
			hands: 1,
			current: BIG_BLIND,
		});
		expect(at(game, 0).hole).toHaveLength(2);
		expect(at(game, 1).hole).toHaveLength(2);
		expect(game.deck).toHaveLength(48);
	});

	it("heads up, puts the small blind on the button, which acts first before the flop and last after it", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		expect(game).toMatchObject({ button: 0, small: 0, big: 1, turn: 0 });
		expect(at(game, 0)).toMatchObject({
			bet: SMALL_BLIND,
			chips: START_STACK - SMALL_BLIND,
			last: "SB",
		});
		expect(at(game, 1)).toMatchObject({
			bet: BIG_BLIND,
			chips: START_STACK - BIG_BLIND,
			last: "BB",
		});
		expect(holdemMovesFor(game, 0)).toEqual({
			fold: true,
			check: false,
			call: SMALL_BLIND,
			raise: { min: 2 * BIG_BLIND, max: START_STACK },
		});

		go(game, 0, "call");
		// The big blind's option: it may check, or raise.
		expect(game.turn).toBe(1);
		expect(holdemMovesFor(game, 1)).toMatchObject({ check: true, call: 0, raise: { min: 20 } });
		go(game, 1, "check");
		expect(game).toMatchObject({ street: "flop", turn: 1, current: 0 });
		expect(game.board).toHaveLength(3);
	});

	it("with three, puts the blinds left of the button, and the button acts first before the flop", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		expect(game).toMatchObject({ button: 0, small: 1, big: 2, turn: 0 });
		go(game, 0, "call");
		go(game, 1, "call");
		go(game, 2, "check");
		// After the flop the first player left of the button opens.
		expect(game).toMatchObject({ street: "flop", turn: 1 });
	});

	it("moves the button round, passing over a player who is away", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		go(game, 0, "fold");
		go(game, 1, "fold");
		expect(game.phase).toBe("showdown");

		at(game, 1).away = NOW;
		holdemAway(game, NOW);
		holdemTimeout(game, NOW + FOLDOUT_MS);
		expect(game).toMatchObject({ phase: "betting", button: 2, small: 2, big: 0 });
		expect(at(game, 1).inHand).toBe(false);
	});
});

describe("betting", () => {
	it("holds every raise to at least the last one, and takes an all-in for less", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		go(game, 0, "raise", 30);
		expect(holdemMovesFor(game, 1)?.raise).toEqual({ min: 50, max: START_STACK });
		expect(holdemAct(game, 1, "raise", 40, NOW)).toBe("bad-raise");
		expect(holdemAct(game, 1, "raise", START_STACK + 10, NOW)).toBe("bad-raise");
		go(game, 1, "raise", 80);
		expect(holdemMovesFor(game, 2)?.raise?.min).toBe(130);
		go(game, 2, "raise", START_STACK);
		expect(at(game, 2)).toMatchObject({ allIn: true, chips: 0, last: "ALL IN" });
	});

	it("does not reopen the betting to those who acted when an all-in raise falls short", () => {
		const game = table([START_STACK, START_STACK, 45]);
		deal(game, 0);
		go(game, 0, "raise", 30);
		go(game, 1, "call");
		// The big blind's last 35 chips make 45: fifteen more, short of a full raise of twenty.
		go(game, 2, "raise", 45);
		expect(game.current).toBe(45);
		expect(game.turn).toBe(0);
		expect(holdemMovesFor(game, 0)).toMatchObject({ call: 15, raise: null });
		go(game, 0, "call");
		expect(holdemMovesFor(game, 1)).toMatchObject({ call: 15, raise: null });
		go(game, 1, "call");
		expect(game).toMatchObject({ street: "flop", turn: 1 });
	});

	it("refuses a check against a bet, a call of nothing, and a move out of turn", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		expect(holdemAct(game, 0, "check", null, NOW)).toBe("cannot");
		expect(holdemAct(game, 1, "call", null, NOW)).toBe("not-your-turn");
		go(game, 0, "call");
		expect(holdemAct(game, 1, "call", null, NOW)).toBe("cannot");
	});

	it("lets nobody raise when everyone else is all in", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		go(game, 0, "raise", START_STACK);
		expect(holdemMovesFor(game, 1)).toMatchObject({ call: START_STACK - BIG_BLIND, raise: null });
	});
});

describe("settling the hand", () => {
	it("goes to the best hand at the showdown, which turns over the cards of everyone still in", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		stack(
			game,
			[
				["AS", "AD"],
				["KS", "KD"],
			],
			["2C", "7H", "9S", "JD", "4C"],
		);
		go(game, 0, "call");
		go(game, 1, "check");
		checkDown(game);

		expect(game.result).toMatchObject({
			uncontested: false,
			pots: [{ amount: 20, winners: [0], hand: "PAIR OF ACES" }],
			won: [20, 0, 0, 0, 0, 0],
		});
		expect(game.result?.shown[1]).toMatchObject({ cards: ["KS", "KD"], hand: "PAIR OF KINGS" });
		expect(at(game, 0).chips).toBe(START_STACK + 10);
		expect(at(game, 1).chips).toBe(START_STACK - 10);
		expect(game.deadline).toBe(NOW + SHOWDOWN_MS);
	});

	it("splits a tie, the odd chip to the first winner left of the button", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		stack(
			game,
			[
				["2C", "3D"],
				["4H", "5H"],
				["2D", "3C"],
			],
			["AS", "KD", "QH", "JC", "TS"],
		);
		go(game, 0, "call");
		go(game, 1, "fold");
		go(game, 2, "check");
		checkDown(game);

		expect(game.result?.pots).toEqual([
			{ amount: 25, winners: [2, 0], hand: "STRAIGHT, ACE HIGH" },
		]);
		expect(at(game, 2).chips).toBe(START_STACK - 10 + 13);
		expect(at(game, 0).chips).toBe(START_STACK - 10 + 12);
		expect(at(game, 1).chips).toBe(START_STACK - SMALL_BLIND);
	});

	it("gives a short stack all in only the pot it matched, and the side pot to the best of the rest", () => {
		const game = table([START_STACK, START_STACK, 100]);
		deal(game, 0);
		stack(
			game,
			[
				["KS", "KD"],
				["QS", "QD"],
				["AS", "AD"],
			],
			["2C", "7H", "9S", "JD", "4C"],
		);
		go(game, 0, "raise", 200);
		go(game, 1, "call");
		go(game, 2, "call");
		expect(at(game, 2)).toMatchObject({ allIn: true, total: 100 });
		expect(buildPots(game)).toEqual([
			{ amount: 300, eligible: [0, 1, 2] },
			{ amount: 200, eligible: [0, 1] },
		]);
		checkDown(game);

		expect(game.result?.pots).toEqual([
			{ amount: 300, winners: [2], hand: "PAIR OF ACES" },
			{ amount: 200, winners: [0], hand: "PAIR OF KINGS" },
		]);
		expect([at(game, 0).chips, at(game, 1).chips, at(game, 2).chips]).toEqual([1000, 800, 300]);
	});

	it("gives the pot to the last player in when everyone else folds, turning nothing over", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		go(game, 0, "fold");
		go(game, 1, "fold");
		expect(game).toMatchObject({ phase: "showdown", deadline: NOW + FOLDOUT_MS });
		expect(game.result).toMatchObject({
			uncontested: true,
			pots: [{ amount: 15, winners: [2], hand: null }],
		});
		expect(game.result?.shown.every((shown) => shown === null)).toBe(true);
		expect(at(game, 2).chips).toBe(START_STACK + SMALL_BLIND);
	});

	it("with everyone all in, turns the cards over and deals the board out a street at a time", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		stack(
			game,
			[
				["AS", "AD"],
				["KS", "KD"],
			],
			["2C", "7H", "9S", "JD", "4C"],
		);
		go(game, 0, "raise", START_STACK);
		go(game, 1, "call");
		expect(game).toMatchObject({ phase: "runout", deadline: NOW + RUNOUT_MS });
		expect(holdemView(game, 0, NOW).seats[1]?.shown).toEqual(["KS", "KD"]);

		holdemTimeout(game, NOW + RUNOUT_MS);
		expect(game.board).toHaveLength(3);
		holdemTimeout(game, NOW + 2 * RUNOUT_MS);
		expect(game.board).toHaveLength(4);
		holdemTimeout(game, NOW + 3 * RUNOUT_MS);
		expect(game.board).toEqual(["2C", "7H", "9S", "JD", "4C"]);
		expect(game.phase).toBe("showdown");
		expect([at(game, 0).chips, at(game, 1).chips]).toEqual([2 * START_STACK, 0]);
	});
});

describe("the clock, leaving and buying back in", () => {
	it("checks for a player who runs out of time when they can, and folds them when they cannot", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		expect(game.deadline).toBe(NOW + HOLDEM_TURN_MS);
		go(game, 0, "call");
		// The big blind, facing nothing more, checks, and the flop comes.
		holdemTimeout(game, NOW + HOLDEM_TURN_MS);
		expect(game.street).toBe("flop");

		holdemTimeout(game, NOW + 2 * HOLDEM_TURN_MS);
		holdemTimeout(game, NOW + 3 * HOLDEM_TURN_MS);
		expect(game.street).toBe("turn");
	});

	it("folds a player who lets the clock run out facing a bet", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		holdemTimeout(game, NOW + HOLDEM_TURN_MS);
		expect(at(game, 0)).toMatchObject({ folded: true, last: "FOLD" });
		expect(game.result).toMatchObject({ uncontested: true, pots: [{ winners: [1] }] });
	});

	it("waits only a moment for a player who drops on their turn", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		at(game, 0).away = NOW + 1_000;
		holdemAway(game, NOW + 1_000);
		expect(game.deadline).toBe(NOW + 1_000 + AWAY_TURN_MS);
	});

	it("keeps in the pot what a player who leaves put in, and passes their turn on", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		go(game, 0, "raise", 30);
		removeHoldemPlayer(game, 1, NOW);
		expect(game.seats[1]).toBeNull();
		expect(game.gone).toEqual([SMALL_BLIND]);
		expect(game.turn).toBe(2);
		go(game, 2, "call");
		expect(holdemView(game, 0, NOW)).toMatchObject({ street: "flop", pot: 65 });
	});

	it("gives the pot to the one player left when the other leaves", () => {
		const game = table([START_STACK, START_STACK]);
		deal(game, 0);
		removeHoldemPlayer(game, 1, NOW);
		expect(game.result).toMatchObject({ uncontested: true, pots: [{ amount: 15, winners: [0] }] });
		expect(at(game, 0).chips).toBe(START_STACK + BIG_BLIND);
	});

	it("buys a player back in once they have run out, and not before", () => {
		const game = table([START_STACK, START_STACK]);
		expect(rebuy(game, 0, NOW)).toBe("has-chips");
		deal(game, 0);
		go(game, 0, "fold");
		at(game, 0).chips = 0;
		holdemTimeout(game, NOW + FOLDOUT_MS);
		// One player with chips is not a game.
		expect(game).toMatchObject({ phase: "waiting", deadline: null });

		expect(rebuy(game, 0, NOW + FOLDOUT_MS)).toBeNull();
		expect(at(game, 0).chips).toBe(START_STACK);
		expect(game.deadline).toBe(NOW + FOLDOUT_MS + DEAL_DELAY_MS);
	});
});

describe("bots", () => {
	it("are seated and sent away by the host, and play hands out with everyone else on the clock", () => {
		const game = table([START_STACK]);
		expect(addHoldemBot(game, 1, NOW)).toBe("not-host");
		for (let bot = 1; bot < HOLDEM_SEATS; bot += 1) {
			expect(addHoldemBot(game, 0, NOW)).toBeNull();
		}
		expect(game.seats.map((sitting) => sitting?.name)).toEqual([
			"Player A@Phoenix",
			"Chrome@Grid",
			"Vex@Grid",
			"Rook@Grid",
			"Halo@Grid",
			"Jinx@Grid",
		]);
		expect(addHoldemBot(game, 0, NOW)).toBe("table-full");

		let now = NOW;
		let showdowns = 0;
		for (let tick = 0; tick < 2_000 && game.hands < 25; tick += 1) {
			now = Math.max(now, game.deadline ?? now);
			const before = game.phase;
			expect(holdemTimeout(game, now)).toBe(true);
			for (const sitting of game.seats) {
				expect(sitting?.chips ?? 0).toBeGreaterThanOrEqual(0);
			}
			if (before !== "showdown" && game.phase === "showdown") {
				showdowns += 1;
				// Every chip put in is paid out, no more.
				const putIn =
					game.seats.reduce((sum, sitting) => sum + (sitting?.inHand ? sitting.total : 0), 0) +
					game.gone.reduce((sum, chips) => sum + chips, 0);
				expect(game.result?.won.reduce((sum, chips) => sum + chips, 0)).toBe(putIn);
			}
		}
		expect(game.hands).toBeGreaterThanOrEqual(25);
		expect(showdowns).toBeGreaterThanOrEqual(20);

		expect(removeHoldemBot(game, 0, 0, now)).toBe("no-bot");
		expect(removeHoldemBot(game, 0, 3, now)).toBeNull();
		expect(game.seats[3]).toBeNull();
	});

	it("judge a hand's chances fairly: aces against one hand win most of the time, a lost hand never", () => {
		const hidden = FULL_DECK.filter((card) => !["AS", "AD"].includes(card));
		expect(equity(["AS", "AD"], [], 1, hidden)).toBeGreaterThan(0.7);

		const board = ["KS", "KD", "KH", "2C", "2D"];
		const rest = FULL_DECK.filter((card) => ![...board, "3S", "4S"].includes(card));
		// Kings full of twos on the board, which this hand plays: it ties most hands, and only a king or a pocket pair beats it.
		expect(equity(["3S", "4S"], board, 1, rest)).toBeGreaterThan(0.3);
	});
});

describe("what a seat is sent", () => {
	it("shows a seat its own two cards and the board, and nobody else's until a showdown", () => {
		const game = table([START_STACK, START_STACK, START_STACK]);
		deal(game, 0);
		stack(
			game,
			[
				["AS", "AD"],
				["KS", "KD"],
				["QS", "QD"],
			],
			["2C", "7H", "9S", "JD", "4C"],
		);
		go(game, 0, "call");
		go(game, 1, "call");
		go(game, 2, "check");

		const view = holdemView(game, 0, NOW);
		expect(view).toMatchObject({ hole: ["AS", "AD"], board: ["2C", "7H", "9S"], street: "flop" });
		expect(view).not.toHaveProperty("deck");
		const text = JSON.stringify(view);
		for (const hidden of ["KS", "KD", "QS", "QD", "JD", "4C"]) {
			expect(text).not.toContain(`"${hidden}"`);
		}
		expect(view.seats.map((seat) => seat?.cards)).toEqual([
			2,
			2,
			2,
			undefined,
			undefined,
			undefined,
		]);
		expect(holdemView(game, null, NOW).hole).toEqual([]);
	});
});
