import { DurableObject } from "cloudflare:workers";
import type { Env } from "../types.js";
import {
	AIM_INTERVAL_MS,
	CODE_ALPHABET,
	CODE_LENGTH,
	HALL_NAME,
	IDLE_TABLE_MS,
	isPlayerName,
	isRules,
	isSeat,
	isSeatToken,
	isTableCode,
	knowsRules,
	MAX_LISTED,
	MAX_MESSAGE_CHARS,
	MAX_SOCKETS,
	MAX_TABLES,
	MAX_WATCHERS,
	MESSAGE_WINDOW_MS,
	MESSAGES_PER_WINDOW,
	OLDEST_PROTOCOL,
	parseAim,
	parseShot,
	parseState,
	physicsOf,
	RECONNECT_GRACE_MS,
	REFUSALS,
	type RefusalCode,
	type Rules,
	type Seat,
	spokenVersion,
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
	/** The game played here, for good: a rematch is the same game again. */
	readonly rules: Rules;
	/**
	 * Whether the decks draw aim assist, their collision predictions. Off for
	 * a tournament, when both players see only the cue ball's line. The hall
	 * only carries it; like the rules, it is kept for good.
	 */
	readonly assist: boolean;
	/**
	 * How the decks at this table play a shot, from the deck that opened it
	 * (see `physicsOf`). Only decks of the same physics are seated, shown it
	 * in the lobby or let watch it, since every deck there plays each shot
	 * itself. A table stored before the hall knew is of the first.
	 */
	readonly physics: number;
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

/** A public table as the lobby lists it: an open one to join, or a game in play to watch. */
interface Listing {
	readonly code: string;
	readonly host: string;
	readonly rules: Rules;
	readonly assist: boolean;
	readonly status: TableStatus;
	readonly players: (string | null)[];
	readonly watchers: number;
}

/**
 * What the hall remembers about a socket, carried on the socket itself so it
 * survives the object hibernating between messages.
 */
interface Attachment {
	readonly token: string;
	readonly name: string;
	/**
	 * The protocol this socket's deck speaks, which decides the games it is
	 * shown. Missing on a socket connected before the hall knew versions,
	 * which is one on version 2.
	 */
	readonly version?: number;
	/** The table this socket is at, and its chair there. A table with no chair is one it is watching. */
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

		const version = spokenVersion(url.searchParams.get("v"));
		const attachment: Attachment = {
			token: back?.chair.token ?? crypto.randomUUID(),
			// A returning player keeps the name the table knows them by.
			name: back?.chair.name ?? name,
			version,
			code: back?.table.code ?? null,
			seat: back?.seat ?? null,
			window: now,
			count: 0,
			aimed: 0,
		};
		server.serializeAttachment(attachment);

		this.send(server, {
			t: "welcome",
			v: version,
			token: attachment.token,
			name: attachment.name,
			tables: this.lobby(version),
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
				await this.open(ws, attachment, fields.private === true, fields.rules, fields.assist, now);
				break;
			case "join":
				await this.join(ws, attachment, fields.code, now);
				break;
			case "watch":
				this.watch(ws, attachment, fields.code);
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
		askedRules: unknown,
		askedAssist: unknown,
		now: number,
	): Promise<void> {
		if (me.code !== null) {
			this.refuse(ws, "already-seated");
			return;
		}

		const rules = askedRules === undefined ? "eight" : askedRules;
		const assist = askedAssist === undefined ? true : askedAssist;
		if (!isRules(rules) || !knowsRules(versionOf(me), rules) || typeof assist !== "boolean") {
			this.refuse(ws, "bad-message");
			return;
		}

		if (this.tableCount() >= MAX_TABLES) {
			this.refuse(ws, "hall-full");
			return;
		}

		const table: Table = {
			code: this.newCode(),
			private: isPrivate,
			rules,
			assist,
			physics: physicsOf(versionOf(me)),
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

		const table = this.tableCalled(code);
		if (table === undefined) {
			this.refuse(ws, "no-table");
			return;
		}

		const mismatch = deckMismatch(me, table);
		if (mismatch !== undefined) {
			this.refuse(ws, mismatch);
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

	/**
	 * Takes the sender to a table to watch, by its code: open, in play or over,
	 * public or private. A watcher is sent everything the opponent is, and has
	 * no say in the game. Nothing is stored: the socket's attachment is all.
	 */
	private watch(ws: WebSocket, me: Attachment, code: unknown): void {
		if (me.code !== null) {
			this.refuse(ws, "already-seated");
			return;
		}

		const table = this.tableCalled(code);
		if (table === undefined) {
			this.refuse(ws, "no-table");
			return;
		}

		const mismatch = deckMismatch(me, table);
		if (mismatch !== undefined) {
			this.refuse(ws, mismatch);
			return;
		}

		if (this.watchersOf(table.code).length >= MAX_WATCHERS) {
			this.refuse(ws, "no-room");
			return;
		}

		this.seat(ws, me, table.code, null);
		this.watchersChanged(table);
	}

	private async leave(ws: WebSocket, me: Attachment, now: number): Promise<void> {
		if (me.code !== null && me.seat === null) {
			this.stopWatching(ws, me);
			return;
		}

		if (me.code === null || me.seat === null) {
			this.refuse(ws, "not-seated");
			return;
		}

		const table = this.load(me.code);
		const seat = me.seat;
		this.seat(ws, me, null, null);
		this.send(ws, { t: "lobby", tables: this.lobby(versionOf(me)) });
		if (table !== undefined) {
			this.vacate(table, seat, now);
			this.broadcastLobby();
		}

		await this.scheduleCleanup();
	}

	/** Takes a watcher back to the lobby. The table itself is untouched. */
	private stopWatching(ws: WebSocket, me: Attachment): void {
		const table = me.code === null ? undefined : this.load(me.code);
		this.seat(ws, me, null, null);
		this.send(ws, { t: "lobby", tables: this.lobby(versionOf(me)) });
		if (table !== undefined) {
			this.watchersChanged(table);
		}
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
		this.passOn(table, table.turn, { t: "aim", seq: table.seq, ...aim });
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

		this.passOn(table, table.turn, { t: "shot", seq: table.seq, shot });
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
		this.passOn(table, me.seat as Seat, {
			t: "settle",
			seq,
			turn: table.turn,
			winner: table.winner,
			state,
		});

		if (table.status === "over") {
			this.sendTable(table);
			// A finished game is no longer one to watch.
			if (!table.private) {
				this.broadcastLobby();
			}
		}
	}

	/** The caller's table again, for a deck that has lost step with it, or a watcher's. Costs a message, writes nothing. */
	private sync(ws: WebSocket, me: Attachment): void {
		const watched = me.code !== null && me.seat === null ? this.load(me.code) : undefined;
		const table = watched ?? this.seatedTable(ws, me, true);
		if (table !== undefined) {
			this.send(ws, viewOf(table, me.seat, this.watchersOf(table.code).length));
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
		// A new game is one to watch again.
		if (table.status === "playing" && !table.private) {
			this.broadcastLobby();
		}
	}

	// ---------------------------------------------------- leaving and expiry

	/**
	 * A socket has gone. A seated player's chair is kept for the grace period,
	 * in case they are only reconnecting; a watcher is simply one fewer. Safe
	 * to call twice for one socket: a close the hall starts and the close
	 * handler that follows both land here.
	 */
	private async leftHall(gone: WebSocket, me: Attachment, now: number): Promise<void> {
		if (me.code !== null && me.seat === null) {
			const watched = this.load(me.code);
			if (watched !== undefined) {
				this.watchersChanged(watched, gone);
			}
			return;
		}

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
	 * table with nobody left at it is cleared away, and anyone watching it is
	 * sent back to the lobby. Every caller broadcasts the lobby after this.
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
			this.closeTable(table, "empty");
			return;
		}

		this.save(table, now);
		this.sendTable(table);
	}

	/**
	 * Clears a table away, telling everyone still at it why and taking them
	 * back to the lobby. The caller broadcasts the lobby they arrive in.
	 */
	private closeTable(table: Table, why: "empty" | "idle"): void {
		for (const socket of this.socketsAt(table.code)) {
			this.send(socket, { t: "closed", code: table.code, why });
			this.unseat(socket);
		}
		this.remove(table.code);
	}

	/** Runs when the earliest kept seat or idle table falls due. */
	override async alarm(): Promise<void> {
		const now = Date.now();
		let lobbyChanged = false;

		for (const table of this.allTables()) {
			if (now - table.updated >= IDLE_TABLE_MS) {
				this.closeTable(table, "idle");
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

		return row === undefined ? undefined : readTable(row.data);
	}

	private allTables(): Table[] {
		return this.ctx.storage.sql
			.exec<{ data: string }>("SELECT data FROM pool_tables ORDER BY updated")
			.toArray()
			.map((row) => readTable(row.data));
	}

	/** The table a deck named by its code, which it may have typed in lower case or with spaces. */
	private tableCalled(code: unknown): Table | undefined {
		const wanted = typeof code === "string" ? code.trim().toUpperCase() : code;
		return isTableCode(wanted) ? this.load(wanted) : undefined;
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
	 * The table a socket is watching, or null. Only a socket still connected
	 * counts, and never `gone`, the one whose leaving is being handled.
	 */
	private watchedBy(socket: WebSocket, gone?: WebSocket): string | null {
		const attachment = this.attachmentOf(socket);
		return socket !== gone && socket.readyState === WebSocket.OPEN && attachment?.seat === null
			? attachment.code
			: null;
	}

	private watchersOf(code: string, gone?: WebSocket): WebSocket[] {
		return this.ctx.getWebSockets().filter((socket) => this.watchedBy(socket, gone) === code);
	}

	/** Watchers at every table, counted in one pass over the sockets. */
	private watcherCounts(gone?: WebSocket): Map<string, number> {
		const counts = new Map<string, number>();
		for (const socket of this.ctx.getWebSockets()) {
			const code = this.watchedBy(socket, gone);
			if (code !== null) {
				counts.set(code, (counts.get(code) ?? 0) + 1);
			}
		}

		return counts;
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

	/**
	 * Public tables worth showing a browsing deck: open ones whose host is
	 * here, to join, and games in play, to watch, of the games that deck
	 * knows. A game always has both players, since one leaving ends it, so
	 * either kind has a host.
	 */
	private lobby(version: number, gone?: WebSocket): Listing[] {
		const watchers = this.watcherCounts(gone);
		return this.allTables()
			.filter(
				(table) =>
					!table.private &&
					knowsRules(version, table.rules) &&
					physicsOf(version) === table.physics &&
					(table.status === "playing" ||
						(table.status === "waiting" && table.chairs[0]?.away === null)),
			)
			.slice(-MAX_LISTED)
			.reverse()
			.map((table) => ({
				code: table.code,
				host: (table.chairs[0] as Chair).name,
				rules: table.rules,
				assist: table.assist,
				status: table.status,
				players: playersOf(table),
				watchers: watchers.get(table.code) ?? 0,
			}));
	}

	/** The lobby to every deck browsing it, each listed the games it knows: worked out once for each version. */
	private broadcastLobby(gone?: WebSocket): void {
		const messages = new Map<number, unknown>();
		for (const socket of this.ctx.getWebSockets()) {
			const attachment = this.attachmentOf(socket);
			if (attachment === null || attachment.code !== null) {
				continue;
			}

			const version = versionOf(attachment);
			let message = messages.get(version);
			if (message === undefined) {
				message = { t: "lobby", tables: this.lobby(version, gone) };
				messages.set(version, message);
			}

			this.send(socket, message);
		}
	}

	/** Each seated player's view of their table, and each watcher's. */
	private sendTable(table: Table, gone?: WebSocket): void {
		const watchers = this.watchersOf(table.code, gone);
		for (const socket of this.socketsAt(table.code)) {
			const seat = this.attachmentOf(socket)?.seat;
			if (seat === 0 || seat === 1) {
				this.send(socket, viewOf(table, seat, watchers.length));
			}
		}

		for (const socket of watchers) {
			this.send(socket, viewOf(table, null, watchers.length));
		}
	}

	/** Someone started or stopped watching: everyone at the table, and a browsing lobby, sees the new count. */
	private watchersChanged(table: Table, gone?: WebSocket): void {
		this.sendTable(table, gone);
		if (!table.private) {
			this.broadcastLobby(gone);
		}
	}

	/** The shooter's aim, shot or settled table, to the other seat and to everyone watching. */
	private passOn(table: Table, from: Seat, message: unknown): void {
		this.sendToSeat(table, other(from), message);
		for (const socket of this.watchersOf(table.code)) {
			this.send(socket, message);
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

/** The protocol a socket's deck speaks; one connected before the hall asked is on the oldest it speaks. */
const versionOf = (attachment: Attachment): number => attachment.version ?? OLDEST_PROTOCOL;

/** Seat 0, the player who opened the table, breaks the first game; the break alternates after that. */
const breakerOf = (game: number): Seat => (game % 2 === 1 ? 0 : 1);

/** A rack seed both decks can hold in a signed 32-bit integer. */
function randomSeed(): number {
	const [value = 0] = crypto.getRandomValues(new Uint32Array(1));
	return value >>> 1;
}

/**
 * A stored table. Protocol 1 stored no rules or assist: every table then was
 * 8-ball, with aim assist. Before protocol 4 no table stored its physics, and
 * every one was of the first.
 */
function readTable(data: string): Table {
	const stored = JSON.parse(data) as Omit<Table, "rules" | "assist" | "physics"> & {
		rules?: Rules;
		assist?: boolean;
		physics?: number;
	};
	return {
		...stored,
		rules: stored.rules ?? "eight",
		assist: stored.assist ?? true,
		physics: stored.physics ?? 1,
	};
}

/**
 * Why a deck may not sit at or watch a table, or nothing when it may: the
 * table's game, or its physics, is newer than the deck's, which is told to
 * update; or its physics is older, from decks that would play every shot
 * differently.
 */
function deckMismatch(me: Attachment, table: Table): RefusalCode | undefined {
	const version = versionOf(me);
	if (!knowsRules(version, table.rules) || physicsOf(version) < table.physics) {
		return "needs-update";
	}

	return physicsOf(version) > table.physics ? "older-deck" : undefined;
}

const playersOf = (table: Table): (string | null)[] =>
	table.chairs.map((chair) => chair?.name ?? null);

/**
 * What one seat sees of its table, or a watcher, whose seat is null. Seat
 * tokens are never part of it.
 */
function viewOf(table: Table, seat: Seat | null, watchers: number) {
	return {
		t: "table",
		code: table.code,
		private: table.private,
		rules: table.rules,
		assist: table.assist,
		seat,
		watching: seat === null,
		players: playersOf(table),
		away: table.chairs.map((chair) => chair?.away != null),
		watchers,
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
