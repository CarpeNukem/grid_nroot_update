import { DurableObject } from "cloudflare:workers";
import type { Env } from "../types.js";
import {
	AIM_INTERVAL_MS,
	CODE_ALPHABET,
	CODE_LENGTH,
	HALL_NAME,
	IDLE_TABLE_MS,
	isPlayerName,
	isSeat,
	isSeatToken,
	isTableCode,
	MAX_LISTED,
	MAX_MESSAGE_CHARS,
	MAX_SOCKETS,
	MAX_TABLES,
	MESSAGE_WINDOW_MS,
	MESSAGES_PER_WINDOW,
	PROTOCOL_VERSION,
	parseAim,
	parseShot,
	parseState,
	RECONNECT_GRACE_MS,
	REFUSALS,
	type RefusalCode,
	type Seat,
} from "./protocol.js";

export { HALL_NAME };

/** One player's chair at a table. `away` is when their connection dropped, while their seat is being kept. */
interface Chair {
	readonly name: string;
	readonly token: string;
	away: number | null;
}

type TableStatus = "waiting" | "playing" | "over";

/** A table as the hall stores it: one row, one JSON document. */
interface Table {
	readonly code: string;
	readonly private: boolean;
	status: TableStatus;
	chairs: [Chair | null, Chair | null];
	/** Games played at this table, counting the one in progress. Seat 0 breaks the odd ones. */
	game: number;
	/** The rack for the current game: both decks set the balls out from it identically. */
	seed: number;
	/** Shots settled this game. A shot and its settled table carry the number they belong to. */
	seq: number;
	turn: Seat;
	winner: Seat | null;
	/** True when the game ended because a player left rather than on the table. */
	forfeit: boolean;
	/** The table after the last settled shot, as the shooter's deck reported it; null before the break. */
	state: unknown | null;
	rematch: [boolean, boolean];
	updated: number;
}

/**
 * What the hall remembers about a socket, carried on the socket itself so it
 * survives the object hibernating between messages.
 */
