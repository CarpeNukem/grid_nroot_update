import { describe, expect, it } from "vitest";
import {
	addBot,
	BOT_THINK_MS,
	callLiar,
	HAND_SIZE,
	ICE_SLOTS,
	ICE_TURN_MS,
	type IceCard,
	type IceGame,
	iceMovesFor,
	iceTimeout,
	iceView,
	newIceGame,
	playCards,
	type Rank,
	REVEAL_MS,
	removeBot,
	removeIcePlayer,
	seatPlayer,
	startGame,
} from "../src/cards/blackice.js";

/**
 * BLACK ICE's rules without a socket. Most tests start a game and then
 * arrange it: the table rank, every hand, where each player's black ICE sits
 * and whose turn it is, so a round plays out exactly as written.
 */

const NOW = 5_000_000;

function seated(count: number): IceGame {
	const game = newIceGame();
	for (let seat = 0; seat < count; seat += 1) {
		seatPlayer(game, seat, `Player ${"ABCD"[seat]}@Phoenix`, `token-${seat}`);
	}
	return game;
}

function started(count: number): IceGame {
	const game = seated(count);
	expect(startGame(game, 0, NOW)).toBeNull();
	return game;
}

/** Sets a started game's round out by hand. */
function arrange(
	game: IceGame,
	{
		rank,
		hands,
		deadly,
		turn = 0,
	}: { rank: Rank; hands: IceCard[][]; deadly?: number[]; turn?: number },
): void {
	game.rank = rank;
	hands.forEach((hand, seat) => {
		const player = game.seats[seat];
		if (player) {
			player.hand = [...hand];
			player.deadly = deadly?.[seat] ?? 5;
		}
	});
	game.turn = turn;
	game.last = null;
	game.pile = 0;
}

const at = (game: IceGame, seat: number) => {
	const player = game.seats[seat];
	if (player === null || player === undefined) {
		throw new Error(`Nobody in seat ${seat}.`);
	}
	return player;
};

describe("starting a game", () => {
	it("deals five cards each from six queens, six kings, six aces and two jokers, and names a table rank", () => {
		const game = started(4);
		const dealt = game.seats.flatMap((player) => player?.hand ?? []);
		expect(dealt).toHaveLength(4 * HAND_SIZE);
		for (const rank of ["Q", "K", "A"]) {
			expect(dealt.filter((card) => card[0] === rank)).toHaveLength(6);
		}
		expect(dealt.filter((card) => card[0] === "Z")).toHaveLength(2);
		expect(["Q", "K", "A"]).toContain(game.rank);
		expect(game.phase).toBe("playing");
		expect(game.turn).not.toBeNull();
		expect(game.deadline).toBe(NOW + ICE_TURN_MS);
	});

	it("hides a black slot somewhere in every player's ICE", () => {
		for (let trial = 0; trial < 20; trial += 1) {
			const game = started(3);
			for (const player of game.seats.slice(0, 3)) {
				expect(player?.deadly).toBeGreaterThanOrEqual(0);
				expect(player?.deadly).toBeLessThan(ICE_SLOTS);
				expect(player?.used).toBe(0);
			}
		}
	});

	it("is the host's to start, with at least two at the table, and only between games", () => {
		const alone = seated(1);
		expect(startGame(alone, 0, NOW)).toBe("too-few");

		const game = seated(2);
		expect(startGame(game, 1, NOW)).toBe("not-host");
		expect(startGame(game, 0, NOW)).toBeNull();
		expect(startGame(game, 0, NOW)).toBe("not-waiting");
	});
});

