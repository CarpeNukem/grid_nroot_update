import { describe, expect, it } from "vitest";
import {
	act,
	BET_WINDOW_MS,
	type Card,
	CUT_CARDS,
	freshShoe,
	type Game,
	movesFor,
	newGame,
	newPlayer,
	placeBet,
	removePlayer,
	START_CHIPS,
	TURN_MS,
	timeout,
	topUp,
	totalOf,
} from "../src/cards/blackjack.js";

/**
 * Blackjack's rules, as the card hall plays them, without a socket: each test
 * stacks the shoe and plays a round by hand. The deal goes a card to each
 * seat with a bet, the dealer's up card, a second card each, then the hole
 * card; every card after that comes off the shoe in order.
 */

const NOW = 1_000_000;

/** Enough low cards after the stacked ones that the shoe is never short of the cut card. */
const FILLER: Card[] = Array.from({ length: CUT_CARDS + 20 }, () => "2C");

function tableFor(players: number): Game {
	const game = newGame();
	for (let seat = 0; seat < players; seat += 1) {
		game.seats[seat] = newPlayer(`P${seat}`, `token-${seat}`);
	}
	return game;
}

function stack(game: Game, cards: Card[]): void {
	game.shoe = [...cards, ...FILLER];
}

/** One player, one bet, the shoe stacked: dealt as soon as the bet is down. */
function dealtSolo(cards: Card[], bet = 100): Game {
	const game = tableFor(1);
	stack(game, cards);
	expect(placeBet(game, 0, bet, NOW)).toBeNull();
	return game;
}

const player = (game: Game, seat = 0) => {
	const found = game.seats[seat];
	if (found === null || found === undefined) {
		throw new Error(`Nobody in seat ${seat}.`);
	}
	return found;
};

describe("counting a hand", () => {
	it("counts an ace as 11 while that stays at 21 or under, and says so", () => {
		expect(totalOf(["AS", "6H"])).toEqual({ total: 17, soft: true });
		expect(totalOf(["AS", "6H", "TD"])).toEqual({ total: 17, soft: false });
		expect(totalOf(["AS", "AH", "9D"])).toEqual({ total: 21, soft: true });
		expect(totalOf(["KS", "QH"])).toEqual({ total: 20, soft: false });
		expect(totalOf(["TS", "6H", "9D"])).toEqual({ total: 25, soft: false });
	});

	it("shuffles six whole decks", () => {
		const shoe = freshShoe();
		expect(shoe).toHaveLength(312);
		const counts = new Map<string, number>();
		for (const card of shoe) {
			counts.set(card, (counts.get(card) ?? 0) + 1);
		}
		expect(counts.size).toBe(52);
		expect([...counts.values()].every((count) => count === 6)).toBe(true);
	});
});

describe("dealing", () => {
	it("deals two cards to the player and two to the dealer, the second face down, and starts the player's clock", () => {
		const game = dealtSolo(["TS", "9H", "7C", "QD"]);

		expect(game.phase).toBe("playing");
		expect(player(game).hands).toEqual([
			expect.objectContaining({ cards: ["TS", "7C"], bet: 100, done: false }),
		]);
		expect(player(game).chips).toBe(START_CHIPS - 100);
		expect(game.dealer).toEqual(["9H", "QD"]);
		expect(game.hole).toBe(true);
		expect(game.turn).toEqual({ seat: 0, hand: 0 });
		expect(game.deadline).toBe(NOW + TURN_MS);
		expect(game.round).toBe(1);
	});

	it("deals seats in order around the table, one card at a time", () => {
		const game = tableFor(2);
		stack(game, ["TS", "9H", "5D", "6C", "9S", "TC"]);
		placeBet(game, 0, 10, NOW);
		placeBet(game, 1, 20, NOW);

		expect(player(game, 0).hands[0]?.cards).toEqual(["TS", "6C"]);
		expect(player(game, 1).hands[0]?.cards).toEqual(["9H", "9S"]);
		expect(game.dealer).toEqual(["5D", "TC"]);
		expect(game.turn).toEqual({ seat: 0, hand: 0 });
	});

	it("shuffles a new shoe before a round once the cut card has come out", () => {
		const game = tableFor(1);
		game.round = 3;
		game.shoe = FILLER.slice(0, CUT_CARDS - 1);
		placeBet(game, 0, 10, NOW);

		expect(game.shuffled).toBe(true);
		expect(game.shoe.length).toBe(312 - 4);

		game.phase = "betting";
		player(game).hands = [];
		placeBet(game, 0, 10, NOW);
		expect(game.shuffled).toBe(false);
	});
});

