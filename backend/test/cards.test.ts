import { env, runDurableObjectAlarm, runInDurableObject, SELF } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { CUT_CARDS, START_CHIPS } from "../src/cards/blackjack.js";
import {
	HALL_NAME,
	MAX_MESSAGE_CHARS,
	MAX_WATCHERS,
	MESSAGES_PER_WINDOW,
	PROTOCOL_VERSION,
	RECONNECT_GRACE_MS,
} from "../src/cards/protocol.js";

/**
 * The card hall: one hibernating Durable Object holding the card lobby and
 * every card table, and dealing at them.
 *
 * Pinned here, above all: no deck is ever sent a card it should not see. The
 * shoe stays in the hall, and the dealer's hole card is null in every view
 * until it is turned. Beyond that, the hall seats players, takes bets and
 * moves only from the player whose turn it is and only against the table as
 * it is now, runs the clocks on its alarm, keeps a dropped player's seat for
 * a minute, and guards its own budget the way the pool hall does.
 */

type Message = Record<string, unknown> & { t: string };

const hall = () => env.CARD_HALL.getByName(HALL_NAME);

/** A caller of its own for each connection: the connect limit counts per address. */
let callers = 0;
const newCaller = (): string => {
	callers += 1;
	return `198.51.${Math.floor(callers / 250)}.${(callers % 250) + 1}`;
};

/** A deck's end of a socket: everything it was sent, in order, and a way to wait for the next of a kind. */
class Deck {
	readonly inbox: Message[] = [];
	readonly all: Message[] = [];
	closed: { code: number; reason: string } | undefined;
	private wake: (() => void) | undefined;

	constructor(readonly ws: WebSocket) {
		ws.addEventListener("message", (event) => {
			const data = event.data as string;
			const message = data === "pong" ? { t: "pong" } : (JSON.parse(data) as Message);
			this.inbox.push(message);
			this.all.push(message);
			this.wake?.();
		});
		ws.addEventListener("close", (event) => {
			this.closed = { code: event.code, reason: event.reason };
			this.wake?.();
		});
	}

	send(message: unknown): void {
		this.ws.send(typeof message === "string" ? message : JSON.stringify(message));
	}

	/** The next message of type `t`, taking it and anything before it out of the inbox. */
	async next(t: string, timeoutMs = 2000): Promise<Message> {
		const deadline = Date.now() + timeoutMs;
		for (;;) {
			const index = this.inbox.findIndex((message) => message.t === t);
			if (index >= 0) {
				return this.inbox.splice(0, index + 1)[index] as Message;
			}

			const left = deadline - Date.now();
			if (left <= 0) {
				throw new Error(`No "${t}" message; got ${JSON.stringify(this.inbox.map((m) => m.t))}`);
			}

			await new Promise<void>((resolve) => {
				this.wake = resolve;
				setTimeout(resolve, left);
			});
		}
	}

	async waitClosed(timeoutMs = 2000): Promise<{ code: number; reason: string }> {
		const deadline = Date.now() + timeoutMs;
		while (this.closed === undefined && Date.now() < deadline) {
			await new Promise<void>((resolve) => {
				this.wake = resolve;
				setTimeout(resolve, deadline - Date.now());
			});
		}

		if (this.closed === undefined) {
			throw new Error("The socket stayed open.");
		}

		return this.closed;
	}

	/** Lets anything in flight arrive, for asserting that something did not. */
	async settle(ms = 150): Promise<void> {
		await new Promise((resolve) => setTimeout(resolve, ms));
	}
}

const open: Deck[] = [];

async function connect(name: string, resume?: string): Promise<{ deck: Deck; welcome: Message }> {
	const query = new URLSearchParams({ name, ...(resume === undefined ? {} : { resume }) });
	const response = await SELF.fetch(`https://example.com/v1/cards?${query}`, {
		headers: { upgrade: "websocket", "cf-connecting-ip": newCaller() },
	});
	expect(response.status).toBe(101);
	const ws = response.webSocket;
	if (ws === null) {
		throw new Error("No WebSocket in the response.");
	}

	ws.accept();
	const deck = new Deck(ws);
	open.push(deck);
	return { deck, welcome: await deck.next("welcome") };
}

/** Rewrites every stored table, as a test needs it: a stacked shoe, a clock run out. */
const rewrite = (change: (table: Record<string, unknown>) => void) =>
	runInDurableObject(hall(), (_instance, state) => {
		for (const row of state.storage.sql
			.exec<{ code: string; data: string }>("SELECT code, data FROM card_tables")
			.toArray()) {
			const table = JSON.parse(row.data) as Record<string, unknown>;
			change(table);
			state.storage.sql.exec(
				"UPDATE card_tables SET data = ? WHERE code = ?",
				JSON.stringify(table),
				row.code,
			);
		}
	});

/** The next cards off the shoe, then low cards enough to keep it clear of the cut card. */
const stackShoe = (cards: string[]) =>
	rewrite((table) => {
		table.shoe = [...cards, ...Array.from({ length: CUT_CARDS + 20 }, () => "2C")];
	});

const storedTables = () =>
	runInDurableObject(
		hall(),
		(_instance, state) =>
			state.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM card_tables").one().n,
	);

