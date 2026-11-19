import { DurableObject } from "cloudflare:workers";
import type { Env } from "../types.js";
import { BET_STEP, MAX_BET, MIN_BET, START_CHIPS } from "./blackjack.js";
import { BLACKJACK_SEATS, GAME_MESSAGES, type GameState, RULES, type Sitter } from "./games.js";
import {
	type CardGame,
	CODE_ALPHABET,
	CODE_LENGTH,
	HALL_NAME,
	IDLE_TABLE_MS,
	isCardGame,
	isPlayerName,
	isSeatToken,
	isTableCode,
	MAX_LISTED,
	MAX_MESSAGE_CHARS,
	MAX_SOCKETS,
	MAX_TABLES,
	MESSAGE_WINDOW_MS,
	MESSAGES_PER_WINDOW,
	PROTOCOL_VERSION,
	RECONNECT_GRACE_MS,
	REFUSALS,
	type RefusalCode,
} from "./protocol.js";

export { HALL_NAME };

/** A table as the hall stores it: one row, one JSON document, its game's whole state included, shoe and hands. */
type CardTable = GameState & {
	readonly code: string;
	readonly private: boolean;
	/** The game dealt here, for good. */
	readonly game: CardGame;
	updated: number;
};

/** A public table as the lobby lists it. */
interface Listing {
	readonly code: string;
	readonly game: CardGame;
	readonly players: (string | null)[];
	/** Whether a hand is being played right now; a player who joins sits in from the next one. */
	readonly playing: boolean;
}

/**
 * What the hall remembers about a socket, carried on the socket itself so it
 * survives the object hibernating between messages.
 */
interface Attachment {
	readonly token: string;
	readonly name: string;
	code: string | null;
	seat: number | null;
	/** Start of the current rate-limit window, and the messages counted in it. */
	window: number;
	count: number;
}

/** WebSocket close codes. 4000 and up are the hall's own. */
const CLOSE = {
	normal: 1000,
	unsupported: 1003,
	policy: 1008,
	tooBig: 1009,
	replaced: 4000,
} as const;

/**
 * The venue's card hall: the lobby and every card table, in one Durable
 * Object, and the dealer at each of them.
 *
 * Built like the pool hall (src/pool/hall.ts), and for the same reasons: one
 * object, so the lobby always sees every table and a code is unique the
 * moment it is handed out; and the hibernation API throughout, so the hall
 * costs nothing between messages. Tables live in SQLite and each socket
 * carries its own attachment.
 *
 * Unlike the pool hall it plays the game. Each game's rules are a module of
 * their own, met through games.ts: blackjack, BLACK ICE and Hold'em. This class seats
 * players, passes each game its players' moves, runs the clocks (a bet window,
 * a player's turn, a bot thinking, a call's reveal, a dropped player's seat)
 * on one alarm, and sends every seat its view of the table, which never holds
 * a card that seat should not see.
 */