describe("naturals", () => {
	it("pays a blackjack 3 to 2 at once, and the dealer has nothing to draw for", () => {
		const game = dealtSolo(["AS", "9H", "KD", "7C"]);

		expect(game.phase).toBe("betting");
		expect(game.hole).toBe(false);
		expect(game.dealer).toEqual(["9H", "7C"]);
		expect(player(game).hands[0]).toMatchObject({ result: "blackjack", paid: 250 });
		expect(player(game).chips).toBe(START_CHIPS + 150);
	});

	it("ends the round on a dealer blackjack before anyone moves, a player's own blackjack pushing", () => {
		const game = tableFor(2);
		stack(game, ["9S", "AH", "AD", "7C", "KH", "KD"]);
		placeBet(game, 0, 100, NOW);
		placeBet(game, 1, 100, NOW);

		expect(game.phase).toBe("betting");
		expect(game.dealer).toEqual(["AD", "KD"]);
		expect(player(game, 0).hands[0]).toMatchObject({ result: "lose", paid: 0 });
		expect(player(game, 1).hands[0]).toMatchObject({ result: "push", paid: 100 });
		expect(player(game, 0).chips).toBe(START_CHIPS - 100);
		expect(player(game, 1).chips).toBe(START_CHIPS);
	});

	it("plays on when the dealer peeks under an ace and finds no blackjack", () => {
		const game = dealtSolo(["TS", "AH", "8C", "6D"]);
		expect(game.phase).toBe("playing");
		expect(movesFor(game, 0)).toContain("hit");
	});
});