/** A deck at a table of its own, its first view of it taken. */
async function seated(
	name = "Solo Player@Phoenix",
	opening: Record<string, unknown> = { private: true },
) {
	const { deck, welcome } = await connect(name);
	deck.send({ t: "open", ...opening });
	const table = await deck.next("table");
	return { deck, table, token: welcome.token as string, code: table.code as string };
}

/** Two decks at one public table, neither yet bet. */
async function twoSeated() {
	const host = await seated("Host Player@Phoenix", {});
	const { deck: guest, welcome } = await connect("Guest Player@Phoenix");
	guest.send({ t: "join", code: host.code });
	expect(await guest.next("table")).toMatchObject({ seat: 1 });
	await host.deck.next("table");
	return { host: host.deck, guest, code: host.code, guestToken: welcome.token as string };
}

const seatsOf = (view: Message) =>
	view.seats as ({
		name: string;
		chips: number;
		bet: number;
		hands: { cards: string[]; result: string | null; paid: number }[];
	} | null)[];

beforeEach(async () => {
	await runInDurableObject(hall(), async (_instance, state) => {
		for (const socket of state.getWebSockets()) {
			socket.close(1000, "Test reset.");
		}
		state.storage.sql.exec("DELETE FROM card_tables");
		await state.storage.deleteAlarm();
	});
});

afterEach(async () => {
	for (const deck of open.splice(0)) {
		try {
			deck.ws.close(1000, "Test done.");
		} catch {
			// Already closed by the hall.
		}
	}
});

describe("connecting to the card hall", () => {
	it("asks for a WebSocket", async () => {
		const response = await SELF.fetch("https://example.com/v1/cards?name=ABC", {
			headers: { "cf-connecting-ip": newCaller() },
		});
		expect(response.status).toBe(426);
	});

	it("turns away anything but a character and home world, or a made-up seat token, before waking the hall", async () => {
		const character = encodeURIComponent("Wyn Talora@Phoenix");
		for (const query of [
			"name=",
			"name=toolongname",
			// Initials, which the pool hall still takes from older decks, were never a card player's name.
			"name=ABC",
			`name=${encodeURIComponent("Wyn Talora")}`,
			`name=${encodeURIComponent("wyn talora@Phoenix")}`,
			`name=${encodeURIComponent("Wyn@Phoenix")}`,
			`name=${character}&resume=nope`,
		]) {
			const response = await SELF.fetch(`https://example.com/v1/cards?${query}`, {
				headers: { upgrade: "websocket", "cf-connecting-ip": newCaller() },
			});
			expect(response.status).toBe(400);
		}
	});

	it("welcomes a deck with its protocol version, a seat token, the table limits and the lobby", async () => {
		const { welcome } = await connect("Wyn Talora@Phoenix");
		expect(welcome).toMatchObject({
			v: PROTOCOL_VERSION,
			name: "Wyn Talora@Phoenix",
			limits: { min: 10, max: 500, step: 10, start: START_CHIPS, seats: 5 },
			tables: [],
		});
		expect(welcome.token).toMatch(/^[0-9a-f-]{36}$/);
	});

	it("takes the names the game gives, apostrophes and hyphens included", async () => {
		const { welcome } = await connect("Ka-Nho Ul'ahn@Ragnarok");
		expect(welcome.name).toBe("Ka-Nho Ul'ahn@Ragnarok");
	});
});

describe("what a deck is sent", () => {
	it("never sends the dealer's hole card while it is down, nor the shoe", async () => {
		const { deck } = await seated();
		await stackShoe(["TS", "9H", "7C", "QD"]);
		deck.send({ t: "bet", amount: 100 });
		const view = await deck.next("table");

		expect(view).toMatchObject({
			phase: "playing",
			dealer: ["9H", null],
			turn: { seat: 0, hand: 0 },
		});
		expect(seatsOf(view)[0]?.hands[0]?.cards).toEqual(["TS", "7C"]);
		expect(view.moves).toEqual(["hit", "stand", "double"]);
		expect(view.shoe).toBeTypeOf("number");
		// Quoted, as a card is on the wire: a table code can have the same two letters in it.
		expect(JSON.stringify(view)).not.toContain('"QD"');
		expect(JSON.stringify(view)).not.toContain("token");
	});

	it("turns the hole card once the hand is over, and pays it", async () => {
		const { deck } = await seated();
		await stackShoe(["TS", "6H", "9C", "TD", "8H"]);
		deck.send({ t: "bet", amount: 100 });
		const dealt = await deck.next("table");

		deck.send({ t: "act", move: "stand", seq: dealt.seq });
		const done = await deck.next("table");
		expect(done).toMatchObject({ phase: "betting", dealer: ["6H", "TD", "8H"], left: null });
		expect(seatsOf(done)[0]).toMatchObject({
			chips: START_CHIPS + 100,
			hands: [expect.objectContaining({ result: "win", paid: 200 })],
		});
	});

	it("keeps the hole card from every seat at a shared table, not only the one on turn", async () => {
		const { host, guest } = await twoSeated();
		await stackShoe(["TS", "9S", "5D", "6C", "9H", "KD"]);
		host.send({ t: "bet", amount: 10 });
		await host.next("table");
		await guest.next("table");
		guest.send({ t: "bet", amount: 20 });

		for (const deck of [host, guest]) {
			const view = await deck.next("table");
			expect(view.dealer).toEqual(["5D", null]);
			expect(JSON.stringify(view)).not.toContain('"KD"');
		}
	});
});

