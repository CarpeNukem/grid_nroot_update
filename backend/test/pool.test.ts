import { env, runDurableObjectAlarm, runInDurableObject, SELF } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
	HALL_NAME,
	MAX_MESSAGE_CHARS,
	MESSAGES_PER_WINDOW,
	PROTOCOL_VERSION,
	RECONNECT_GRACE_MS,
} from "../src/pool/protocol.js";

/**
 * The pool hall: one hibernating Durable Object holding the lobby and every
 * table.
 *
 * Pinned here: the hall's part of a game is bookkeeping, not physics. It
 * checks whose turn it is and that a shot and its settled table arrive in
 * step, passes both to the opponent, and keeps the settled table so a
 * reconnecting player gets it back. And it protects its own budget: a socket
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

async function connect(name: string, resume?: string): Promise<{ deck: Deck; welcome: Message }> {
	const query = new URLSearchParams({ name, ...(resume === undefined ? {} : { resume }) });
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

/** Two decks at one table, game started, seat 0 to break. */
async function startedTable(): Promise<{
	host: Deck;
	guest: Deck;
	code: string;
	hostToken: string;
	guestToken: string;
}> {
	const { deck: host, welcome: hostWelcome } = await connect("HOST");
	host.send({ t: "open" });
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

	it("welcomes a deck with its protocol version, a seat token and the lobby", async () => {
		const { welcome } = await connect("ABC");

		expect(welcome.v).toBe(PROTOCOL_VERSION);
		expect(welcome.name).toBe("ABC");
		expect(welcome.token).toMatch(/^[0-9a-f-]{36}$/);
		expect(welcome.tables).toEqual([]);
	});

	it("answers a text ping without a message of its own", async () => {
		const { deck } = await connect("ABC");
		deck.send("ping");

		await deck.next("pong");
	});
});

describe("the lobby", () => {
	it("lists an open public table to everyone browsing, and takes it off once joined", async () => {
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
		expect(listed.tables).toEqual([{ code: table.code, host: "HOST" }]);

		const { deck: guest } = await connect("GUEST");
		guest.send({ t: "join", code: table.code });
		expect(await guest.next("table")).toMatchObject({
			status: "playing",
			seat: 1,
			turn: 0,
			players: ["HOST", "GUEST"],
		});
		expect((await browser.next("lobby")).tables).toEqual([]);
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