describe("playing a hand", () => {
	it("busts a hand that goes over 21, and the dealer turns the hole card without drawing", () => {
		const game = dealtSolo(["TS", "6H", "5C", "TD", "KH"]);
		expect(act(game, 0, "hit", NOW)).toBeNull();

		expect(player(game).hands[0]).toMatchObject({
			cards: ["TS", "5C", "KH"],
			result: "bust",
			paid: 0,
		});
		expect(game.dealer).toEqual(["6H", "TD"]);
		expect(player(game).chips).toBe(START_CHIPS - 100);
	});

	it("draws the dealer to 17 once the player stands, and pays a dealer bust", () => {
		const game = dealtSolo(["TS", "6H", "9C", "TD", "8H"]);
		act(game, 0, "stand", NOW);

		expect(game.dealer).toEqual(["6H", "TD", "8H"]);
		expect(player(game).hands[0]).toMatchObject({ result: "win", paid: 200 });
		expect(player(game).chips).toBe(START_CHIPS + 100);
	});

	it("stands the dealer on a soft 17", () => {
		const game = dealtSolo(["TS", "AH", "8C", "6D"]);
		act(game, 0, "stand", NOW);

		expect(game.dealer).toEqual(["AH", "6D"]);
		expect(player(game).hands[0]).toMatchObject({ result: "win" });
	});

	it("pushes a tie and pays nothing extra", () => {
		const game = dealtSolo(["TS", "9H", "8C", "9D"]);
		act(game, 0, "stand", NOW);

		expect(player(game).hands[0]).toMatchObject({ result: "push", paid: 100 });
		expect(player(game).chips).toBe(START_CHIPS);
	});

	it("stands a hand on 21 by itself", () => {
		const game = dealtSolo(["TS", "9H", "5C", "8D", "6H", "KS"]);
		act(game, 0, "hit", NOW);

		expect(player(game).hands[0]?.cards).toEqual(["TS", "5C", "6H"]);
		expect(game.phase).toBe("betting");
		expect(game.dealer).toEqual(["9H", "8D"]);
	});

	it("doubles the stake for exactly one more card", () => {
		const game = dealtSolo(["6S", "9H", "5C", "7D", "TH", "TC"]);
		expect(act(game, 0, "double", NOW)).toBeNull();

		expect(player(game).hands[0]).toMatchObject({
			cards: ["6S", "5C", "TH"],
			bet: 200,
			doubled: true,
			result: "win",
			paid: 400,
		});
		expect(game.dealer).toEqual(["9H", "7D", "TC"]);
		expect(player(game).chips).toBe(START_CHIPS + 200);
	});

	it("splits a pair into two hands played in turn, doubling after the split allowed", () => {
		const game = dealtSolo(["8S", "7H", "8C", "TD", "3H", "TC", "9D"]);
		expect(movesFor(game, 0)).toEqual(["hit", "stand", "double", "split"]);
		expect(act(game, 0, "split", NOW)).toBeNull();

		expect(player(game).hands.map((hand) => hand.cards)).toEqual([
			["8S", "3H"],
			["8C", "TC"],
		]);
		expect(game.turn).toEqual({ seat: 0, hand: 0 });

		act(game, 0, "double", NOW);
		expect(game.turn).toEqual({ seat: 0, hand: 1 });
		act(game, 0, "stand", NOW);

		expect(game.dealer).toEqual(["7H", "TD"]);
		expect(player(game).hands).toEqual([
			expect.objectContaining({ cards: ["8S", "3H", "9D"], bet: 200, result: "win", paid: 400 }),
			expect.objectContaining({ cards: ["8C", "TC"], bet: 100, result: "win", paid: 200 }),
		]);
		expect(player(game).chips).toBe(START_CHIPS - 300 + 600);
	});

	it("splits any two tens, as two cards of the same value", () => {
		const game = dealtSolo(["KS", "7H", "QC", "TD"]);
		expect(movesFor(game, 0)).toContain("split");
	});

	it("gives split aces one card each, and counts 21 on one as 21 rather than blackjack", () => {
		const game = dealtSolo(["AS", "9H", "AC", "7D", "KH", "5C", "2D"]);
		act(game, 0, "split", NOW);

		expect(game.phase).toBe("betting");
		expect(player(game).hands).toEqual([
			expect.objectContaining({ cards: ["AS", "KH"], result: "win", paid: 200 }),
			expect.objectContaining({ cards: ["AC", "5C"], result: "lose", paid: 0 }),
		]);
		expect(game.dealer).toEqual(["9H", "7D", "2D"]);
	});

	it("splits to four hands and no further", () => {
		const game = dealtSolo(["8S", "7H", "8C", "TD", "8D", "8H", "8S", "2C", "8H", "2D"]);
		act(game, 0, "split", NOW);
		act(game, 0, "split", NOW);
		act(game, 0, "split", NOW);

		// Another pair of eights on the first hand, and no fifth hand for it.
		expect(player(game).hands).toHaveLength(4);
		expect(player(game).hands[0]?.cards).toEqual(["8S", "8H"]);
		expect(movesFor(game, 0)).toEqual(["hit", "stand", "double"]);
	});

	it("offers no double or split the player cannot cover", () => {
		const game = tableFor(1);
		player(game).chips = 150;
		stack(game, ["8S", "7H", "8C", "TD"]);
		placeBet(game, 0, 100, NOW);

		expect(movesFor(game, 0)).toEqual(["hit", "stand"]);
		expect(act(game, 0, "double", NOW)).toBe("cannot");
	});

	it("refuses a move from anyone but the player on turn", () => {
		const game = tableFor(2);
		stack(game, ["TS", "9H", "5D", "6C", "9S", "TC"]);
		placeBet(game, 0, 10, NOW);
		placeBet(game, 1, 10, NOW);

		expect(act(game, 1, "hit", NOW)).toBe("not-your-turn");
		expect(movesFor(game, 1)).toEqual([]);
		act(game, 0, "stand", NOW);
		expect(game.turn).toEqual({ seat: 1, hand: 0 });
	});
});