describe("playing at a shared table", () => {
	it("lists a public table to everyone browsing, and who is at it, but never a private one", async () => {
		const { deck: browser } = await connect("Look Around@Omega");
		await seated("Private Table@Omega");
		await browser.settle();
		expect(browser.inbox.filter((message) => message.t === "lobby")).toEqual([]);

		const { code } = await seated("Host Player@Phoenix", {});
		const lobby = await browser.next("lobby");
		expect(lobby.tables).toEqual([
			{
				code,
				game: "blackjack",
				players: ["Host Player@Phoenix", null, null, null, null],
				playing: false,
				watchers: 0,
			},
		]);
	});

	it("deals once everyone seated has bet, and tells both seats whose turn it is", async () => {
		const { host, guest } = await twoSeated();
		await stackShoe(["TS", "9S", "5D", "6C", "9H", "KD"]);

		host.send({ t: "bet", amount: 10 });
		await host.next("table");
		const waiting = await guest.next("table");
		expect(waiting.phase).toBe("betting");
		expect(waiting.left).toBeGreaterThan(10_000);

		guest.send({ t: "bet", amount: 20 });
		const forHost = await host.next("table");
		const forGuest = await guest.next("table");
		expect(forHost).toMatchObject({
			phase: "playing",
			turn: { seat: 0, hand: 0 },
			moves: ["hit", "stand", "double"],
		});
		expect(forGuest).toMatchObject({ phase: "playing", turn: { seat: 0, hand: 0 }, moves: [] });
		expect(seatsOf(forGuest)[1]?.hands[0]?.cards).toEqual(["9S", "9H"]);
	});

	it("takes a move only from the player on turn, and only against the table as it is now", async () => {
		const { host, guest } = await twoSeated();
		await stackShoe(["TS", "9S", "5D", "6C", "9H", "KD", "2H"]);
		host.send({ t: "bet", amount: 10 });
		await guest.next("table");
		guest.send({ t: "bet", amount: 10 });
		const dealt = await guest.next("table");
		await host.next("table");
		await host.next("table");

		guest.send({ t: "act", move: "hit", seq: dealt.seq });
		expect(await guest.next("error")).toMatchObject({ code: "not-your-turn" });

		host.send({ t: "act", move: "hit", seq: dealt.seq });
		const hit = await host.next("table");
		expect(seatsOf(hit)[0]?.hands[0]?.cards).toEqual(["TS", "6C", "2H"]);

		// A second click on the view before the hit is stale, and is not a second hit.
		host.send({ t: "act", move: "hit", seq: dealt.seq });
		expect(await host.next("error")).toMatchObject({ code: "out-of-order" });
	});

	it("refuses a bet out of bounds or while a hand is being played", async () => {
		const { deck } = await seated();
		deck.send({ t: "bet", amount: 15 });
		expect(await deck.next("error")).toMatchObject({ code: "bad-bet" });

		await stackShoe(["TS", "9H", "7C", "QD"]);
		deck.send({ t: "bet", amount: 10 });
		await deck.next("table");
		deck.send({ t: "bet", amount: 10 });
		expect(await deck.next("error")).toMatchObject({ code: "not-betting" });
	});

	it("seats a player who joins during a hand for the next one", async () => {
		const host = await seated("Host Player@Phoenix", {});
		await stackShoe(["TS", "9H", "7C", "QD"]);
		host.deck.send({ t: "bet", amount: 10 });
		await host.deck.next("table");

		const { deck: late } = await connect("Late Comer@Omega");
		late.send({ t: "join", code: host.code });
		const view = await late.next("table");
		expect(view).toMatchObject({ seat: 1, phase: "playing", moves: [] });
		expect(seatsOf(view)[1]).toMatchObject({ name: "Late Comer@Omega", hands: [] });
	});

	it("refuses a sixth player", async () => {
		const host = await seated("Host Player@Phoenix", {});
		for (const name of [
			"Bea Second@Omega",
			"Cal Third@Omega",
			"Dee Fourth@Omega",
			"Eli Fifth@Omega",
		]) {
			const { deck } = await connect(name);
			deck.send({ t: "join", code: host.code });
			await deck.next("table");
		}

		const { deck: sixth } = await connect("Sixth Wheel@Omega");
		sixth.send({ t: "join", code: host.code });
		expect(await sixth.next("error")).toMatchObject({ code: "table-full" });
	});

	it("tops up a broke player, and only a broke one", async () => {
		const { deck } = await seated();
		deck.send({ t: "topup" });
		expect(await deck.next("error")).toMatchObject({ code: "has-chips" });

		await rewrite((table) => {
			(table.seats as [{ chips: number }])[0].chips = 0;
		});
		deck.send({ t: "topup" });
		expect(seatsOf(await deck.next("table"))[0]?.chips).toBe(START_CHIPS);
	});
});