describe("bots", () => {
	it("lets the host fill empty seats with bots, named like characters, and take them away again", () => {
		const game = seated(1);
		expect(addBot(game, 0)).toBeNull();
		expect(addBot(game, 0)).toBeNull();
		expect(game.seats[1]).toMatchObject({ name: "Glitch@Ice", bot: true });
		expect(game.seats[2]).toMatchObject({ name: "Daemon@Ice", bot: true });

		expect(removeBot(game, 0, 1)).toBeNull();
		expect(game.seats[1]).toBeNull();
		expect(removeBot(game, 0, 0)).toBe("no-bot");
	});

	it("refuses bots from anyone but the host, at a full table, or during a game", () => {
		const game = seated(2);
		expect(addBot(game, 1)).toBe("not-host");
		addBot(game, 0);
		addBot(game, 0);
		expect(addBot(game, 0)).toBe("table-full");

		startGame(game, 0, NOW);
		expect(removeBot(game, 0, 2)).toBe("not-waiting");
	});

	it("starts a game against bots alone", () => {
		const game = seated(1);
		addBot(game, 0);
		expect(startGame(game, 0, NOW)).toBeNull();
		expect(at(game, 1).hand).toHaveLength(HAND_SIZE);
	});

	it("gives a bot a moment to think, then moves for it on the clock", () => {
		const game = seated(1);
		addBot(game, 0);
		startGame(game, 0, NOW);
		arrange(game, {
			rank: "K",
			hands: [
				["QS", "QH", "AS", "AH", "QD"],
				["KS", "KH", "QC", "AC", "ZR"],
			],
		});
		playCards(game, 0, [0], NOW);

		expect(game.turn).toBe(1);
		expect(game.deadline).toBeGreaterThanOrEqual(NOW + BOT_THINK_MS[0]);
		expect(game.deadline).toBeLessThanOrEqual(NOW + BOT_THINK_MS[1]);

		const seq = game.seq;
		expect(iceTimeout(game, game.deadline as number)).toBe(true);
		expect(game.seq).toBeGreaterThan(seq);
		// It either called or laid cards of its own.
		expect(game.phase === "reveal" || game.last?.seat === 1).toBe(true);
	});

	it("calls a claim it can prove false", () => {
		const game = seated(1);
		addBot(game, 0);
		startGame(game, 0, NOW);
		// The bot holds six kings and a joker between hand and nothing laid: three more kings cannot exist.
		arrange(game, {
			rank: "K",
			hands: [
				["QS", "QH", "QD", "AS", "AH"],
				["KS", "KH", "KD", "KC", "ZR"],
			],
		});
		at(game, 1).hand.push("KS", "ZB");
		playCards(game, 0, [0, 1, 2], NOW);
		iceTimeout(game, game.deadline as number);

		expect(game.phase).toBe("reveal");
		expect(game.reveal).toMatchObject({ caller: 1, liar: 0, truthful: false, victim: 0 });
	});

	it("plays a whole game out to a winner, every card and slot accounted for", () => {
		for (let trial = 0; trial < 25; trial += 1) {
			const game = seated(1);
			addBot(game, 0);
			addBot(game, 0);
			addBot(game, 0);
			startGame(game, 0, NOW);
			let now = NOW;
			for (let step = 0; step < 2000 && game.phase !== "over"; step += 1) {
				now = Math.max(now, game.deadline ?? now);
				iceTimeout(game, now);
				const held = game.seats.reduce((sum, player) => sum + (player?.hand.length ?? 0), 0);
				if (game.phase === "playing") {
					expect(held + game.pile).toBeLessThanOrEqual(20);
				}
				for (const player of game.seats) {
					expect(player?.used ?? 0).toBeLessThanOrEqual(ICE_SLOTS);
				}
			}

			expect(game.phase).toBe("over");
			expect(game.winner).not.toBeNull();
			expect(game.seats.filter((player) => player !== null && !player.out)).toHaveLength(1);
		}
	});
});

