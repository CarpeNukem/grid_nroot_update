/**
 * The arcade's Grid-wide high-score boards.
 *
 * Built to cost as little as the venue's free plan can stand:
 *
 *  - Reading the boards is not a request at all. They ride in `/v1/catalog`,
 *    which every deck already fetches, so the arcade adds no polling.
 *  - Writing is one request, and a deck only makes it when a score would make
 *    a board. Scores it could not send go in the same request next time.
 *  - A board keeps its best {@link BOARD_DEPTH} and nothing else, so the table
 *    never grows and the catalogue's extra query stays under a hundred rows.
 *
 * Scores come from an untrusted client: the games run on the player's machine
 * and the module that runs them can be decompiled. What is enforced here is
 * shape, not honesty — a known board, a cabinet name, a whole positive score
 * under a ceiling — and anything that gets through can be removed by an
 * administrator (see the admin routes).
 */

/** How many scores a board keeps. The deck shows the top five; the rest move up after a removal. */
export const BOARD_DEPTH = 10;

/** Most scores one submission may carry: a deck's whole backlog after time offline fits in one request. */
export const MAX_SUBMISSIONS = 25;

/** A ceiling well above anything the games award, so a corrupted or forged number cannot top a board forever. */
export const MAX_SCORE = 999_999_999;

/**
 * The boards the games keep: one per game, and nothing else.
 *
 * An allowlist rather than a pattern, so a client cannot open a new board per
 * request and grow the table and the catalogue without limit. A game added to
 * the arcade needs its id added here, and a Worker deploy, before its scores
 * are accepted; until then the deck is told they were rejected, treats that as
 * final, and keeps them on its own machine.
 */
const GAME_BOARDS: ReadonlySet<string> = new Set([
	"escape-from-the-grid",
	"shots-fired",
	"firewall",
	"no-fly-zone",
	"night-circuit",
	"last-call",
	"redline-rush",
]);

/** NIGHT CIRCUIT's daily challenge keeps a board per UTC day: `night-circuit-daily-20261110`. */
const DAILY_PREFIX = "night-circuit-daily-";
const DAILY_BOARD = /^night-circuit-daily-\d{8}$/;

/** The name typed at the cabinet: up to six capitals or digits, or dashes for a name left blank. */
const INITIALS = /^[A-Z0-9-]{1,6}$/;

/** The deck's submission id: a GUID without its dashes. */
const SUBMISSION_ID = /^[0-9a-f]{32}$/;

/** `yyyymmdd` for a UTC day, `daysBack` before `now`. */
function utcDay(now: Date, daysBack = 0): string {
	const day = new Date(now.getTime() - daysBack * 86_400_000);

	return day.toISOString().slice(0, 10).replaceAll("-", "");
}

/**
 * The daily boards still live: today and yesterday, UTC.
 *
 * Yesterday stays because a run started before midnight ends after it, and a
 * deck whose clock is a little behind is still posting to the day it thinks
 * it is.
 */
function liveDailyBoards(now: Date): ReadonlySet<string> {
	return new Set([`${DAILY_PREFIX}${utcDay(now)}`, `${DAILY_PREFIX}${utcDay(now, 1)}`]);
}

/** Whether a board is shown and takes scores right now: a game's board, or a live daily one. */
export function isOpenBoard(board: string, now: Date): boolean {
	return GAME_BOARDS.has(board) || (DAILY_BOARD.test(board) && liveDailyBoards(now).has(board));
}

export const isSubmissionId = (value: unknown): value is string =>
	typeof value === "string" && SUBMISSION_ID.test(value);

export const isInitials = (value: unknown): value is string =>
	typeof value === "string" && INITIALS.test(value);

export const isScore = (value: unknown): value is number =>
	typeof value === "number" && Number.isInteger(value) && value > 0 && value <= MAX_SCORE;

// -------------------------------------------------------------------- rows

export interface ArcadeScoreRow {
	readonly id: string;
	readonly board: string;
	readonly initials: string;
	readonly score: number;
	readonly created_at: string;
}

/** One line on a board, as the catalogue carries it. The row's id and time stay behind. */
export interface PublicArcadeScore {
	readonly initials: string;
	readonly score: number;
}

/** Board id to its scores, best first. */
export type PublicArcadeBoards = Readonly<Record<string, readonly PublicArcadeScore[]>>;

/** The order a board ranks in. Of two equal scores, the one set first stays ahead. */
const RANK_ORDER = "score DESC, created_at ASC, id ASC";