describe("the clocks", () => {
	it("closes the bets for a player who has not bet, and deals the rest", async () => {
		const { host, guest } = await twoSeated();
		await stackShoe(["TS", "9H", "7C", "QD"]);
		host.send({ t: "bet", amount: 10 });
		await guest.next("table");

		await rewrite((table) => {
			table.deadline = Date.now() - 1;
		});
		expect(await runDurableObjectAlarm(hall())).toBe(true);
		const view = await guest.next("table");
		expect(view).toMatchObject({ phase: "playing", turn: { seat: 0, hand: 0 } });
		expect(seatsOf(view)[1]?.hands).toEqual([]);
	});

	it("stands the hand on turn when its player runs out of time", async () => {
		const { deck } = await seated();
		await stackShoe(["TS", "6H", "9C", "TD", "8H"]);
		deck.send({ t: "bet", amount: 10 });
		await deck.next("table");

		await rewrite((table) => {
			table.deadline = Date.now() - 1;
		});
		await runDurableObjectAlarm(hall());
		const view = await deck.next("table");
		expect(view).toMatchObject({ phase: "betting", dealer: ["6H", "TD", "8H"] });
		expect(seatsOf(view)[0]?.hands[0]?.cards).toEqual(["TS", "9C"]);
	});
});

describe("leaving and dropped connections", () => {
	it("plays on without a player who leaves mid-hand, and clears a table nobody is left at", async () => {
		const { host, guest } = await twoSeated();
		await stackShoe(["TS", "9S", "5D", "6C", "9H", "KD", "TH"]);
		host.send({ t: "bet", amount: 10 });
		await guest.next("table");
		guest.send({ t: "bet", amount: 10 });
		await guest.next("table");
		await host.next("table");
		await host.next("table");

		host.send({ t: "leave" });
		await host.next("lobby");
		const view = await guest.next("table");
		expect(seatsOf(view)[0]).toBeNull();
		expect(view).toMatchObject({
			turn: { seat: 1, hand: 0 },
			moves: ["hit", "stand", "double", "split"],
		});

		guest.send({ t: "leave" });
		await guest.next("lobby");
		expect(await storedTables()).toBe(0);
	});

	it("keeps a player's seat and chips while they reconnect", async () => {
		const { host, guest, guestToken } = await twoSeated();
		guest.send({ t: "bet", amount: 50 });
		await host.next("table");

		guest.ws.close(1000, "Lost the connection.");
		const seen = await host.next("table");
		expect(seatsOf(seen)[1]).toMatchObject({ away: true, bet: 50 });

		const { deck: back, welcome } = await connect("Guest Player@Phoenix", guestToken);
		expect(welcome.token).toBe(guestToken);
		expect(await back.next("table")).toMatchObject({ seat: 1 });
		expect(seatsOf(await host.next("table"))[1]).toMatchObject({
			away: false,
			chips: START_CHIPS - 50,
			bet: 50,
		});
	});

	it("gives up the seat of a player who does not come back in time", async () => {
		const { host, guest } = await twoSeated();
		guest.ws.close(1000, "Gone.");
		await host.next("table");

		await rewrite((table) => {
			for (const player of table.seats as ({ away: number | null } | null)[]) {
				if (player?.away != null) {
					player.away -= RECONNECT_GRACE_MS + 1000;
				}
			}
		});
		await runDurableObjectAlarm(hall());
		expect(seatsOf(await host.next("table"))[1]).toBeNull();
	});

	it("never hands a seat to a token the hall did not issue", async () => {
		await seated();
		const { deck, welcome } = await connect(
			"Sly Thief@Omega",
			"00000000-0000-4000-8000-000000000000",
		);
		expect(welcome.token).not.toBe("00000000-0000-4000-8000-000000000000");
		await deck.settle();
		expect(deck.inbox.filter((message) => message.t === "table")).toEqual([]);
	});
});

describe("the hall's own budget", () => {
	it("answers bad JSON and unknown messages without closing", async () => {
		const { deck } = await connect("Abe Cee@Omega");
		deck.send("not json");
		expect(await deck.next("error")).toMatchObject({ code: "bad-message" });
		deck.send({ t: "deal-me-aces" });
		expect(await deck.next("error")).toMatchObject({ code: "unknown-message" });
		expect(deck.closed).toBeUndefined();
	});

	it("closes a socket that sends an oversized frame", async () => {
		const { deck } = await connect("Abe Cee@Omega");
		deck.send("x".repeat(MAX_MESSAGE_CHARS + 1));
		expect((await deck.waitClosed()).code).toBe(1009);
	});

	it("closes a socket that floods", async () => {
		const { deck } = await connect("Abe Cee@Omega");
		for (let index = 0; index <= MESSAGES_PER_WINDOW; index += 1) {
			deck.send({ t: "sync" });
		}
		expect((await deck.waitClosed()).code).toBe(1008);
	});

	it("meters connections on a limit of its own", async () => {
		const caller = "192.0.2.77";
		const statuses: number[] = [];
		for (let attempt = 0; attempt < 11; attempt += 1) {
			const response = await SELF.fetch(
				`https://example.com/v1/cards?name=${encodeURIComponent("Abe Cee@Omega")}`,
				{
					headers: { upgrade: "websocket", "cf-connecting-ip": caller },
				},
			);
			statuses.push(response.status);
			response.webSocket?.accept();
			response.webSocket?.close(1000, "Done.");
		}

		expect(statuses.slice(0, 10).every((status) => status === 101)).toBe(true);
		expect(statuses[10]).toBe(429);
	});
});