describe("laying cards", () => {
	it("lays one to three cards face down and passes the turn on", () => {
		const game = started(3);
		arrange(game, { rank: "A", hands: [["AS", "QH", "KD", "AC", "ZB"], ["KS"], ["QS"]] });
		expect(iceMovesFor(game, 0)).toEqual(["play"]);
		expect(playCards(game, 0, [0, 4], NOW)).toBeNull();

		expect(at(game, 0).hand).toEqual(["QH", "KD", "AC"]);
		expect(game.last).toEqual({ seat: 0, cards: ["AS", "ZB"] });
		expect(game.pile).toBe(2);
		expect(game.turn).toBe(1);
		expect(iceMovesFor(game, 1)).toEqual(["play", "call"]);
	});

	it("refuses no cards, more than three, the same card twice, cards not held, and anyone off turn", () => {
		const game = started(2);
		arrange(game, {
			rank: "Q",
			hands: [
				["QS", "QH", "KD", "AC", "ZB"],
				["KS", "KH"],
			],
		});
		expect(playCards(game, 0, [], NOW)).toBe("bad-play");
		expect(playCards(game, 0, [0, 1, 2, 3], NOW)).toBe("bad-play");
		expect(playCards(game, 0, [1, 1], NOW)).toBe("bad-play");
		expect(playCards(game, 0, [5], NOW)).toBe("bad-play");
		expect(playCards(game, 0, "0", NOW)).toBe("bad-play");
		expect(playCards(game, 1, [0], NOW)).toBe("not-your-turn");
	});

	it("passes over a player with no cards left", () => {
		const game = started(3);
		arrange(game, { rank: "K", hands: [["KS", "KH"], [], ["QS", "QH"]] });
		playCards(game, 0, [0], NOW);
		expect(game.turn).toBe(2);
	});

	it("leaves nothing but a call to the last player with cards", () => {
		const game = started(2);
		arrange(game, { rank: "K", hands: [["KS"], ["QS", "QH"]] });
		playCards(game, 0, [0], NOW);

		expect(game.turn).toBe(1);
		expect(iceMovesFor(game, 1)).toEqual(["call"]);
		expect(playCards(game, 1, [0], NOW)).toBe("bad-play");
	});

	it("offers no call to the player leading a round, nor on their own play", () => {
		const game = started(2);
		arrange(game, { rank: "K", hands: [["KS", "KH"], ["QS"]] });
		expect(callLiar(game, 0, NOW)).toBe("cannot-call");
	});
});

describe("calling LIAR", () => {
	it("costs the caller a slot when every card was the rank or a joker", () => {
		const game = started(2);
		arrange(game, {
			rank: "K",
			hands: [
				["KS", "ZR", "QD"],
				["AS", "AH"],
			],
		});
		playCards(game, 0, [0, 1], NOW);
		expect(callLiar(game, 1, NOW)).toBeNull();

		expect(game.reveal).toEqual({
			caller: 1,
			liar: 0,
			cards: ["KS", "ZR"],
			truthful: true,
			victim: 1,
			slot: 1,
			flatlined: false,
		});
		expect(at(game, 1).used).toBe(1);
		expect(game.phase).toBe("reveal");
		expect(game.deadline).toBe(NOW + REVEAL_MS);
	});

	it("costs the liar a slot when any card was not", () => {
		const game = started(2);
		arrange(game, {
			rank: "K",
			hands: [
				["KS", "QS", "QD"],
				["AS", "AH"],
			],
		});
		playCards(game, 0, [0, 1], NOW);
		callLiar(game, 1, NOW);

		expect(game.reveal).toMatchObject({ truthful: false, victim: 0, slot: 1 });
		expect(at(game, 0).used).toBe(1);
	});

	it("flatlines a player on their black slot, and ends the game on the last one standing", () => {
		const game = started(2);
		arrange(game, {
			rank: "K",
			hands: [
				["QS", "QD"],
				["AS", "AH"],
			],
			deadly: [0, 5],
		});
		playCards(game, 0, [0], NOW);
		callLiar(game, 1, NOW);

		expect(game.reveal).toMatchObject({ victim: 0, slot: 1, flatlined: true });
		expect(at(game, 0).out).toBe(true);

		iceTimeout(game, NOW + REVEAL_MS);
		expect(game.phase).toBe("over");
		expect(game.winner).toBe(1);
	});

	it("tries the slots in turn, so the black one is found by the sixth at the latest", () => {
		const game = started(2);
		at(game, 1).deadly = 2;
		const outcomes: boolean[] = [];
		for (let round = 0; round < 3; round += 1) {
			arrange(game, { rank: "K", hands: [["KS", "QD"], ["AS"]], deadly: [5, 2] });
			playCards(game, 0, [0], NOW);
			callLiar(game, 1, NOW);
			outcomes.push((game.reveal as { flatlined: boolean }).flatlined);
			if (game.phase === "reveal") {
				iceTimeout(game, NOW + REVEAL_MS);
			}
		}

		expect(outcomes).toEqual([false, false, true]);
	});

	it("deals the next round after the reveal, led by whoever lost the call", () => {
		const game = started(3);
		arrange(game, { rank: "K", hands: [["QS", "QD"], ["AS", "AH"], ["KS"]] });
		playCards(game, 0, [0], NOW);
		callLiar(game, 1, NOW);
		const round = game.round;

		iceTimeout(game, NOW + REVEAL_MS);
		expect(game.phase).toBe("playing");
		expect(game.round).toBe(round + 1);
		expect(game.turn).toBe(0);
		expect(game.reveal).toBeNull();
		for (const seat of [0, 1, 2]) {
			expect(at(game, seat).hand).toHaveLength(HAND_SIZE);
		}
	});
});