interface Attachment {
	readonly token: string;
	readonly name: string;
	code: string | null;
	seat: Seat | null;
	/** Start of the current rate-limit window, and the messages counted in it. */
	window: number;
	count: number;
	/** When this socket's last cue preview was passed on. */
	aimed: number;
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
 * The venue's pool hall: the lobby and every table, in one Durable Object.
 *
 * One object rather than one per table, because the venue is small and the
 * lobby has to see every table at once. With a single object a table code is
 * unique the moment it is handed out and the lobby is never out of step with
 * the tables, which two kinds of object passing updates to each other would
 * have to work for. If the hall ever outgrows that, tables can move into
 * objects of their own behind the same protocol.
 *
 * Everything uses the hibernation API. Between messages the object is evicted
 * and costs nothing; the sockets stay connected at Cloudflare's edge. So no
 * state is kept in fields: tables live in this object's SQLite storage and
 * each socket carries its own attachment, and every handler reads what it
 * needs fresh. Keep-alives never wake it either: protocol pings are answered
 * by the runtime, and a text "ping" by the auto-response set up below.
 */
export class PoolHall extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		ctx.blockConcurrencyWhile(async () => {
			ctx.storage.sql.exec(
				"CREATE TABLE IF NOT EXISTS pool_tables (code TEXT PRIMARY KEY, data TEXT NOT NULL, updated INTEGER NOT NULL)",
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
			return new Response("The pool hall is full.", { status: 503 });
		}

		const now = Date.now();
		const resume = url.searchParams.get("resume");
		const back = isSeatToken(resume) ? this.findChair(resume) : undefined;

		// A seat token belongs to one socket. An older one still holding it is
		// a connection the player has already given up on.
		if (back !== undefined) {
			for (const socket of this.socketsFor(back.chair.token)) {
				this.closeQuietly(socket, CLOSE.replaced, "Reconnected elsewhere.");
			}
		}

		const pair = new WebSocketPair();
		const client = pair[0];
		const server = pair[1];
		this.ctx.acceptWebSocket(server);

		const attachment: Attachment = {
			token: back?.chair.token ?? crypto.randomUUID(),
			// A returning player keeps the name the table knows them by.
			name: back?.chair.name ?? name,
			code: back?.table.code ?? null,
			seat: back?.seat ?? null,
			window: now,
			count: 0,
			aimed: 0,
		};
		server.serializeAttachment(attachment);

		this.send(server, {
			t: "welcome",
			v: PROTOCOL_VERSION,
			token: attachment.token,
			name: attachment.name,
			tables: this.lobby(),
		});

		if (back !== undefined) {
			back.chair.away = null;
			this.save(back.table, now);
			this.sendTable(back.table);
			await this.scheduleCleanup();
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
				await this.open(ws, attachment, fields.private === true, now);
				break;
			case "join":
				await this.join(ws, attachment, fields.code, now);
				break;
			case "leave":
				await this.leave(ws, attachment, now);
				break;
			case "aim":
				this.aim(ws, attachment, fields, now);
				break;
			case "shot":
				this.shot(ws, attachment, fields);
				break;
			case "settle":
				this.settle(ws, attachment, fields, now);
				break;
			case "rematch":
				this.rematch(ws, attachment, now);
				break;
			case "sync":
				this.sync(ws, attachment);
				break;
			default:
				this.refuse(ws, "unknown-message");
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
		now: number,
	): Promise<void> {
		if (me.code !== null) {
			this.refuse(ws, "already-seated");
			return;
		}

		if (this.tableCount() >= MAX_TABLES) {
			this.refuse(ws, "hall-full");
			return;
		}

		const table: Table = {
			code: this.newCode(),
			private: isPrivate,
			status: "waiting",
			chairs: [{ name: me.name, token: me.token, away: null }, null],
			game: 1,
			seed: randomSeed(),
			seq: 0,
			turn: 0,
			winner: null,
			forfeit: false,
			state: null,
			rematch: [false, false],
			updated: now,
		};

		this.seat(ws, me, table.code, 0);
		this.save(table, now);
		this.sendTable(table);
		if (!table.private) {
			this.broadcastLobby();
		}

		await this.scheduleCleanup();
	}

	private async join(ws: WebSocket, me: Attachment, code: unknown, now: number): Promise<void> {
		if (me.code !== null) {
			this.refuse(ws, "already-seated");
			return;
		}

		const wanted = typeof code === "string" ? code.trim().toUpperCase() : code;
		const table = isTableCode(wanted) ? this.load(wanted) : undefined;
		if (table === undefined) {
			this.refuse(ws, "no-table");
			return;
		}

		if (table.status !== "waiting" || table.chairs[1] !== null || table.chairs[0] === null) {
			this.refuse(ws, "table-full");
			return;
		}

		table.chairs[1] = { name: me.name, token: me.token, away: null };
		table.status = "playing";
		table.turn = breakerOf(table.game);
		this.seat(ws, me, table.code, 1);
		this.save(table, now);
		this.sendTable(table);
		if (!table.private) {
			this.broadcastLobby();
		}

		await this.scheduleCleanup();
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

		await this.scheduleCleanup();
	}

	/** A cue preview, passed to the opponent and never stored. Dropped rather than refused when it is not wanted. */
	private aim(ws: WebSocket, me: Attachment, fields: Record<string, unknown>, now: number): void {
		const table = this.seatedTable(ws, me, false);
		if (
			table === undefined ||
			table.status !== "playing" ||
			table.turn !== me.seat ||
			fields.seq !== table.seq
		) {
			return;
		}

		if (now - me.aimed < AIM_INTERVAL_MS) {
			return;
		}

		const aim = parseAim(fields);
		if (aim === undefined) {
			return;
		}

		me.aimed = now;
		this.sendToSeat(table, other(table.turn), { t: "aim", seq: table.seq, ...aim });
	}

	/** The shot itself, passed on so the opponent's deck plays it while the shooter's does. Not stored: the settled table is. */
	private shot(ws: WebSocket, me: Attachment, fields: Record<string, unknown>): void {
		const table = this.turnTable(ws, me, fields.seq);
		if (table === undefined) {
			return;
		}

		const shot = parseShot(fields.shot);
		if (shot === undefined) {
			this.refuse(ws, "bad-message");
			return;
		}

		this.sendToSeat(table, other(table.turn), { t: "shot", seq: table.seq, shot });
	}

	/** The table after the shot: whose turn it is now, whether the game is won, and the balls as the shooter's deck left them. */
	private settle(
		ws: WebSocket,
		me: Attachment,
		fields: Record<string, unknown>,
		now: number,
	): void {
		const table = this.turnTable(ws, me, fields.seq);
		if (table === undefined) {
			return;
		}

		const state = parseState(fields.state);
		const winner = fields.winner;
		if (state === undefined || !isSeat(fields.turn) || !(winner === null || isSeat(winner))) {
			this.refuse(ws, "bad-message");
			return;
		}

		const seq = table.seq;
		table.state = state;
		table.seq += 1;
		table.turn = fields.turn;
		if (winner !== null) {
			table.status = "over";
			table.winner = winner;
		}

		this.save(table, now);
		this.sendToSeat(table, other(me.seat as Seat), {
			t: "settle",
			seq,
			turn: table.turn,
			winner: table.winner,
			state,
		});

		if (table.status === "over") {
			this.sendTable(table);
		}
	}

	/** The caller's table again, for a deck that has lost step with it. Costs a message, writes nothing. */
	private sync(ws: WebSocket, me: Attachment): void {
		const table = this.seatedTable(ws, me, true);
		if (table !== undefined && me.seat !== null) {
			this.send(ws, viewOf(table, me.seat));
		}
	}

	private rematch(ws: WebSocket, me: Attachment, now: number): void {
		const table = this.seatedTable(ws, me, true);
		if (table === undefined || me.seat === null) {
			return;
		}

		if (table.status !== "over") {
			this.refuse(ws, "not-over");
			return;
		}

		const opponent = table.chairs[other(me.seat)];
		if (opponent === null || opponent.away !== null) {
			this.refuse(ws, "no-opponent");
			return;
		}

		table.rematch[me.seat] = true;
		if (table.rematch[0] && table.rematch[1]) {
			table.game += 1;
			table.seed = randomSeed();
			table.seq = 0;
			table.turn = breakerOf(table.game);
			table.status = "playing";
			table.winner = null;
			table.forfeit = false;
			table.state = null;
			table.rematch = [false, false];
		}

		this.save(table, now);
		this.sendTable(table);
	}

	// ---------------------------------------------------- leaving and expiry

	/**
	 * A socket has gone. A seated player's chair is kept for the grace period,
	 * in case they are only reconnecting. Safe to call twice for one socket:
	 * a close the hall starts and the close handler that follows both land here.
	 */
	private async leftHall(gone: WebSocket, me: Attachment, now: number): Promise<void> {
		if (me.code === null || me.seat === null) {
			return;
		}

		const table = this.load(me.code);
		const chair = table?.chairs[me.seat];
		if (
			table === undefined ||
			chair === null ||
			chair === undefined ||
			chair.token !== me.token ||
			chair.away !== null
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

		chair.away = now;
		this.save(table, now);
		this.sendTable(table);
		if (!table.private) {
			this.broadcastLobby();
		}

		await this.scheduleCleanup();
	}

	/**
	 * Gives up a chair. Leaving a game in play hands it to the opponent; a
	 * table with nobody left at it is cleared away.
	 */
	private vacate(table: Table, seat: Seat, now: number): void {
		if (table.status === "playing") {
			table.status = "over";
			table.winner = other(seat);
			table.forfeit = true;
		}

		// An open table has only its host in it, so a host leaving one empties it.
		table.chairs[seat] = null;
		table.rematch = [false, false];
		if (table.chairs[0] === null && table.chairs[1] === null) {
			this.remove(table.code);
			return;
		}

		this.save(table, now);
		this.sendTable(table);
	}

	/** Runs when the earliest kept seat or idle table falls due. */
	override async alarm(): Promise<void> {
		const now = Date.now();
		let lobbyChanged = false;

		for (const table of this.allTables()) {
			if (now - table.updated >= IDLE_TABLE_MS) {
				for (const socket of this.socketsAt(table.code)) {
					this.send(socket, { t: "closed", code: table.code });
					this.unseat(socket);
				}
				this.remove(table.code);
				lobbyChanged = true;
				continue;
			}

			for (const seat of [0, 1] as const) {
				const chair = table.chairs[seat];
				if (chair !== null && chair.away !== null && now - chair.away >= RECONNECT_GRACE_MS) {
					this.vacate(table, seat, now);
					lobbyChanged = true;
					break;
				}
			}
		}

		if (lobbyChanged) {
			this.broadcastLobby();
		}

		await this.scheduleCleanup();
	}

	/** Sets the alarm for the next kept seat or idle table to fall due, or clears it when nothing will. */
	private async scheduleCleanup(): Promise<void> {
		let next: number | undefined;
		for (const table of this.allTables()) {
			const due = [table.updated + IDLE_TABLE_MS];
			for (const chair of table.chairs) {
				if (chair?.away != null) {
					due.push(chair.away + RECONNECT_GRACE_MS);
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

	private load(code: string): Table | undefined {
		const row = this.ctx.storage.sql
			.exec<{ data: string }>("SELECT data FROM pool_tables WHERE code = ?", code)
			.toArray()[0];

		return row === undefined ? undefined : (JSON.parse(row.data) as Table);
	}

	private allTables(): Table[] {
		return this.ctx.storage.sql
			.exec<{ data: string }>("SELECT data FROM pool_tables ORDER BY updated")
			.toArray()
			.map((row) => JSON.parse(row.data) as Table);
	}

	private tableCount(): number {
		return this.ctx.storage.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM pool_tables").one()
			.n;
	}

	private save(table: Table, now: number): void {
		table.updated = now;
		this.ctx.storage.sql.exec(
			"INSERT INTO pool_tables (code, data, updated) VALUES (?, ?, ?) ON CONFLICT(code) DO UPDATE SET data = excluded.data, updated = excluded.updated",
			table.code,
			JSON.stringify(table),
			now,
		);
	}

	private remove(code: string): void {
		this.ctx.storage.sql.exec("DELETE FROM pool_tables WHERE code = ?", code);
	}

	private findChair(token: string): { table: Table; seat: Seat; chair: Chair } | undefined {
		for (const table of this.allTables()) {
			for (const seat of [0, 1] as const) {
				const chair = table.chairs[seat];
				if (chair !== null && chair.token === token) {
					return { table, seat, chair };
				}
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

	/**
	 * Moves the caller to a table or back to the lobby. Written to the socket
	 * at once, because the broadcasts that follow find sockets by what their
	 * attachments say.
	 */
	private seat(ws: WebSocket, me: Attachment, code: string | null, seat: Seat | null): void {
		me.code = code;
		me.seat = seat;
		ws.serializeAttachment(me);
	}

	/** Takes a socket away from its table, back to the lobby. */
	private unseat(socket: WebSocket): void {
		const attachment = this.attachmentOf(socket);
		if (attachment === null) {
			return;
		}

		attachment.code = null;
		attachment.seat = null;
		socket.serializeAttachment(attachment);
	}

	/** The caller's table, when they are at one. */
	private seatedTable(ws: WebSocket, me: Attachment, refuse: boolean): Table | undefined {
		const table = me.code === null ? undefined : this.load(me.code);
		if (table === undefined || me.seat === null) {
			if (refuse) {
				this.refuse(ws, "not-seated");
			}
			return undefined;
		}

		return table;
	}

	/** The caller's table, when it is their turn and the shot is the one the table is waiting for. */
	private turnTable(ws: WebSocket, me: Attachment, seq: unknown): Table | undefined {
		const table = this.seatedTable(ws, me, true);
		if (table === undefined) {
			return undefined;
		}

		if (table.status !== "playing" || table.turn !== me.seat) {
			this.refuse(ws, "not-your-turn");
			return undefined;
		}

		if (seq !== table.seq) {
			this.refuse(ws, "out-of-order");
			return undefined;
		}

		return table;
	}

	private lobby(): { code: string; host: string }[] {
		return this.allTables()
			.filter(
				(table) => !table.private && table.status === "waiting" && table.chairs[0]?.away === null,
			)
			.slice(-MAX_LISTED)
			.reverse()
			.map((table) => ({ code: table.code, host: (table.chairs[0] as Chair).name }));
	}

	private broadcastLobby(): void {
		const message = { t: "lobby", tables: this.lobby() };
		for (const socket of this.ctx.getWebSockets()) {
			if (this.attachmentOf(socket)?.code === null) {
				this.send(socket, message);
			}
		}
	}

	/** Each seated player's view of their table. */
	private sendTable(table: Table): void {
		for (const socket of this.socketsAt(table.code)) {
			const seat = this.attachmentOf(socket)?.seat;
			if (seat === 0 || seat === 1) {
				this.send(socket, viewOf(table, seat));
			}
		}
	}

	private sendToSeat(table: Table, seat: Seat, message: unknown): void {
		const chair = table.chairs[seat];
		if (chair === null) {
			return;
		}

		for (const socket of this.socketsFor(chair.token)) {
			this.send(socket, message);
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

const other = (seat: Seat): Seat => (seat === 0 ? 1 : 0);

/** Seat 0, the player who opened the table, breaks the first game; the break alternates after that. */
const breakerOf = (game: number): Seat => (game % 2 === 1 ? 0 : 1);

/** A rack seed both decks can hold in a signed 32-bit integer. */
function randomSeed(): number {
	const [value = 0] = crypto.getRandomValues(new Uint32Array(1));
	return value >>> 1;
}

/** What one seat sees of its table. Seat tokens are never part of it. */
function viewOf(table: Table, seat: Seat) {
	return {
		t: "table",
		code: table.code,
		private: table.private,
		seat,
		players: table.chairs.map((chair) => chair?.name ?? null),
		away: table.chairs.map((chair) => chair?.away != null),
		status: table.status,
		game: table.game,
		seed: table.seed,
		seq: table.seq,
		turn: table.turn,
		winner: table.winner,
		forfeit: table.forfeit,
		rematch: table.rematch,
		state: table.state,
	};
}