describe("BLACK ICE at the card hall", () => {
	type IceSeat = { name: string; bot: boolean; cards: number; used: number; out: boolean } | null;
	const iceSeats = (view: Message) => view.seats as IceSeat[];

	/** Card names in a view: a rank (or Z for a joker) then a suit (or R or B for a joker). */
	const cardsIn = (view: Message) => JSON.stringify(view).match(/"[QKAZ][SHDCRB]"/g) ?? [];

	/** A host at a BLACK ICE table of their own, its first view taken. */
	async function iceTable(isPrivate = true) {
		const { deck } = await connect("Host Player@Phoenix");
		deck.send({ t: "open", private: isPrivate, game: "blackice" });
		const view = await deck.next("table");
		return { host: deck, code: view.code as string, view };
	}

	/** Two people at a started table, the round then set out by hand at sequence 100. */
	async function arrangedPair(hands: string[][], rank = "K") {
		const { host, code } = await iceTable();
		const { deck: guest } = await connect("Guest Player@Phoenix");
		guest.send({ t: "join", code });
		await guest.next("table");
		await host.next("table");
		host.send({ t: "start" });
		await host.next("table");
		await guest.next("table");
		await rewrite((table) => {
			const seats = table.seats as { hand: string[]; deadly: number }[];
			hands.forEach((hand, seat) => {
				(seats[seat] as { hand: string[]; deadly: number }).hand = hand;
				(seats[seat] as { hand: string[]; deadly: number }).deadly = 5;
			});
			table.rank = rank;
			table.turn = 0;
			table.seq = 100;
			table.last = null;
			table.pile = 0;
		});
		return { host, guest, code };
	}

	it("opens a table for the host, who fills seats with bots and starts the game", async () => {
		const { host, view } = await iceTable();
		expect(view).toMatchObject({ game: "blackice", phase: "waiting", host: 0, seat: 0, hand: [] });

		host.send({ t: "addbot" });
		await host.next("table");
		host.send({ t: "addbot" });
		const filled = await host.next("table");
		expect(iceSeats(filled).map((seat) => seat?.name ?? null)).toEqual([
			"Host Player@Phoenix",
			"Glitch@Ice",
			"Daemon@Ice",
			null,
		]);
		expect(iceSeats(filled)[1]).toMatchObject({ bot: true });

		host.send({ t: "start" });
		const dealt = await host.next("table");
		expect(dealt).toMatchObject({ phase: "playing", round: 1 });
		expect(["Q", "K", "A"]).toContain(dealt.rank);
		expect(dealt.hand).toHaveLength(5);
		expect(iceSeats(dealt).map((seat) => seat?.cards ?? null)).toEqual([5, 5, 5, null]);
	});

	it("sends each seat its own hand and nobody else's, and never where anyone's black ICE is", async () => {
		const { host } = await iceTable();
		host.send({ t: "addbot" });
		await host.next("table");
		host.send({ t: "addbot" });
		await host.next("table");
		host.send({ t: "start" });
		const dealt = await host.next("table");

		expect(cardsIn(dealt)).toHaveLength(5);
		expect(JSON.stringify(dealt)).not.toContain("deadly");
		expect(JSON.stringify(dealt)).not.toContain("token");
		for (const seat of iceSeats(dealt)) {
			expect(seat === null || !("hand" in seat)).toBe(true);
		}
	});

	it("takes a play and a call, and turns the cards over for every seat", async () => {
		const { host, guest } = await arrangedPair([
			["QS", "QD", "KS", "AS", "AH"],
			["KH", "KD", "ZR", "AC", "QC"],
		]);

		host.send({ t: "play", cards: [0, 1], seq: 100 });
		const laid = await guest.next("table");
		await host.next("table");
		expect(laid).toMatchObject({
			last: { seat: 0, count: 2 },
			turn: 1,
			pile: 2,
			moves: ["play", "call"],
		});
		expect(cardsIn(laid)).toHaveLength(5);

		guest.send({ t: "call", seq: laid.seq });
		for (const deck of [host, guest]) {
			const called = await deck.next("table");
			expect(called).toMatchObject({
				phase: "reveal",
				reveal: { caller: 1, liar: 0, cards: ["QS", "QD"], truthful: false, victim: 0, slot: 1 },
			});
			expect(iceSeats(called)[0]).toMatchObject({ used: 1, out: false });
		}
	});

	it("refuses a stale move, a start from anyone but the host, and another game's moves", async () => {
		const { host, guest } = await arrangedPair([["QS", "QD"], ["KH"]]);
		host.send({ t: "play", cards: [0], seq: 99 });
		expect(await host.next("error")).toMatchObject({ code: "out-of-order" });

		guest.send({ t: "start" });
		expect(await guest.next("error")).toMatchObject({ code: "not-host" });

		host.send({ t: "bet", amount: 10 });
		expect(await host.next("error")).toMatchObject({ code: "unknown-message" });
	});

	it("moves for a bot on the alarm, and deals the next round once a call's reveal is over", async () => {
		const { host } = await iceTable();
		host.send({ t: "addbot" });
		await host.next("table");
		host.send({ t: "start" });
		await host.next("table");
		await rewrite((table) => {
			const seats = table.seats as { hand: string[] }[];
			(seats[0] as { hand: string[] }).hand = ["QS", "QD", "QH", "AS", "AH"];
			(seats[1] as { hand: string[] }).hand = ["KS", "KH", "KD", "KC", "ZR", "ZB"];
			table.rank = "K";
			table.turn = 1;
			table.last = { seat: 0, cards: ["QS", "QD", "QH"] };
			table.pile = 3;
			table.deadline = Date.now() - 1;
		});

		expect(await runDurableObjectAlarm(hall())).toBe(true);
		const moved = await host.next("table");
		// Eight cards are true this round, six kings and two jokers. Holding six of them, the bot knows three more is a lie.
		expect(moved).toMatchObject({
			phase: "reveal",
			reveal: { caller: 1, liar: 0, truthful: false },
		});

		await rewrite((table) => {
			table.deadline = Date.now() - 1;
		});
		await runDurableObjectAlarm(hall());
		const next = await host.next("table");
		expect(next.phase === "playing" || next.phase === "over").toBe(true);
	});

	it("clears away a table with nobody left at it but bots", async () => {
		const { host } = await iceTable();
		host.send({ t: "addbot" });
		await host.next("table");
		host.send({ t: "leave" });
		await host.next("lobby");
		expect(await storedTables()).toBe(0);
	});

	it("lists a public table under its game", async () => {
		const { deck: browser } = await connect("Look Around@Omega");
		const { code } = await iceTable(false);
		const lobby = await browser.next("lobby");
		expect(lobby.tables).toEqual([
			{
				code,
				game: "blackice",
				players: ["Host Player@Phoenix", null, null, null],
				playing: false,
				watchers: 0,
			},
		]);
	});
});