describe("the clock and leaving", () => {
	it("lays the first card for a person whose clock runs out, or calls if that is all they can do", () => {
		const game = started(2);
		arrange(game, { rank: "K", hands: [["QS", "QD"], ["AS"]] });
		game.deadline = NOW + ICE_TURN_MS;
		iceTimeout(game, NOW + ICE_TURN_MS);
		expect(game.last).toEqual({ seat: 0, cards: ["QS"] });

		at(game, 0).hand = [];
		game.deadline = NOW;
		iceTimeout(game, NOW);
		expect(game.phase).toBe("reveal");
	});

	it("moves play on when the player on turn leaves, and their play can no longer be called", () => {
		const game = started(3);
		arrange(game, { rank: "K", hands: [["KS", "QD"], ["AS", "AH"], ["KD"]] });
		playCards(game, 0, [0], NOW);
		removeIcePlayer(game, 0, NOW);

		expect(game.last).toBeNull();
		expect(game.turn).toBe(1);
		expect(iceMovesFor(game, 1)).toEqual(["play"]);
	});

	it("hands the game to the last player left", () => {
		const game = started(2);
		removeIcePlayer(game, 0, NOW);
		expect(game.phase).toBe("over");
		expect(game.winner).toBe(1);
	});

	it("deals again when someone leaving leaves nobody anything to do", () => {
		const game = started(2);
		game.seats[2] = null;
		seatPlayer(game, 2, "Player C@Phoenix", "token-2");
		at(game, 2).out = false;
		at(game, 2).waiting = false;
		arrange(game, { rank: "K", hands: [[], ["KS"], []], turn: 2 });
		game.last = { seat: 1, cards: ["KS"] };
		at(game, 1).hand = [];
		removeIcePlayer(game, 1, NOW);

		expect(game.phase).toBe("playing");
		expect(iceMovesFor(game, game.turn)).toEqual(["play"]);
	});

	it("sits a player who joins mid-game out until the next one", () => {
		const game = started(2);
		seatPlayer(game, 2, "Late Comer@Omega", "token-late");
		expect(at(game, 2)).toMatchObject({ out: true, waiting: true, hand: [] });

		removeIcePlayer(game, 1, NOW);
		expect(game.phase).toBe("over");
		startGame(game, 0, NOW);
		expect(at(game, 2)).toMatchObject({ out: false, waiting: false });
		expect(at(game, 2).hand).toHaveLength(HAND_SIZE);
	});
});

describe("what a seat sees", () => {
	it("shows a seat its own hand, and of everyone else only how many cards they hold", () => {
		const game = started(3);
		arrange(game, {
			rank: "K",
			hands: [["KS", "QD"], ["AS", "AH", "ZR"], ["KD"]],
			deadly: [3, 4, 1],
		});
		const view = iceView(game, 0, NOW);
		const text = JSON.stringify(view);

		expect(view.hand).toEqual(["KS", "QD"]);
		expect(view.seats.map((seat) => seat?.cards)).toEqual([2, 3, 1, undefined]);
		for (const card of ['"AS"', '"AH"', '"ZR"', '"KD"']) {
			expect(text).not.toContain(card);
		}
		expect(text).not.toContain("deadly");
		expect(text).not.toContain("token");
	});

	it("shows how many cards were laid, but not which, until a call turns them over", () => {
		const game = started(2);
		arrange(game, { rank: "K", hands: [["QS", "QD"], ["AS"]] });
		playCards(game, 0, [0, 1], NOW);

		const laid = iceView(game, 1, NOW);
		expect(laid.last).toEqual({ seat: 0, count: 2 });
		expect(JSON.stringify(laid)).not.toContain('"QS"');

		callLiar(game, 1, NOW);
		expect(iceView(game, 1, NOW).reveal?.cards).toEqual(["QS", "QD"]);
	});
});