export class CardHall extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		ctx.blockConcurrencyWhile(async () => {
			ctx.storage.sql.exec(
				"CREATE TABLE IF NOT EXISTS card_tables (code TEXT PRIMARY KEY, data TEXT NOT NULL, updated INTEGER NOT NULL)",
			);
		});
		ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
	}

	// ------------------------------------------------------------ connecting

	override async fetch(request: Request): Promise<Response> {
		if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
			return new Response("Expected a WebSocket.", { status: 426 });
		}

		const url = new URL(request.url);
		const name = url.searchParams.get("name");
		if (!isPlayerName(name)) {
			return new Response("A player name is required.", { status: 400 });
		}

		if (this.ctx.getWebSockets().length >= MAX_SOCKETS) {
			return new Response("The card hall is full.", { status: 503 });
		}

		const now = Date.now();
		const resume = url.searchParams.get("resume");
		const back = isSeatToken(resume) ? this.findPlayer(resume) : undefined;

		// A seat token belongs to one socket. An older one still holding it is
		// a connection the player has already given up on.
		if (back !== undefined) {
			for (const socket of this.socketsFor(back.player.token)) {
				this.closeQuietly(socket, CLOSE.replaced, "Reconnected elsewhere.");
			}
		}

		const pair = new WebSocketPair();
		const client = pair[0];
		const server = pair[1];
		this.ctx.acceptWebSocket(server);

		const attachment: Attachment = {
			token: back?.player.token ?? crypto.randomUUID(),
			name: back?.player.name ?? name,
			code: back?.table.code ?? null,
			seat: back?.seat ?? null,
			window: now,
			count: 0,
		};
		server.serializeAttachment(attachment);

		this.send(server, {
			t: "welcome",
			v: PROTOCOL_VERSION,
			token: attachment.token,
			name: attachment.name,
			// Blackjack's table limits. Every game's own are in its views.
			limits: {
				min: MIN_BET,
				max: MAX_BET,
				step: BET_STEP,
				start: START_CHIPS,
				seats: BLACKJACK_SEATS,
			},
			tables: this.lobby(),
		});

		if (back !== undefined) {
			back.player.away = null;
			RULES[back.table.game].back?.(back.table, now);
			this.save(back.table, now);
			this.sendTable(back.table, now);
			if (!back.table.private) {
				this.broadcastLobby();
			}
			await this.scheduleAlarm();
		}

		return new Response(null, { status: 101, webSocket: client });
	}

	// ------------------------------------------------------------- messages

	override async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
		const attachment = ws.deserializeAttachment() as Attachment | null;
		if (attachment === null) {
			this.closeQuietly(ws, CLOSE.policy, "Unknown socket.");
			return;
		}

		if (typeof message !== "string") {
			this.closeQuietly(ws, CLOSE.unsupported, "Text frames only.");
			return;
		}

		if (message.length > MAX_MESSAGE_CHARS) {
			this.closeQuietly(ws, CLOSE.tooBig, "Message too large.");
			return;
		}

		const now = Date.now();
		if (now - attachment.window >= MESSAGE_WINDOW_MS) {
			attachment.window = now;
			attachment.count = 0;
		}

		attachment.count += 1;
		if (attachment.count > MESSAGES_PER_WINDOW) {
			ws.serializeAttachment(attachment);
			this.closeQuietly(ws, CLOSE.policy, "Too many messages.");
			await this.leftHall(ws, attachment, now);
			return;
		}

		let parsed: unknown;
		try {
			parsed = JSON.parse(message);
		} catch {
			ws.serializeAttachment(attachment);
			this.refuse(ws, "bad-message");
			return;
		}

		if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
			ws.serializeAttachment(attachment);
			this.refuse(ws, "bad-message");
			return;
		}

		const fields = parsed as Record<string, unknown>;
		switch (fields.t) {
			case "open":
				await this.open(ws, attachment, fields.private === true, fields.game, now);
				break;
			case "join":
				await this.join(ws, attachment, fields.code, now);
				break;
			case "leave":
				await this.leave(ws, attachment, now);
				break;
			case "sync":
				this.sync(ws, attachment, now);
				break;
			default:
				if (typeof fields.t === "string" && GAME_MESSAGES.has(fields.t)) {
					await this.play(ws, attachment, fields, now);
				} else {
					this.refuse(ws, "unknown-message");
				}
				break;
		}

		ws.serializeAttachment(attachment);
	}

	override async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
		// Finish the closing handshake. 1005 and 1006 describe a close; they cannot be sent.
		this.closeQuietly(ws, code === 1005 || code === 1006 ? CLOSE.normal : code, reason);
		const attachment = ws.deserializeAttachment() as Attachment | null;
		if (attachment !== null) {
			await this.leftHall(ws, attachment, Date.now());
		}
	}

	override async webSocketError(ws: WebSocket): Promise<void> {
		const attachment = ws.deserializeAttachment() as Attachment | null;
		if (attachment !== null) {
			await this.leftHall(ws, attachment, Date.now());
		}
	}

	// --------------------------------------------------------------- tables

	private async open(
		ws: WebSocket,
		me: Attachment,
		isPrivate: boolean,
		askedGame: unknown,
		now: number,
	): Promise<void> {
		if (me.code !== null) {
			this.refuse(ws, "already-seated");
			return;
		}

		const game = askedGame === undefined ? "blackjack" : askedGame;
		if (!isCardGame(game)) {
			this.refuse(ws, "bad-message");
			return;
		}

		if (this.tableCount() >= MAX_TABLES) {
			this.refuse(ws, "hall-full");
			return;
		}

		const table: CardTable = {
			...RULES[game].create(),
			code: this.newCode(),
			private: isPrivate,
			game,
			updated: now,
		};
		RULES[game].seat(table, 0, me.name, me.token, now);

		this.seat(ws, me, table.code, 0);
		this.save(table, now);
		this.sendTable(table, now);
		if (!table.private) {
			this.broadcastLobby();
		}

		await this.scheduleAlarm();
	}

	/** Sits the caller in the first empty seat. During a hand or a game they wait for the next one. */
	private async join(ws: WebSocket, me: Attachment, code: unknown, now: number): Promise<void> {
		if (me.code !== null) {
			this.refuse(ws, "already-seated");
			return;
		}

		const table = this.tableCalled(code);
		if (table === undefined) {
			this.refuse(ws, "no-table");
			return;
		}

		const seat = table.seats.indexOf(null);
		if (seat < 0) {
			this.refuse(ws, "table-full");
			return;
		}

		// At blackjack a new player has not bet yet, so a table counting down keeps counting.
		RULES[table.game].seat(table, seat, me.name, me.token, now);
		this.seat(ws, me, table.code, seat);
		this.save(table, now);
		this.sendTable(table, now);
		if (!table.private) {
			this.broadcastLobby();
		}

		await this.scheduleAlarm();
	}

	private async leave(ws: WebSocket, me: Attachment, now: number): Promise<void> {
		if (me.code === null || me.seat === null) {
			this.refuse(ws, "not-seated");
			return;
		}

		const table = this.load(me.code);
		const seat = me.seat;
		this.seat(ws, me, null, null);
		this.send(ws, { t: "lobby", tables: this.lobby() });
		if (table !== undefined) {
			this.vacate(table, seat, now);
			this.broadcastLobby();
		}

		await this.scheduleAlarm();
	}

	/**
	 * A move in the game at the caller's table: a bet or a hit at blackjack, a
	 * play or a call at BLACK ICE. Moves made against the table as it was are
	 * refused by the game, so a double click is one move.
	 */
	private async play(
		ws: WebSocket,
		me: Attachment,
		fields: Record<string, unknown>,
		now: number,
	): Promise<void> {
		const table = this.seatedTable(ws, me);
		if (table === undefined || me.seat === null) {
			return;
		}

		const rules = RULES[table.game];
		if (!rules.messages.includes(fields.t as string)) {
			this.refuse(ws, "unknown-message");
			return;
		}

		const before = rules.playing(table);
		const refusal = rules.message(table, me.seat, fields, now);
		if (refusal !== null) {
			this.refuse(ws, refusal);
			return;
		}

		await this.changed(table, before, now);
	}

	/** The caller's table again, for a deck that has lost step with it. Costs a message, writes nothing. */
	private sync(ws: WebSocket, me: Attachment, now: number): void {
		const table = this.seatedTable(ws, me);
		if (table !== undefined) {
			this.send(ws, viewOf(table, me.seat, now));
		}
	}

	/** After a move: stored, sent to every seat, and the clock set for whatever the table now waits on. */
	private async changed(table: CardTable, before: boolean, now: number): Promise<void> {
		this.save(table, now);
		this.sendTable(table, now);
		// The lobby says whether a hand is being played.
		if (!table.private && before !== RULES[table.game].playing(table)) {
			this.broadcastLobby();
		}
		await this.scheduleAlarm();
	}

	// ---------------------------------------------------- leaving and expiry

	/**
	 * A socket has gone. A seated player's chair is kept for the grace period,
	 * in case they are only reconnecting. Being away does not hold a table's
	 * bets up, and their turn stands when its clock runs out.
	 */
	private async leftHall(gone: WebSocket, me: Attachment, now: number): Promise<void> {
		if (me.code === null || me.seat === null) {
			return;
		}

		const table = this.load(me.code);
		const player = table?.seats[me.seat];
		if (
			table === undefined ||
			player === null ||
			player === undefined ||
			player.token !== me.token ||
			player.away !== null
		) {
			return;
		}

		// Only if no other socket of theirs is still connected, such as the one they reconnected on.
		if (
			this.socketsFor(me.token).some(
				(socket) => socket !== gone && socket.readyState === WebSocket.OPEN,
			)
		) {
			return;
		}

		player.away = now;
		RULES[table.game].away(table, now);
		this.save(table, now);
		this.sendTable(table, now);
		if (!table.private) {
			this.broadcastLobby();
		}

		await this.scheduleAlarm();
	}

	/**
	 * Gives up a seat for good. A table with no person left at it, bots or
	 * not, is cleared away. Every caller broadcasts the lobby after this.
	 */
	private vacate(table: CardTable, seat: number, now: number): void {
		RULES[table.game].remove(table, seat, now);
		if (!hasPeople(table)) {
			this.closeTable(table, "empty");
			return;
		}

		this.save(table, now);
		this.sendTable(table, now);
	}

	/** Clears a table away, telling anyone still at it why and taking them back to the lobby. */
	private closeTable(table: CardTable, why: "empty" | "idle"): void {
		for (const socket of this.socketsAt(table.code)) {
			this.send(socket, { t: "closed", code: table.code, why });
			this.unseat(socket);
		}
		this.remove(table.code);
	}

	/** Runs when the earliest clock falls due: bets closing, a turn running out, a kept seat lapsing, an idle table. */
	override async alarm(): Promise<void> {
		const now = Date.now();
		let lobbyChanged = false;

		for (const table of this.allTables()) {
			if (now - table.updated >= IDLE_TABLE_MS) {
				this.closeTable(table, "idle");
				lobbyChanged = true;
				continue;
			}

			const rules = RULES[table.game];
			let changed = false;
			table.seats.forEach((player, seat) => {
				if (player !== null && player.away !== null && now - player.away >= RECONNECT_GRACE_MS) {
					rules.remove(table, seat, now);
					changed = true;
					lobbyChanged = true;
				}
			});

			if (!hasPeople(table)) {
				this.closeTable(table, "empty");
				continue;
			}

			const playing = rules.playing(table);
			changed = rules.timeout(table, now) || changed;
			lobbyChanged ||= playing !== rules.playing(table) && !table.private;
			if (changed) {
				this.save(table, now);
				this.sendTable(table, now);
			}
		}

		if (lobbyChanged) {
			this.broadcastLobby();
		}

		await this.scheduleAlarm();
	}

	/** Sets the alarm for the earliest clock at any table, or clears it when nothing is waiting. */
	private async scheduleAlarm(): Promise<void> {
		let next: number | undefined;
		for (const table of this.allTables()) {
			const due = [table.updated + IDLE_TABLE_MS];
			if (table.deadline !== null) {
				due.push(table.deadline);
			}
			for (const player of table.seats) {
				if (player?.away != null) {
					due.push(player.away + RECONNECT_GRACE_MS);
				}
			}
			const soonest = Math.min(...due);
			next = next === undefined ? soonest : Math.min(next, soonest);
		}

		const current = await this.ctx.storage.getAlarm();
		if (next === undefined) {
			if (current !== null) {
				await this.ctx.storage.deleteAlarm();
			}
			return;
		}

		if (current !== next) {
			await this.ctx.storage.setAlarm(next);
		}
	}

	// ------------------------------------------------------------- storage

	private load(code: string): CardTable | undefined {
		const row = this.ctx.storage.sql
			.exec<{ data: string }>("SELECT data FROM card_tables WHERE code = ?", code)
			.toArray()[0];

		return row === undefined ? undefined : (JSON.parse(row.data) as CardTable);
	}

	private allTables(): CardTable[] {
		return this.ctx.storage.sql
			.exec<{ data: string }>("SELECT data FROM card_tables ORDER BY updated")
			.toArray()
			.map((row) => JSON.parse(row.data) as CardTable);
	}

	/** The table a deck named by its code, which it may have typed in lower case or with spaces. */
	private tableCalled(code: unknown): CardTable | undefined {
		const wanted = typeof code === "string" ? code.trim().toUpperCase() : code;
		return isTableCode(wanted) ? this.load(wanted) : undefined;
	}

	private tableCount(): number {
		return this.ctx.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM card_tables").one()
			.n;
	}

	private save(table: CardTable, now: number): void {
		table.updated = now;
		this.ctx.storage.sql.exec(
			"INSERT INTO card_tables (code, data, updated) VALUES (?, ?, ?) ON CONFLICT(code) DO UPDATE SET data = excluded.data, updated = excluded.updated",
			table.code,
			JSON.stringify(table),
			now,
		);
	}

	private remove(code: string): void {
		this.ctx.storage.sql.exec("DELETE FROM card_tables WHERE code = ?", code);
	}

	private findPlayer(
		token: string,
	): { table: CardTable; seat: number; player: Sitter } | undefined {
		for (const table of this.allTables()) {
			const seat = table.seats.findIndex((player) => player?.token === token);
			if (seat >= 0) {
				return { table, seat, player: table.seats[seat] as Sitter };
			}
		}

		return undefined;
	}

	private newCode(): string {
		for (;;) {
			const bytes = crypto.getRandomValues(new Uint8Array(CODE_LENGTH));
			const code = [...bytes].map((byte) => CODE_ALPHABET[byte % CODE_ALPHABET.length]).join("");
			if (this.load(code) === undefined) {
				return code;
			}
		}
	}

	// ------------------------------------------------------------- sockets

	private attachmentOf(socket: WebSocket): Attachment | null {
		return socket.deserializeAttachment() as Attachment | null;
	}

	private socketsFor(token: string): WebSocket[] {
		return this.ctx.getWebSockets().filter((socket) => this.attachmentOf(socket)?.token === token);
	}

	private socketsAt(code: string): WebSocket[] {
		return this.ctx.getWebSockets().filter((socket) => this.attachmentOf(socket)?.code === code);
	}

	/** Moves the caller to a seat or back to the lobby, written at once: the broadcasts that follow read it. */
	private seat(ws: WebSocket, me: Attachment, code: string | null, seat: number | null): void {
		me.code = code;
		me.seat = seat;
		ws.serializeAttachment(me);
	}

	private unseat(socket: WebSocket): void {
		const attachment = this.attachmentOf(socket);
		if (attachment === null) {
			return;
		}

		attachment.code = null;
		attachment.seat = null;
		socket.serializeAttachment(attachment);
	}

	/** The caller's table, refusing when they are not at one. */
	private seatedTable(ws: WebSocket, me: Attachment): CardTable | undefined {
		const table = me.code === null ? undefined : this.load(me.code);
		if (table === undefined || me.seat === null) {
			this.refuse(ws, "not-seated");
			return undefined;
		}

		return table;
	}

	/** Public tables with a person at them who is here. */
	private lobby(): Listing[] {
		return this.allTables()
			.filter(
				(table) =>
					!table.private &&
					table.seats.some(
						(player) => player !== null && player.bot !== true && player.away === null,
					),
			)
			.slice(-MAX_LISTED)
			.reverse()
			.map((table) => ({
				code: table.code,
				game: table.game,
				players: table.seats.map((player) => player?.name ?? null),
				playing: RULES[table.game].playing(table),
			}));
	}

	private broadcastLobby(): void {
		const message = { t: "lobby", tables: this.lobby() };
		for (const socket of this.ctx.getWebSockets()) {
			if (this.attachmentOf(socket)?.code === null) {
				this.send(socket, message);
			}
		}
	}

	/** Each seat's view of the table. */
	private sendTable(table: CardTable, now: number): void {
		for (const socket of this.socketsAt(table.code)) {
			const seat = this.attachmentOf(socket)?.seat;
			if (typeof seat === "number") {
				this.send(socket, viewOf(table, seat, now));
			}
		}
	}

	private refuse(ws: WebSocket, code: RefusalCode): void {
		this.send(ws, { t: "error", code, message: REFUSALS[code] });
	}

	private send(socket: WebSocket, message: unknown): void {
		try {
			socket.send(JSON.stringify(message));
		} catch {
			// Already closing. Its close handler tidies up.
		}
	}

	private closeQuietly(socket: WebSocket, code: number, reason: string): void {
		try {
			socket.close(code, reason);
		} catch {
			// Already closed.
		}
	}
}

/** Whether anyone at a table is a person rather than a bot. A table of bots alone is cleared away. */
const hasPeople = (table: CardTable): boolean =>
	table.seats.some((player) => player !== null && player.bot !== true);

/**
 * What one seat sees of its table: which table and game it is, and the
 * game's own view for that seat, which never holds a card the seat should not
 * see. Seat tokens are never part of it. A game's `left` is the time on its
 * clock, in milliseconds, so a deck can count it down without trusting its own
 * clock to agree with the hall's.
 */
function viewOf(table: CardTable, seat: number | null, now: number) {
	return {
		t: "table",
		code: table.code,
		private: table.private,
		game: table.game,
		seat,
		...RULES[table.game].view(table, seat, now),
	};
}