describe("Hold'em at the card hall", () => {
	type HoldemSeat = {
		name: string;
		bot: boolean;
		chips: number;
		bet: number;
		cards: number;
		shown: string[] | null;
	} | null;
	const holdemSeats = (view: Message) => view.seats as HoldemSeat[];

	/** Card names in a view, ranks and suits as the hall writes them. */
	const cardsIn = (view: Message) => JSON.stringify(view).match(/"[2-9TJQKA][SHDC]"/g) ?? [];

	/** A host at a private Hold'em table with one bot, the first hand dealt with the host on the button. */
	async function dealtTable() {
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", private: true, game: "holdem" });
		const opened = await host.next("table");
		host.send({ t: "addbot" });
		const filled = await host.next("table");
		await rewrite((table) => {
			table.button = 1;
			table.deadline = Date.now() - 1;
		});
		expect(await runDurableObjectAlarm(hall())).toBe(true);
		const dealt = await host.next("table");
		return { host, opened, filled, dealt };
	}

	it("opens a table, deals once a bot sits down, and sends a seat only its own two cards", async () => {
		const { opened, filled, dealt } = await dealtTable();
		expect(opened).toMatchObject({
			game: "holdem",
			phase: "waiting",
			host: 0,
			seat: 0,
			blinds: [5, 10],
			start: 1000,
		});
		expect(holdemSeats(filled)[1]).toMatchObject({ name: "Chrome@Grid", bot: true, chips: 1000 });
		expect(filled.left).toBeGreaterThan(0);

		// Heads up, the host on the button posts the small blind and acts first.
		expect(dealt).toMatchObject({
			phase: "betting",
			street: "preflop",
			button: 0,
			small: 0,
			big: 1,
			turn: 0,
		});
		expect(dealt.hole).toHaveLength(2);
		expect(dealt.moves).toMatchObject({ check: false, call: 5 });
		expect(holdemSeats(dealt)[1]).toMatchObject({ cards: 2, shown: null, bet: 10 });
		expect(dealt).not.toHaveProperty("deck");
		expect(cardsIn(dealt).sort()).toEqual(
			(dealt.hole as string[]).map((card) => `"${card}"`).sort(),
		);
	});

	it("takes the player's move, refuses one made against an old view, and moves for the bot on the alarm", async () => {
		const { host, dealt } = await dealtTable();
		host.send({ t: "act", move: "call", seq: dealt.seq });
		const called = await host.next("table");
		expect(called).toMatchObject({ turn: 1 });
		expect(holdemSeats(called)[0]).toMatchObject({ bet: 10, chips: 990 });

		host.send({ t: "act", move: "check", seq: dealt.seq });
		expect(await host.next("error")).toMatchObject({ code: "out-of-order" });
		host.send({ t: "act", move: "shove", seq: called.seq });
		expect(await host.next("error")).toMatchObject({ code: "bad-message" });

		await rewrite((table) => {
			table.deadline = Date.now() - 1;
		});
		expect(await runDurableObjectAlarm(hall())).toBe(true);
		const moved = await host.next("table");
		// The bot checked, raised or folded its option, so the hand went on either way.
		expect(moved.seq).toBeGreaterThan(called.seq as number);
	});

	it("refuses a raise below the least, and a buy-back while the player still has chips", async () => {
		const { host, dealt } = await dealtTable();
		host.send({ t: "act", move: "raise", to: 15, seq: dealt.seq });
		expect(await host.next("error")).toMatchObject({ code: "bad-raise" });
		host.send({ t: "rebuy" });
		expect(await host.next("error")).toMatchObject({ code: "has-chips" });
		host.send({ t: "start" });
		expect(await host.next("error")).toMatchObject({ code: "unknown-message" });
	});

	it("deals the next hand with a player who comes back after dropping", async () => {
		const { deck: host, welcome } = await connect("Host Player@Phoenix");
		host.send({ t: "open", private: true, game: "holdem" });
		await host.next("table");
		host.send({ t: "addbot" });
		await host.next("table");

		// As the hall leaves a table whose only person dropped: their seat kept, the deal waiting on them.
		await rewrite((table) => {
			const seats = table.seats as { away: number | null }[];
			(seats[0] as { away: number | null }).away = Date.now();
			table.deadline = null;
		});

		const { deck: back } = await connect("Host Player@Phoenix", welcome.token as string);
		const view = await back.next("table");
		expect(view).toMatchObject({ phase: "waiting" });
		expect(view.left).toBeGreaterThan(0);
	});

	it("lists a public table under its game", async () => {
		const { deck: browser } = await connect("Look Around@Omega");
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", game: "holdem" });
		const view = await host.next("table");
		const lobby = await browser.next("lobby");
		expect(lobby.tables).toEqual([
			{
				code: view.code,
				game: "holdem",
				players: ["Host Player@Phoenix", null, null, null, null, null],
				playing: false,
				watchers: 0,
				blinds: [5, 10],
				start: 1000,
			},
		]);
	});

	it("opens a table at the stakes asked for, lists them, and refuses ones it does not offer", async () => {
		const { deck: browser } = await connect("Look Around@Omega");
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", game: "holdem", blinds: [10, 20], start: 30000 });
		expect(await host.next("error")).toMatchObject({ code: "bad-stakes" });
		host.send({ t: "open", game: "holdem", blinds: [25, 45], start: 5000 });
		expect(await host.next("error")).toMatchObject({ code: "bad-stakes" });
		expect(await storedTables()).toBe(0);

		host.send({ t: "open", game: "holdem", blinds: [25, 50], start: 5000 });
		const view = await host.next("table");
		expect(view).toMatchObject({ blinds: [25, 50], start: 5000 });
		expect(holdemSeats(view)[0]).toMatchObject({ chips: 5000 });
		const lobby = await browser.next("lobby");
		expect(lobby.tables).toMatchObject([
			{ code: view.code, game: "holdem", blinds: [25, 50], start: 5000 },
		]);

		// Stakes are Hold'em's alone: another game's table takes no notice of them.
		const { deck: other } = await connect("Other Host@Phoenix");
		other.send({ t: "open", private: true, game: "blackice", blinds: [1, 2], start: 7 });
		expect(await other.next("table")).toMatchObject({ game: "blackice", phase: "waiting" });
	});
});