/** Every row on every board, best first within a board. At most a board's depth per board. */
export async function listAllScores(db: D1Database): Promise<ArcadeScoreRow[]> {
	const { results } = await db
		.prepare(
			`SELECT id, board, initials, score, created_at FROM arcade_scores ORDER BY board ASC, ${RANK_ORDER}`,
		)
		.all<ArcadeScoreRow>();

	return results;
}

/**
 * The boards as the catalogue publishes them.
 *
 * Only what a cabinet draws: a name and a number per line. Ids are left out so
 * the catalogue stays small, and so does the time — the array order is the
 * ranking. The keys are sorted because the catalogue's tag is a hash of its
 * bytes, and an expired daily board is dropped here rather than waiting for
 * the next write to delete it.
 */
export function toPublicBoards(rows: readonly ArcadeScoreRow[], now: Date): PublicArcadeBoards {
	const boards: Record<string, PublicArcadeScore[]> = {};
	for (const row of rows) {
		if (!isOpenBoard(row.board, now)) {
			continue;
		}

		const board = boards[row.board] ?? [];
		if (board.length < BOARD_DEPTH) {
			board.push({ initials: row.initials, score: row.score });
		}
		boards[row.board] = board;
	}

	const sorted: Record<string, PublicArcadeScore[]> = {};
	for (const key of Object.keys(boards).sort()) {
		sorted[key] = boards[key] as PublicArcadeScore[];
	}

	return sorted;
}

export interface ValidScore {
	readonly id: string;
	readonly board: string;
	readonly initials: string;
	readonly score: number;
}

/**
 * Writes a batch of scores in one round trip, and says which are on a board.
 *
 * For each score, two statements:
 *  - an insert that only happens when the score makes the board, so a score
 *    that does not costs a read and no write. `OR IGNORE` makes a retried
 *    submission a no-op rather than a second entry;
 *  - a trim back to the board's depth, which is what keeps the table small.
 *
 * Then daily boards past their day are dropped, and the submitted ids are
 * looked up. D1 runs a batch as one transaction, so two decks posting to the
 * same board at once cannot both slip under its last place.
 */
export async function submitScores(
	db: D1Database,
	scores: readonly ValidScore[],
	now: Date,
): Promise<ReadonlySet<string>> {
	const createdAt = now.toISOString();
	const statements: D1PreparedStatement[] = [];

	for (const entry of scores) {
		statements.push(
			db
				.prepare(
					`INSERT OR IGNORE INTO arcade_scores (id, board, initials, score, created_at)
					SELECT ?1, ?2, ?3, ?4, ?5
					WHERE (SELECT COUNT(*) FROM arcade_scores WHERE board = ?2) < ?6
						OR ?4 > (SELECT score FROM arcade_scores WHERE board = ?2
							ORDER BY ${RANK_ORDER} LIMIT 1 OFFSET ?6 - 1)`,
				)
				.bind(entry.id, entry.board, entry.initials, entry.score, createdAt, BOARD_DEPTH),
			db
				.prepare(
					`DELETE FROM arcade_scores WHERE board = ?1 AND id NOT IN (
						SELECT id FROM arcade_scores WHERE board = ?1 ORDER BY ${RANK_ORDER} LIMIT ?2)`,
				)
				.bind(entry.board, BOARD_DEPTH),
		);
	}

	// The prefix test matters: a bare `board < ?` would also match every game
	// board that sorts before the date.
	statements.push(
		db
			.prepare("DELETE FROM arcade_scores WHERE board LIKE ?1 AND board < ?2")
			.bind(`${DAILY_PREFIX}%`, `${DAILY_PREFIX}${utcDay(now, 1)}`),
	);

	// Asked last, after every trim, so a score this same batch pushed back off
	// its board is reported as not placed.
	const placeholders = scores.map((_, index) => `?${index + 1}`).join(", ");
	statements.push(
		db
			.prepare(`SELECT id FROM arcade_scores WHERE id IN (${placeholders})`)
			.bind(...scores.map((entry) => entry.id)),
	);

	const results = await db.batch<{ id: string }>(statements);

	return new Set((results.at(-1)?.results ?? []).map((row) => row.id));
}

export async function deleteScore(db: D1Database, id: string): Promise<boolean> {
	const result = await db.prepare("DELETE FROM arcade_scores WHERE id = ?1").bind(id).run();

	return result.meta.changes > 0;
}