describe("bets", () => {
	it("takes bets of 10 to 500 in tens, never more than the player has", () => {
		const game = tableFor(2);
		expect(placeBet(game, 0, 5, NOW)).toBe("bad-bet");
		expect(placeBet(game, 0, 15, NOW)).toBe("bad-bet");
		expect(placeBet(game, 0, 510, NOW)).toBe("bad-bet");
		expect(placeBet(game, 0, "100", NOW)).toBe("bad-bet");
		player(game).chips = 40;
		expect(placeBet(game, 0, 50, NOW)).toBe("bad-bet");
		expect(placeBet(game, 0, 40, NOW)).toBeNull();
		expect(player(game)).toMatchObject({ chips: 0, bet: 40 });
	});

	it("lets a bet be changed or taken back until the deal", () => {
		const game = tableFor(2);
		placeBet(game, 0, 100, NOW);
		placeBet(game, 0, 30, NOW);
		expect(player(game)).toMatchObject({ chips: START_CHIPS - 30, bet: 30 });

		placeBet(game, 0, 0, NOW);
		expect(player(game)).toMatchObject({ chips: START_CHIPS, bet: 0 });
		expect(game.deadline).toBeNull();
	});

	it("waits for everyone seated, closing the bets on the clock for anyone who has not", () => {
		const game = tableFor(2);
		stack(game, ["TS", "9H", "7C", "QD"]);
		placeBet(game, 0, 10, NOW);
		expect(game.phase).toBe("betting");
		expect(game.deadline).toBe(NOW + BET_WINDOW_MS);

		// A second bet does not restart the clock.
		placeBet(game, 0, 20, NOW + 5000);
		expect(game.deadline).toBe(NOW + BET_WINDOW_MS);

		expect(timeout(game, NOW + BET_WINDOW_MS - 1)).toBe(false);
		expect(timeout(game, NOW + BET_WINDOW_MS)).toBe(true);
		expect(game.phase).toBe("playing");
		expect(player(game, 0).hands).toHaveLength(1);
		expect(player(game, 1).hands).toHaveLength(0);
	});

	it("deals at once when the last player seated bets", () => {
		const game = tableFor(3);
		stack(game, ["TS", "9S", "8S", "7H", "6C", "5C", "4C", "TD"]);
		placeBet(game, 0, 10, NOW);
		placeBet(game, 2, 10, NOW);
		expect(game.phase).toBe("betting");
		placeBet(game, 1, 10, NOW);
		expect(game.phase).toBe("playing");
	});

	it("does not wait for a player who is away", () => {
		const game = tableFor(2);
		stack(game, ["TS", "9H", "7C", "QD"]);
		player(game, 1).away = NOW;
		placeBet(game, 0, 10, NOW);
		expect(game.phase).toBe("playing");
	});

	it("takes no bet while a hand is being played", () => {
		const game = dealtSolo(["TS", "9H", "7C", "QD"]);
		expect(placeBet(game, 0, 10, NOW)).toBe("not-betting");
	});

	it("tops a player up to the starting stack only once they cannot cover the smallest bet", () => {
		const game = tableFor(1);
		expect(topUp(game, 0)).toBe("has-chips");
		player(game).chips = 0;
		expect(topUp(game, 0)).toBeNull();
		expect(player(game).chips).toBe(START_CHIPS);
	});
});

describe("the clock and leaving", () => {
	it("stands the hand on turn when its clock runs out", () => {
		const game = dealtSolo(["TS", "6H", "9C", "TD", "8H"]);
		expect(timeout(game, NOW + TURN_MS)).toBe(true);

		expect(player(game).hands[0]).toMatchObject({ cards: ["TS", "9C"], result: "win" });
		expect(game.phase).toBe("betting");
	});

	it("restarts the clock for each decision", () => {
		const game = dealtSolo(["2S", "6H", "3C", "TD", "4H"]);
		act(game, 0, "hit", NOW + 10_000);
		expect(game.deadline).toBe(NOW + 10_000 + TURN_MS);
	});

	it("moves play on when the player on turn leaves, and settles everyone else", () => {
		const game = tableFor(2);
		stack(game, ["TS", "9H", "6D", "6C", "9S", "TC", "5H"]);
		placeBet(game, 0, 10, NOW);
		placeBet(game, 1, 10, NOW);
		act(game, 0, "stand", NOW);

		removePlayer(game, 1, NOW);
		expect(game.seats[1]).toBeNull();
		expect(game.phase).toBe("betting");
		expect(player(game, 0).hands[0]?.result).not.toBeNull();
	});

	it("deals for the rest when the only player still to bet leaves", () => {
		const game = tableFor(2);
		stack(game, ["TS", "9H", "7C", "QD"]);
		placeBet(game, 0, 10, NOW);
		removePlayer(game, 1, NOW);
		expect(game.phase).toBe("playing");
	});

	it("bumps the table's sequence on every change a move depends on", () => {
		const game = dealtSolo(["2S", "6H", "3C", "TD", "4H"]);
		const dealt = game.seq;
		act(game, 0, "hit", NOW);
		expect(game.seq).toBeGreaterThan(dealt);
	});
});