describe("watching a card table", () => {
	/** Card names in a view, of any game: ranks and suits, jokers included. */
	const cardsIn = (view: Message) => JSON.stringify(view).match(/"[2-9TJQKAZ][SHDCRB]"/g) ?? [];

	/** A host at a Hold'em table with one bot, the first hand dealt, the host holding two cards. */
	async function dealtHoldem(isPrivate = true) {
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", private: isPrivate, game: "holdem" });
		const opened = await host.next("table");
		host.send({ t: "addbot" });
		await host.next("table");
		// The host on the button acts first, on a person's long clock, so no bot moves while a test looks on.
		await rewrite((table) => {
			table.button = 1;
			table.deadline = Date.now() - 1;
		});
		expect(await runDurableObjectAlarm(hall())).toBe(true);
		const dealt = await host.next("table");
		expect(dealt.hole).toHaveLength(2);
		expect(dealt.turn).toBe(0);
		return { host, code: opened.code as string, dealt };
	}

	/** The next lobby a deck is sent that lists a table with this many watching. */
	async function lobbyWatched(deck: Deck, code: string, watchers: number): Promise<Message> {
		for (;;) {
			const lobby = await deck.next("lobby");
			const tables = lobby.tables as { code: string; watchers: number }[];
			if (tables.some((table) => table.code === code && table.watchers === watchers)) {
				return lobby;
			}
		}
	}

	it("shows a watcher the table by its code, private or not, with nobody's cards in it", async () => {
		const { host, code } = await dealtHoldem();
		const { deck: watcher } = await connect("Rail Bird@Omega");
		watcher.send({ t: "watch", code: code.toLowerCase() });
		const view = await watcher.next("table");
		expect(view).toMatchObject({
			code,
			game: "holdem",
			seat: null,
			watchers: 1,
			hole: [],
			moves: null,
		});
		expect(view.phase).toBe("betting");
		// Preflop the board is empty, so nothing in the view names a card.
		expect(cardsIn(view)).toEqual([]);
		expect(JSON.stringify(view)).not.toContain("token");
		expect(JSON.stringify(view)).not.toContain("deck");

		// Everyone at the table sees one more watching.
		expect(await host.next("table")).toMatchObject({ seat: 0, watchers: 1 });
	});

	it("sends a watcher every change, but never the hand of anyone at a BLACK ICE table", async () => {
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", private: false, game: "blackice" });
		const { code } = await host.next("table");
		host.send({ t: "addbot" });
		await host.next("table");

		const { deck: watcher } = await connect("Rail Bird@Omega");
		watcher.send({ t: "watch", code });
		expect(await watcher.next("table")).toMatchObject({ phase: "waiting", seat: null });
		await host.next("table");

		host.send({ t: "start" });
		const dealt = await host.next("table");
		expect(dealt.hand).toHaveLength(5);
		const watched = await watcher.next("table");
		expect(watched).toMatchObject({
			phase: "playing",
			seat: null,
			hand: [],
			moves: [],
			seq: dealt.seq,
		});
		expect(cardsIn(watched)).toEqual([]);
		expect(JSON.stringify(watched)).not.toContain("deadly");
	});

	it("refuses a watcher's moves, and lets them take a seat that is free", async () => {
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", private: true, game: "blackice" });
		const { code } = await host.next("table");
		const { deck: watcher } = await connect("Rail Bird@Omega");
		watcher.send({ t: "watch", code });
		await watcher.next("table");
		await host.next("table");

		watcher.send({ t: "addbot" });
		expect(await watcher.next("error")).toMatchObject({ code: "not-seated" });
		watcher.send({ t: "open", private: true, game: "blackice" });
		expect(await watcher.next("table")).toMatchObject({ seat: 0, watchers: 0 });
		expect(await host.next("table")).toMatchObject({ seat: 0, watchers: 0 });
		watcher.send({ t: "leave" });
		await watcher.next("lobby");

		watcher.send({ t: "watch", code });
		await watcher.next("table");
		await host.next("table");
		watcher.send({ t: "join", code });
		const sat = await watcher.next("table");
		expect(sat).toMatchObject({ seat: 1, watchers: 0 });
		expect((sat.seats as ({ name: string } | null)[])[1]?.name).toBe("Rail Bird@Omega");
		watcher.send({ t: "watch", code });
		expect(await watcher.next("error")).toMatchObject({ code: "already-seated" });
	});

	it("counts a watcher off when they leave or drop, and lists the count in the lobby", async () => {
		const { deck: browser } = await connect("Look Around@Omega");
		const { host, code } = await dealtHoldem(false);
		await lobbyWatched(browser, code, 0);

		const { deck: watcher } = await connect("Rail Bird@Omega");
		watcher.send({ t: "watch", code });
		await watcher.next("table");
		expect(await host.next("table")).toMatchObject({ watchers: 1 });
		const listed = await lobbyWatched(browser, code, 1);
		expect(listed.tables).toMatchObject([{ code, game: "holdem", playing: true, watchers: 1 }]);

		watcher.send({ t: "leave" });
		expect(await watcher.next("lobby")).toMatchObject({ tables: [{ code, watchers: 0 }] });
		expect(await host.next("table")).toMatchObject({ watchers: 0 });

		const { deck: dropper } = await connect("Quick Look@Omega");
		dropper.send({ t: "watch", code });
		await dropper.next("table");
		expect(await host.next("table")).toMatchObject({ watchers: 1 });
		dropper.ws.close(1000, "Gone.");
		expect(await host.next("table")).toMatchObject({ watchers: 0 });
	});

	it("refuses an unknown code and a watcher too many, and takes watchers away when the table is cleared", async () => {
		const { deck: host } = await connect("Host Player@Phoenix");
		host.send({ t: "open", private: true, game: "blackjack" });
		const { code } = await host.next("table");

		const { deck: lost } = await connect("Lost Bird@Omega");
		lost.send({ t: "watch", code: "ZZZZ" });
		expect(await lost.next("error")).toMatchObject({ code: "no-table" });
		host.send({ t: "watch", code });
		expect(await host.next("error")).toMatchObject({ code: "already-seated" });

		const watchers: Deck[] = [];
		for (let index = 0; index < MAX_WATCHERS; index += 1) {
			const { deck } = await connect(`Rail Bird${String.fromCharCode(65 + index)}@Omega`);
			deck.send({ t: "watch", code });
			expect(await deck.next("table")).toMatchObject({ watchers: index + 1 });
			watchers.push(deck);
		}
		lost.send({ t: "watch", code });
		expect(await lost.next("error")).toMatchObject({ code: "no-room" });

		host.send({ t: "leave" });
		for (const deck of watchers) {
			expect(await deck.next("closed")).toMatchObject({ code, why: "empty" });
		}
		expect(await storedTables()).toBe(0);
	});
});

describe("wrangler configuration for the card hall", () => {
	const config = env.TEST_WRANGLER_CONFIG as unknown as {
		durable_objects?: { bindings: { name: string; class_name: string }[] };
		migrations?: { tag: string; new_sqlite_classes?: string[] }[];
		env?: Record<
			string,
			{ durable_objects?: { bindings: { name: string; class_name: string }[] } }
		>;
	};

	it("binds the hall in development and production", () => {
		const binding = { name: "CARD_HALL", class_name: "CardHall" };
		expect(config.durable_objects?.bindings).toContainEqual(binding);
		expect(config.env?.production?.durable_objects?.bindings).toContainEqual(binding);
	});

	it("creates it as a SQLite-backed class in a migration of its own, after the pool hall's", () => {
		const tags = config.migrations?.map((migration) => migration.tag);
		expect(tags).toEqual(["pool-hall-v1", "card-hall-v1"]);
		expect(config.migrations?.[1]?.new_sqlite_classes).toEqual(["CardHall"]);
	});
});
