import { env, runDurableObjectAlarm, runInDurableObject, SELF } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
	HALL_NAME,
	MAX_MESSAGE_CHARS,
	MAX_WATCHERS,
	MESSAGES_PER_WINDOW,
	RECONNECT_GRACE_MS,
} from "../src/pool/protocol.js";

/**
 * The pool hall: one hibernating Durable Object holding the lobby and every
 * table.
 *
 * Pinned here: the hall's part of a game is bookkeeping, not physics. It
 * checks whose turn it is and that a shot and its settled table arrive in
 * step, passes both to the opponent, and keeps the settled table so a
 * reconnecting player gets it back. Anyone watching is sent what the opponent
 * is and has no say in the game. And it protects its own budget: a socket
 * that floods or sends oversized frames is closed, and the connect route is
 * metered before the object is woken.
 */

type Message = Record<string, unknown> & { t: string };

const hall = () => env.POOL_HALL.getByName(HALL_NAME);

/** A caller of its own for each connection: the connect limit counts per address. */
let callers = 0;
const newCaller = (): string => {
	callers += 1;
	return `203.0.${Math.floor(callers / 250)}.${(callers % 250) + 1}`;
};

/** A deck's end of a socket: everything it was sent, in order, and a way to wait for the next of a kind. */
class Deck {
	/** Messages not yet taken by {@link next}. */
	readonly inbox: Message[] = [];
	/** Every message, taken or not. */
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

/**
 * A deck connecting as `name`. It says it speaks protocol 3, as a deck does
 * now; `version: null` is a deck from before free pyramid, which says nothing.
 */
async function connect(
	name: string,
	resume?: string,
	version: string | null = "3",
): Promise<{ deck: Deck; welcome: Message }> {
	const query = new URLSearchParams({
		name,
		...(version === null ? {} : { v: version }),
		...(resume === undefined ? {} : { resume }),
	});
	const response = await SELF.fetch(`https://example.com/v1/pool?${query}`, {
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

/** Two decks at one table, game started, seat 0 to break. `opening` goes into the host's `open`. */
async function startedTable(opening: Record<string, unknown> = {}): Promise<{
	host: Deck;
	guest: Deck;
	code: string;
	hostToken: string;
	guestToken: string;
}> {
	const { deck: host, welcome: hostWelcome } = await connect("HOST");
	host.send({ t: "open", ...opening });
	const table = await host.next("table");
	const code = table.code as string;
	const { deck: guest, welcome: guestWelcome } = await connect("GUEST");
	guest.send({ t: "join", code });
	await host.next("table");
	await guest.next("table");
	return {
		host,
		guest,
		code,
		hostToken: hostWelcome.token as string,
		guestToken: guestWelcome.token as string,
	};
}

/** A deck watching the table with this code, its first view of it taken. */
async function watching(code: string, name = "LOOK"): Promise<Deck> {
	const { deck } = await connect(name);
	deck.send({ t: "watch", code });
	expect(await deck.next("table")).toMatchObject({ code, seat: null, watching: true });
	return deck;
}

const storedTables = () =>
	runInDurableObject(
		hall(),
		(_instance, state) =>
			state.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM pool_tables").one().n,
	);

const shot = { a: 0.5, p: 0.8, sx: 0, sy: -0.2 };
const settled = (turn: 0 | 1, winner: 0 | 1 | null = null) => ({
	turn,
	winner,
	state: { balls: [[0, 0.6, 0.63]], groups: [0, 0], hand: false },
});

beforeEach(async () => {
	await runInDurableObject(hall(), async (_instance, state) => {
		for (const socket of state.getWebSockets()) {
			socket.close(1000, "Test reset.");
		}
		state.storage.sql.exec("DELETE FROM pool_tables");
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

describe("connecting to the pool hall", () => {
	it("asks for a WebSocket", async () => {
		const response = await SELF.fetch("https://example.com/v1/pool?name=ABC", {
			headers: { "cf-connecting-ip": newCaller() },
		});
		const body = (await response.json()) as { error: { code: string } };

		expect(response.status).toBe(426);
		expect(body.error.code).toBe("UPGRADE_REQUIRED");
	});

	it("turns away a bad name or a made-up seat token before waking the hall", async () => {
		for (const query of [
			"",
			"name=",
			"name=toolongname",
			"name=a%20b",
			"name=ABC&resume=not-a-token",
		]) {
			const response = await SELF.fetch(`https://example.com/v1/pool?${query}`, {
				headers: { upgrade: "websocket", "cf-connecting-ip": newCaller() },
			});

			expect(response.status, query).toBe(400);
		}
	});

	it("seats players as their character, and still takes the initials older decks send", async () => {
		const { welcome: character } = await connect("Ka-Nho Ul'ahn@Ragnarok");
		expect(character.name).toBe("Ka-Nho Ul'ahn@Ragnarok");
		const { welcome: initials } = await connect("ABC");
		expect(initials.name).toBe("ABC");
	});

	it("welcomes a deck with its protocol version, a seat token and the lobby", async () => {
		const { welcome } = await connect("ABC");

		// Protocol 3: free pyramid and the ball a shot strikes.
		expect(welcome.v).toBe(3);
		expect(welcome.name).toBe("ABC");
		expect(welcome.token).toMatch(/^[0-9a-f-]{36}$/);
		expect(welcome.tables).toEqual([]);
	});

	it("speaks protocol 2 to a deck that names none, and its own newest to one from the future", async () => {
		// A deck from before free pyramid says nothing, and plays on; one still on 1 sees 2 and asks to be updated.
		expect((await connect("OLD", undefined, null)).welcome.v).toBe(2);
		expect((await connect("ODD", undefined, "x")).welcome.v).toBe(2);
		expect((await connect("ONE", undefined, "1")).welcome.v).toBe(2);
		// A newer deck than the hall sees 3, and waits for the hall to be updated.
		expect((await connect("NEW", undefined, "4")).welcome.v).toBe(3);
	});

	it("answers a text ping without a message of its own", async () => {
		const { deck } = await connect("ABC");
		deck.send("ping");

		await deck.next("pong");
	});
});

describe("the lobby", () => {
	it("lists an open public table to everyone browsing, and lists it as a game to watch once joined", async () => {
		const { deck: browser } = await connect("LOOK");
		const { deck: host } = await connect("HOST");
		host.send({ t: "open" });

		const table = await host.next("table");
		expect(table).toMatchObject({
			status: "waiting",
			seat: 0,
			players: ["HOST", null],
			private: false,
		});
		expect(table.code).toMatch(/^[A-HJ-NP-Z]{4}$/);

		const listed = await browser.next("lobby");
		expect(listed.tables).toEqual([
			{
				code: table.code,
				host: "HOST",
				rules: "eight",
				assist: true,
				status: "waiting",
				players: ["HOST", null],
				watchers: 0,
			},
		]);

		const { deck: guest } = await connect("GUEST");
		guest.send({ t: "join", code: table.code });
		expect(await guest.next("table")).toMatchObject({
			status: "playing",
			seat: 1,
			turn: 0,
			players: ["HOST", "GUEST"],
		});
		expect((await browser.next("lobby")).tables).toEqual([
			{
				code: table.code,
				host: "HOST",
				rules: "eight",
				assist: true,
				status: "playing",
				players: ["HOST", "GUEST"],
				watchers: 0,
			},
		]);
	});

	it("keeps a public game in the lobby with its watchers while it is played, and not once it is over", async () => {
		const { deck: browser } = await connect("LOOK");
		const { host, guest, code } = await startedTable();
		await browser.next("lobby");
		expect((await browser.next("lobby")).tables).toMatchObject([{ code, status: "playing" }]);

		await watching(code, "FAN");
		expect((await browser.next("lobby")).tables).toEqual([
			{
				code,
				host: "HOST",
				rules: "eight",
				assist: true,
				status: "playing",
				players: ["HOST", "GUEST"],
				watchers: 1,
			},
		]);

		host.send({ t: "settle", seq: 0, ...settled(0, 0) });
		expect((await browser.next("lobby")).tables).toEqual([]);

		host.send({ t: "rematch" });
		guest.send({ t: "rematch" });
		expect((await browser.next("lobby")).tables).toMatchObject([
			{ code, status: "playing", watchers: 1 },
		]);
	});

	it("never lists a private table, which is still joined by its code in any case", async () => {
		const { deck: browser } = await connect("LOOK");
		const { deck: host } = await connect("HOST");
		host.send({ t: "open", private: true });
		const table = await host.next("table");
		expect(table.private).toBe(true);

		await browser.settle();
		expect(browser.inbox.filter((message) => message.t === "lobby")).toEqual([]);

		const { deck: guest } = await connect("GUEST");
		guest.send({ t: "join", code: (table.code as string).toLowerCase() });
		expect(await guest.next("table")).toMatchObject({ status: "playing", seat: 1 });
	});

	it("refuses an unknown code, a full table, and a second table for one deck", async () => {
		const { host, code } = await startedTable();

		host.send({ t: "open" });
		expect((await host.next("error")).code).toBe("already-seated");

		const { deck: third } = await connect("THIRD");
		third.send({ t: "join", code });
		expect((await third.next("error")).code).toBe("table-full");

		third.send({ t: "join", code: "ZZZZ" });
		expect((await third.next("error")).code).toBe("no-table");
	});
});

describe("playing a game", () => {
	it("passes the shooter's aim, shot and settled table to the opponent, in turn and in step", async () => {
		const { host, guest } = await startedTable();

		guest.send({ t: "shot", seq: 0, shot });
		expect((await guest.next("error")).code).toBe("not-your-turn");

		host.send({ t: "shot", seq: 3, shot });
		expect((await host.next("error")).code).toBe("out-of-order");

		host.send({ t: "shot", seq: 0, shot });
		expect(await guest.next("shot")).toMatchObject({ seq: 0, shot });

		host.send({ t: "settle", seq: 0, ...settled(1) });
		expect(await guest.next("settle")).toMatchObject({
			seq: 0,
			turn: 1,
			winner: null,
			state: settled(1).state,
		});

		// Now it is the guest's shot, numbered 1.
		host.send({ t: "shot", seq: 1, shot });
		expect((await host.next("error")).code).toBe("not-your-turn");
		guest.send({ t: "shot", seq: 1, shot });
		expect(await host.next("shot")).toMatchObject({ seq: 1, shot });
	});

	it("relays a cue preview, and drops a second one sent straight after it", async () => {
		const { host, guest } = await startedTable();
		host.send({ t: "aim", seq: 0, a: 1, p: 0.2, cx: 0.6, cy: 0.6 });
		host.send({ t: "aim", seq: 0, a: 1.1, p: 0.3 });
		host.send({ t: "shot", seq: 0, shot });
		await guest.next("shot");

		const previews = guest.all.filter((message) => message.t === "aim");
		expect(previews).toHaveLength(1);
		expect(previews[0]).toMatchObject({ seq: 0, a: 1, p: 0.2, cx: 0.6, cy: 0.6 });
	});

	it("never passes on the opponent's previews, which are not their turn to send", async () => {
		const { host, guest } = await startedTable();
		guest.send({ t: "aim", seq: 0, a: 1, p: 0.2 });
		host.send({ t: "shot", seq: 0, shot });
		await guest.next("shot");
		await host.settle();

		expect(host.all.filter((message) => message.t === "aim")).toEqual([]);
	});

	it("refuses a malformed shot or settled table", async () => {
		const { host } = await startedTable();

		host.send({ t: "shot", seq: 0, shot: { ...shot, p: 2 } });
		expect((await host.next("error")).code).toBe("bad-message");

		host.send({ t: "settle", seq: 0, turn: 2, winner: null, state: {} });
		expect((await host.next("error")).code).toBe("bad-message");

		host.send({ t: "settle", seq: 0, turn: 1, winner: null, state: { junk: "x".repeat(3000) } });
		expect((await host.next("error")).code).toBe("bad-message");
	});

	it("ends the game on a winning table, and racks a new one when both want a rematch, the other player breaking", async () => {
		const { host, guest } = await startedTable();
		const first = await runInDurableObject(hall(), (_instance, state) =>
			state.storage.sql.exec<{ data: string }>("SELECT data FROM pool_tables").one(),
		);
		const firstSeed = (JSON.parse(first.data) as { seed: number }).seed;

		host.send({ t: "settle", seq: 0, ...settled(0, 0) });
		expect(await host.next("table")).toMatchObject({ status: "over", winner: 0, forfeit: false });
		expect(await guest.next("table")).toMatchObject({ status: "over", winner: 0 });

		host.send({ t: "rematch" });
		expect(await guest.next("table")).toMatchObject({ status: "over", rematch: [true, false] });

		guest.send({ t: "rematch" });
		const rack = await guest.next("table");
		expect(rack).toMatchObject({
			status: "playing",
			game: 2,
			turn: 1,
			seq: 0,
			winner: null,
			state: null,
		});
		expect(rack.seed).not.toBe(firstSeed);
	});

	it("refuses a rematch while the game is still being played", async () => {
		const { host } = await startedTable();
		host.send({ t: "rematch" });

		expect((await host.next("error")).code).toBe("not-over");
	});

	it("sends a deck its table again when it asks, and only to that deck", async () => {
		const { host, guest } = await startedTable();
		host.send({ t: "settle", seq: 0, ...settled(1) });
		await guest.next("settle");

		guest.send({ t: "sync" });
		expect(await guest.next("table")).toMatchObject({
			seat: 1,
			seq: 1,
			turn: 1,
			state: settled(1).state,
		});
		await host.settle();
		expect(host.inbox.filter((message) => message.t === "table")).toEqual([]);
	});

	it("hands the game to the opponent when a player leaves the table", async () => {
		const { host, guest } = await startedTable();
		guest.send({ t: "leave" });

		expect(await guest.next("lobby")).toBeDefined();
		expect(await host.next("table")).toMatchObject({
			status: "over",
			winner: 0,
			forfeit: true,
			players: ["HOST", null],
		});
	});
});

describe("table rules", () => {
	it("opens a 9-ball table when asked and an 8-ball one when not, and lists each with its game", async () => {
		const { deck: browser } = await connect("LOOK");
		const { deck: nine } = await connect("NINE");
		nine.send({ t: "open", rules: "nine" });
		const nineTable = await nine.next("table");
		expect(nineTable).toMatchObject({ rules: "nine", status: "waiting", seat: 0, watchers: 0 });
		expect((await browser.next("lobby")).tables).toEqual([
			{
				code: nineTable.code,
				host: "NINE",
				rules: "nine",
				assist: true,
				status: "waiting",
				players: ["NINE", null],
				watchers: 0,
			},
		]);

		const { deck: eight } = await connect("EIGHT");
		eight.send({ t: "open" });
		const eightTable = await eight.next("table");
		expect(eightTable.rules).toBe("eight");

		const tables = (await browser.next("lobby")).tables;
		expect(tables).toHaveLength(2);
		expect(tables).toEqual(
			expect.arrayContaining([
				expect.objectContaining({ code: nineTable.code, rules: "nine" }),
				expect.objectContaining({ code: eightTable.code, rules: "eight" }),
			]),
		);
	});

	it("refuses a game it does not know, and opens no table for it", async () => {
		const { deck } = await connect("ABC");
		for (const rules of ["snooker", "NINE", "PYRAMID", 9, null, true]) {
			deck.send({ t: "open", rules });
			expect((await deck.next("error")).code, String(rules)).toBe("bad-message");
		}
		expect(await storedTables()).toBe(0);

		deck.send({ t: "open", rules: "eight" });
		expect(await deck.next("table")).toMatchObject({ rules: "eight", seat: 0 });
	});

	it("keeps a 9-ball table's game through a rematch", async () => {
		const { host, guest } = await startedTable({ rules: "nine" });
		host.send({ t: "settle", seq: 0, ...settled(0, 0) });
		await guest.next("table");
		host.send({ t: "rematch" });
		guest.send({ t: "rematch" });

		expect(await guest.next("table")).toMatchObject({ status: "over", rematch: [true, false] });
		expect(await guest.next("table")).toMatchObject({ status: "playing", game: 2, rules: "nine" });
	});

	it("reads a table stored before 9-ball as an 8-ball table", async () => {
		const { deck: host } = await connect("HOST");
		host.send({ t: "open", rules: "nine" });
		const table = await host.next("table");
		await runInDurableObject(hall(), (_instance, state) => {
			state.storage.sql.exec("UPDATE pool_tables SET data = json_remove(data, '$.rules')");
		});

		const { deck: guest, welcome } = await connect("GUEST");
		expect(welcome.tables).toMatchObject([{ code: table.code, rules: "eight" }]);
		guest.send({ t: "join", code: table.code });
		expect(await guest.next("table")).toMatchObject({ rules: "eight", status: "playing" });
	});
});

describe("free pyramid", () => {
	it("opens a pyramid table for a deck that knows the game, and lists it only to decks that do", async () => {
		const { deck: browser } = await connect("LOOK");
		const { deck: older } = await connect("OLDY", undefined, null);
		const { deck: host } = await connect("HOST");
		host.send({ t: "open", rules: "pyramid" });
		const table = await host.next("table");
		expect(table).toMatchObject({ rules: "pyramid", status: "waiting", seat: 0 });
		expect((await browser.next("lobby")).tables).toMatchObject([
			{ code: table.code, rules: "pyramid", status: "waiting" },
		]);
		expect((await older.next("lobby")).tables).toEqual([]);

		// An 8-ball table beside it is listed to both.
		const { deck: eight } = await connect("EIGHT");
		eight.send({ t: "open" });
		const eightTable = await eight.next("table");
		expect((await older.next("lobby")).tables).toMatchObject([
			{ code: eightTable.code, rules: "eight" },
		]);
		expect((await browser.next("lobby")).tables).toHaveLength(2);
		expect((await connect("LATE", undefined, null)).welcome.tables).toMatchObject([
			{ code: eightTable.code },
		]);
		expect((await connect("LATER")).welcome.tables).toHaveLength(2);
	});

	it("tells a deck from before free pyramid to update, rather than seat it at a pyramid table or let it open one", async () => {
		const { deck: host } = await connect("HOST");
		host.send({ t: "open", rules: "pyramid", private: true });
		const table = await host.next("table");

		const { deck: older } = await connect("OLDY", undefined, null);
		older.send({ t: "join", code: table.code });
		expect(await older.next("error")).toMatchObject({ code: "needs-update" });
		older.send({ t: "watch", code: table.code });
		expect((await older.next("error")).code).toBe("needs-update");
		older.send({ t: "open", rules: "pyramid" });
		expect((await older.next("error")).code).toBe("bad-message");
		expect(await storedTables()).toBe(1);

		const { deck: guest } = await connect("GUEST");
		guest.send({ t: "join", code: table.code });
		expect(await guest.next("table")).toMatchObject({
			rules: "pyramid",
			status: "playing",
			seat: 1,
		});
	});

	it("lets a deck from before free pyramid play 8-ball against one that knows it", async () => {
		const { deck: older } = await connect("OLDY", undefined, null);
		older.send({ t: "open" });
		const table = await older.next("table");
		const { deck: newer } = await connect("NEWY");
		newer.send({ t: "join", code: table.code });
		await older.next("table");
		expect(await newer.next("table")).toMatchObject({ rules: "eight", status: "playing" });

		older.send({ t: "shot", seq: 0, shot });
		expect((await newer.next("shot")).shot).toEqual(shot);
	});

	it("passes on the ball a shot strikes, with its preview, and leaves it out for the cue ball", async () => {
		const { host, guest } = await startedTable({ rules: "pyramid" });
		host.send({ t: "aim", seq: 0, a: 1, p: 0.2, s: 7 });
		expect(await guest.next("aim")).toMatchObject({ seq: 0, a: 1, p: 0.2, s: 7 });

		host.send({ t: "shot", seq: 0, shot: { ...shot, s: 7 } });
		expect((await guest.next("shot")).shot).toEqual({ ...shot, s: 7 });

		host.send({ t: "settle", seq: 0, ...settled(1) });
		await guest.next("settle");
		guest.send({ t: "shot", seq: 1, shot });
		const plain = (await host.next("shot")).shot as Record<string, unknown>;
		expect(plain).toEqual(shot);
		expect(Object.keys(plain)).not.toContain("s");
	});

	it("refuses a shot striking a ball the table does not have, and drops such a preview", async () => {
		const { host, guest } = await startedTable({ rules: "pyramid" });
		for (const s of [16, -1, 2.5, "3", null]) {
			host.send({ t: "shot", seq: 0, shot: { ...shot, s } });
			expect((await host.next("error")).code, String(s)).toBe("bad-message");
		}

		host.send({ t: "aim", seq: 0, a: 1, p: 0.2, s: 16 });
		host.send({ t: "aim", seq: 0, a: 1.1, p: 0.3, s: 15 });
		host.send({ t: "shot", seq: 0, shot: { ...shot, s: 15 } });
		await guest.next("shot");

		const previews = guest.all.filter((message) => message.t === "aim");
		expect(previews).toHaveLength(1);
		expect(previews[0]).toMatchObject({ a: 1.1, s: 15 });
	});

	it("keeps a pyramid table's game through a rematch", async () => {
		const { host, guest } = await startedTable({ rules: "pyramid" });
		host.send({ t: "settle", seq: 0, ...settled(0, 0) });
		await guest.next("table");
		host.send({ t: "rematch" });
		guest.send({ t: "rematch" });

		expect(await guest.next("table")).toMatchObject({ status: "over", rematch: [true, false] });
		expect(await guest.next("table")).toMatchObject({
			status: "playing",
			game: 2,
			rules: "pyramid",
		});
	});
});

describe("aim assist", () => {
	it("opens a table with aim assist unless asked not to", async () => {
		const { deck: browser } = await connect("LOOK");
		const { deck: host } = await connect("HOST");
		host.send({ t: "open" });

		expect(await host.next("table")).toMatchObject({ assist: true, seat: 0 });
		expect((await browser.next("lobby")).tables).toMatchObject([{ host: "HOST", assist: true }]);
	});

	it("carries a table opened without it to the lobby, the players, watchers and every rematch", async () => {
		const { deck: browser } = await connect("LOOK");
		const { host, guest, code } = await startedTable({ assist: false });
		expect((await browser.next("lobby")).tables).toMatchObject([{ code, assist: false }]);

		const { deck: watcher } = await connect("FAN");
		watcher.send({ t: "watch", code });
		expect(await watcher.next("table")).toMatchObject({ watching: true, assist: false });
		expect(await host.next("table")).toMatchObject({ seat: 0, assist: false });
		expect(await guest.next("table")).toMatchObject({ seat: 1, assist: false });

		host.send({ t: "settle", seq: 0, ...settled(0, 0) });
		await guest.next("table");
		host.send({ t: "rematch" });
		guest.send({ t: "rematch" });
		await guest.next("table");
		expect(await guest.next("table")).toMatchObject({ status: "playing", game: 2, assist: false });

		await browser.settle();
		const lobbies = browser.inbox.filter((message) => message.t === "lobby");
		expect(lobbies[lobbies.length - 1]?.tables).toMatchObject([
			{ code, status: "playing", assist: false },
		]);
	});

	it("refuses an assist that is not true or false, and opens no table for it", async () => {
		const { deck } = await connect("ABC");
		for (const assist of ["no", "true", 0, 1, null]) {
			deck.send({ t: "open", assist });
			expect((await deck.next("error")).code, String(assist)).toBe("bad-message");
		}
		expect(await storedTables()).toBe(0);

		deck.send({ t: "open", assist: false });
		expect(await deck.next("table")).toMatchObject({ assist: false, seat: 0 });
	});

	it("reads a table stored without assist as one with it", async () => {
		const { deck: host } = await connect("HOST");
		host.send({ t: "open", assist: false });
		const table = await host.next("table");
		await runInDurableObject(hall(), (_instance, state) => {
			state.storage.sql.exec("UPDATE pool_tables SET data = json_remove(data, '$.assist')");
		});

		const { deck: guest, welcome } = await connect("GUEST");
		expect(welcome.tables).toMatchObject([{ code: table.code, assist: true }]);
		guest.send({ t: "join", code: table.code });
		expect(await guest.next("table")).toMatchObject({ assist: true, status: "playing" });
	});
});

describe("called pockets", () => {
	it("passes a called pocket on with a cue preview and a shot, and leaves it out when none is called", async () => {
		const { host, guest } = await startedTable();
		host.send({ t: "aim", seq: 0, a: 1, p: 0.2, k: 0 });
		expect(await guest.next("aim")).toMatchObject({ seq: 0, a: 1, p: 0.2, k: 0 });

		host.send({ t: "shot", seq: 0, shot: { ...shot, k: 5 } });
		expect((await guest.next("shot")).shot).toEqual({ ...shot, k: 5 });

		host.send({ t: "settle", seq: 0, ...settled(1) });
		await guest.next("settle");
		guest.send({ t: "shot", seq: 1, shot });
		const plain = (await host.next("shot")).shot as Record<string, unknown>;
		expect(plain).toEqual(shot);
		expect(Object.keys(plain)).not.toContain("k");
	});

	it("refuses a shot calling a pocket the table does not have, and drops such a preview", async () => {
		const { host, guest } = await startedTable();
		for (const k of [6, -1, 1.5, "2", null]) {
			host.send({ t: "shot", seq: 0, shot: { ...shot, k } });
			expect((await host.next("error")).code, String(k)).toBe("bad-message");
		}

		// A dropped preview does not hold up the next one.
		host.send({ t: "aim", seq: 0, a: 1, p: 0.2, k: 6 });
		host.send({ t: "aim", seq: 0, a: 1.1, p: 0.3, k: 1 });
		host.send({ t: "shot", seq: 0, shot });
		await guest.next("shot");

		const previews = guest.all.filter((message) => message.t === "aim");
		expect(previews).toHaveLength(1);
		expect(previews[0]).toMatchObject({ a: 1.1, k: 1 });
		expect(guest.all.filter((message) => message.t === "shot")).toHaveLength(1);
	});
});

describe("watching a game", () => {
	it("sends a watcher the table and everything the shooter sends, and tells the players", async () => {
		const { host, guest, code, hostToken, guestToken } = await startedTable();
		const { deck: watcher } = await connect("LOOK");
		watcher.send({ t: "watch", code: ` ${code.toLowerCase()} ` });

		const view = await watcher.next("table");
		expect(view).toMatchObject({
			code,
			seat: null,
			watching: true,
			watchers: 1,
			rules: "eight",
			status: "playing",
			players: ["HOST", "GUEST"],
			turn: 0,
		});
		expect(JSON.stringify(view)).not.toContain(hostToken);
		expect(JSON.stringify(view)).not.toContain(guestToken);
		expect(await host.next("table")).toMatchObject({ seat: 0, watching: false, watchers: 1 });
		expect(await guest.next("table")).toMatchObject({ seat: 1, watching: false, watchers: 1 });

		host.send({ t: "aim", seq: 0, a: 1, p: 0.2, k: 2 });
		expect(await watcher.next("aim")).toMatchObject({ seq: 0, a: 1, p: 0.2, k: 2 });
		host.send({ t: "shot", seq: 0, shot });
		expect(await watcher.next("shot")).toMatchObject({ seq: 0, shot });
		host.send({ t: "settle", seq: 0, ...settled(1) });
		expect(await watcher.next("settle")).toMatchObject({
			seq: 0,
			turn: 1,
			winner: null,
			state: settled(1).state,
		});

		// Exactly what the opponent was sent.
		await guest.next("settle");
		const relayed = (deck: Deck) =>
			deck.all.filter((message) => ["aim", "shot", "settle"].includes(message.t));
		expect(relayed(watcher)).toEqual(relayed(guest));
	});

	it("lets a deck watch an open table or a private one, by its code", async () => {
		const { deck: browser } = await connect("BROWSE");
		const { deck: host } = await connect("HOST");
		host.send({ t: "open", private: true });
		const table = await host.next("table");
		const code = table.code as string;

		const watcher = await watching(code);
		expect(await host.next("table")).toMatchObject({ status: "waiting", watchers: 1 });

		const { deck: guest } = await connect("GUEST");
		guest.send({ t: "join", code });
		expect(await watcher.next("table")).toMatchObject({
			status: "playing",
			private: true,
			seat: null,
			watching: true,
			players: ["HOST", "GUEST"],
		});

		await browser.settle();
		expect(browser.inbox.filter((message) => message.t === "lobby")).toEqual([]);
	});

	it("gives a watcher no say in the game, and no second table while it watches", async () => {
		const { host, guest, code } = await startedTable();
		const watcher = await watching(code);

		for (const message of [
			{ t: "shot", seq: 0, shot },
			{ t: "settle", seq: 0, ...settled(1) },
			{ t: "rematch" },
		]) {
			watcher.send(message);
			expect((await watcher.next("error")).code, message.t).toBe("not-seated");
		}

		for (const message of [{ t: "open" }, { t: "join", code }, { t: "watch", code }]) {
			watcher.send(message);
			expect((await watcher.next("error")).code, message.t).toBe("already-seated");
		}

		watcher.send({ t: "aim", seq: 0, a: 1, p: 0.2 });
		watcher.send({ t: "sync" });
		expect(await watcher.next("table")).toMatchObject({
			code,
			seat: null,
			watching: true,
			watchers: 1,
			status: "playing",
			seq: 0,
		});

		await host.settle();
		expect([...host.all, ...guest.all].filter((message) => message.t === "aim")).toEqual([]);
		expect(await storedTables()).toBe(1);
	});

	it("takes a watcher back to the lobby when it leaves, without touching the game", async () => {
		const { host, guest, code } = await startedTable();
		const watcher = await watching(code);
		await host.next("table");
		await guest.next("table");

		watcher.send({ t: "leave" });
		expect((await watcher.next("lobby")).tables).toEqual([
			{
				code,
				host: "HOST",
				rules: "eight",
				assist: true,
				status: "playing",
				players: ["HOST", "GUEST"],
				watchers: 0,
			},
		]);
		expect(await host.next("table")).toMatchObject({
			status: "playing",
			players: ["HOST", "GUEST"],
			away: [false, false],
			forfeit: false,
			watchers: 0,
		});
		expect(await guest.next("table")).toMatchObject({ status: "playing", watchers: 0 });

		host.send({ t: "shot", seq: 0, shot });
		await guest.next("shot");
		await watcher.settle();
		expect(watcher.inbox.filter((message) => message.t === "shot")).toEqual([]);

		watcher.send({ t: "open" });
		expect(await watcher.next("table")).toMatchObject({ seat: 0, status: "waiting" });
	});

	it("counts a watcher gone when its connection drops", async () => {
		const { host, guest, code } = await startedTable();
		const watcher = await watching(code);
		await host.next("table");
		await guest.next("table");

		watcher.ws.close(1000, "Gone.");
		expect(await host.next("table")).toMatchObject({ status: "playing", watchers: 0 });
		expect(await guest.next("table")).toMatchObject({ watchers: 0 });
	});

	it(`turns away a watcher past the first ${MAX_WATCHERS}`, async () => {
		const { host, code } = await startedTable();
		for (let index = 0; index < MAX_WATCHERS; index += 1) {
			await watching(code, `FAN${index}`);
		}

		const { deck: late } = await connect("LATE");
		late.send({ t: "watch", code });
		expect((await late.next("error")).code).toBe("no-room");

		// One view for each watcher who got in, and none for the one who did not.
		await host.settle();
		const counts = host.inbox
			.filter((message) => message.t === "table")
			.map((message) => message.watchers);
		expect(counts).toEqual(Array.from({ length: MAX_WATCHERS }, (_, index) => index + 1));
	});

	it("refuses to watch a table that is not there", async () => {
		const { deck } = await connect("LOOK");
		deck.send({ t: "watch", code: "ZZZZ" });
		expect((await deck.next("error")).code).toBe("no-table");

		deck.send({ t: "watch", code: 7 });
		expect((await deck.next("error")).code).toBe("no-table");
	});

	it("sends watchers back to the lobby once both players have left", async () => {
		const { host, guest, code } = await startedTable();
		const watcher = await watching(code);

		host.send({ t: "leave" });
		expect(await watcher.next("table")).toMatchObject({
			status: "over",
			winner: 1,
			forfeit: true,
			players: [null, "GUEST"],
		});

		guest.send({ t: "leave" });
		expect(await watcher.next("closed")).toEqual({ t: "closed", code, why: "empty" });
		expect((await watcher.next("lobby")).tables).toEqual([]);
		expect(await storedTables()).toBe(0);

		watcher.send({ t: "open" });
		expect(await watcher.next("table")).toMatchObject({ seat: 0, status: "waiting" });
	});

	it("tells players and watchers alike when an idle table is cleared away", async () => {
		const { host, guest, code } = await startedTable();
		const watcher = await watching(code);
		await runInDurableObject(hall(), (_instance, state) => {
			state.storage.sql.exec("UPDATE pool_tables SET data = json_set(data, '$.updated', 0)");
		});

		expect(await runDurableObjectAlarm(hall())).toBe(true);
		for (const deck of [host, guest, watcher]) {
			expect(await deck.next("closed")).toEqual({ t: "closed", code, why: "idle" });
			expect((await deck.next("lobby")).tables).toEqual([]);
		}
	});
});

describe("dropped connections", () => {
	it("keeps a player's seat while they reconnect, with the table as it was", async () => {
		const { host, guest, guestToken } = await startedTable();
		host.send({ t: "shot", seq: 0, shot });
		host.send({ t: "settle", seq: 0, ...settled(1) });
		await guest.next("settle");

		guest.ws.close(1000, "Lost the connection.");
		expect(await host.next("table")).toMatchObject({ status: "playing", away: [false, true] });

		const { deck: back, welcome } = await connect("GUEST", guestToken);
		expect(welcome.token).toBe(guestToken);
		expect(await back.next("table")).toMatchObject({
			seat: 1,
			status: "playing",
			seq: 1,
			turn: 1,
			state: settled(1).state,
		});
		expect(await host.next("table")).toMatchObject({ away: [false, false] });

		back.send({ t: "shot", seq: 1, shot });
		expect(await host.next("shot")).toMatchObject({ seq: 1 });
	});

	it("gives the game to the player who stayed once the grace period runs out", async () => {
		const { host, guest } = await startedTable();
		guest.ws.close(1000, "Gone.");
		await host.next("table");

		await runInDurableObject(hall(), (_instance, state) => {
			for (const row of state.storage.sql
				.exec<{ code: string; data: string }>("SELECT code, data FROM pool_tables")
				.toArray()) {
				const table = JSON.parse(row.data) as { chairs: ({ away: number | null } | null)[] };
				for (const chair of table.chairs) {
					if (chair?.away != null) {
						chair.away -= RECONNECT_GRACE_MS + 1000;
					}
				}
				state.storage.sql.exec(
					"UPDATE pool_tables SET data = ? WHERE code = ?",
					JSON.stringify(table),
					row.code,
				);
			}
		});

		expect(await runDurableObjectAlarm(hall())).toBe(true);
		expect(await host.next("table")).toMatchObject({ status: "over", winner: 0, forfeit: true });
	});

	it("takes an open table off the lobby while its host is away, and clears it when they do not return", async () => {
		const { deck: browser } = await connect("LOOK");
		const { deck: host } = await connect("HOST");
		host.send({ t: "open" });
		await browser.next("lobby");

		host.ws.close(1000, "Gone.");
		expect((await browser.next("lobby")).tables).toEqual([]);

		await runInDurableObject(hall(), (_instance, state) => {
			state.storage.sql.exec("UPDATE pool_tables SET data = json_set(data, '$.chairs[0].away', 1)");
		});
		await runDurableObjectAlarm(hall());

		const remaining = await runInDurableObject(
			hall(),
			(_instance, state) =>
				state.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM pool_tables").one().n,
		);
		expect(remaining).toBe(0);
	});

	it("never hands a seat to a token the hall did not issue", async () => {
		await startedTable();
		const { deck, welcome } = await connect("THIEF", "00000000-0000-4000-8000-000000000000");

		expect(welcome.token).not.toBe("00000000-0000-4000-8000-000000000000");
		await deck.settle();
		expect(deck.inbox.filter((message) => message.t === "table")).toEqual([]);
	});
});

describe("the hall's own budget", () => {
	it("keeps tables in storage, so a hibernating hall loses nothing", async () => {
		const { code } = await startedTable();
		const stored = await runInDurableObject(hall(), (_instance, state) =>
			state.storage.sql.exec<{ code: string }>("SELECT code FROM pool_tables").toArray(),
		);

		expect(stored).toEqual([{ code }]);
	});

	it("answers bad JSON and unknown messages without closing", async () => {
		const { deck } = await connect("ABC");
		deck.send("{not json");
		expect((await deck.next("error")).code).toBe("bad-message");

		deck.send({ t: "dance" });
		expect((await deck.next("error")).code).toBe("unknown-message");
		expect(deck.closed).toBeUndefined();
	});

	it("closes a socket that sends an oversized frame", async () => {
		const { deck } = await connect("ABC");
		deck.send(JSON.stringify({ t: "open", pad: "x".repeat(MAX_MESSAGE_CHARS) }));

		expect((await deck.waitClosed()).code).toBe(1009);
	});

	it("closes a socket that sends binary frames", async () => {
		const { deck } = await connect("ABC");
		deck.ws.send(new Uint8Array([1, 2, 3]));

		expect((await deck.waitClosed()).code).toBe(1003);
	});

	it("closes a socket that floods, and hands its game to the opponent once the seat lapses", async () => {
		const { host, guest } = await startedTable();
		for (let index = 0; index <= MESSAGES_PER_WINDOW; index += 1) {
			guest.send({ t: "aim", seq: 0, a: 0, p: 0 });
		}

		expect((await guest.waitClosed()).code).toBe(1008);
		expect(await host.next("table")).toMatchObject({ away: [false, true] });
	});

	it("meters connections on a limit of their own", async () => {
		const caller = "198.51.100.200";
		const statuses: number[] = [];
		for (let attempt = 0; attempt < 11; attempt += 1) {
			const response = await SELF.fetch("https://example.com/v1/pool?name=ABC", {
				headers: { upgrade: "websocket", "cf-connecting-ip": caller },
			});
			statuses.push(response.status);
			response.webSocket?.accept();
			response.webSocket?.close(1000, "Counted.");
		}

		expect(statuses.slice(0, 10).every((status) => status === 101)).toBe(true);
		expect(statuses[10]).toBe(429);
	});
});

describe("wrangler configuration for the hall", () => {
	const config = env.TEST_WRANGLER_CONFIG as unknown as {
		durable_objects?: { bindings: { name: string; class_name: string }[] };
		migrations?: { new_sqlite_classes?: string[] }[];
		env?: Record<
			string,
			{ durable_objects?: { bindings: { name: string; class_name: string }[] } }
		>;
	};

	it("binds the hall in development and production", () => {
		const binding = { name: "POOL_HALL", class_name: "PoolHall" };
		expect(config.durable_objects?.bindings).toContainEqual(binding);
		expect(config.env?.production?.durable_objects?.bindings).toContainEqual(binding);
	});

	it("creates it as a SQLite-backed class, the kind the free plan has", () => {
		expect(config.migrations?.flatMap((migration) => migration.new_sqlite_classes ?? [])).toContain(
			"PoolHall",
		);
	});
});
