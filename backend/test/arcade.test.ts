import { env, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { BOARD_DEPTH, MAX_SCORE, MAX_SUBMISSIONS } from "../src/data/arcade.js";
import { asAdmin, clearPublicReadCache, jsonHeaders, resetTables } from "./helpers.js";

/**
 * The arcade's Grid-wide high-score boards.
 *
 * Two things are pinned here. The first is the traffic shape the feature was
 * built around: the boards are read through the catalogue and nowhere else,
 * and a score that does not make a board writes nothing. The second is that
 * the one anonymous write this Worker has cannot be used to grow the database
 * or the catalogue — unknown boards, malformed scores and repeats are all
 * turned away or absorbed.
 */

interface Scoreline {
	initials: string;
	score: number;
}

interface SubmitBody {
	results: { id: string; outcome: string }[];
}

/**
 * A caller of its own for each test.
 *
 * The submit limiter is live in the test pool and counts per address, so tests
 * sharing one would start refusing each other after the sixth post.
 */
let callers = 0;
const newCaller = (): Record<string, string> => {
	callers += 1;

	return { "cf-connecting-ip": `198.51.100.${callers}` };
};

/** A well-formed submission id: 32 hex characters. */
const idFor = (n: number): string => n.toString(16).padStart(32, "0");

const utcDay = (daysBack = 0): string =>
	new Date(Date.now() - daysBack * 86_400_000).toISOString().slice(0, 10).replaceAll("-", "");

const submit = (scores: unknown, caller: Record<string, string>): Promise<Response> =>
	SELF.fetch("https://example.com/v1/arcade/scores", {
		method: "POST",
		headers: jsonHeaders(caller),
		body: JSON.stringify({ scores }),
	});

const outcomes = async (response: Response): Promise<Record<string, string>> => {
	const body = (await response.json()) as SubmitBody;

	return Object.fromEntries(body.results.map((result) => [result.id, result.outcome]));
};

/** The boards as a deck sees them: through the catalogue, past the edge cache. */
const boards = async (caller: Record<string, string>): Promise<Record<string, Scoreline[]>> => {
	await clearPublicReadCache();
	const response = await SELF.fetch("https://example.com/v1/catalog", { headers: caller });
	const body = (await response.json()) as { arcade: { boards: Record<string, Scoreline[]> } };

	return body.arcade.boards;
};

const rowCount = async (): Promise<number> => {
	const row = await env.DB.prepare("SELECT COUNT(*) AS n FROM arcade_scores").first<{
		n: number;
	}>();

	return row?.n ?? 0;
};

/** Puts a full board in place directly: scores 1000, 990, ... down to 910. */
async function fillBoard(board: string): Promise<void> {
	await env.DB.batch(
		Array.from({ length: BOARD_DEPTH }, (_, index) =>
			env.DB.prepare(
				"INSERT INTO arcade_scores (id, board, initials, score, created_at) VALUES (?, ?, ?, ?, ?)",
			).bind(idFor(0xf000 + index), board, "FULL", 1000 - index * 10, "2026-01-01T00:00:00.000Z"),
		),
	);
}

describe("posting a score", () => {
	beforeEach(resetTables);

	it("puts it on the board the catalogue carries", async () => {
		const caller = newCaller();
		const response = await submit(
			[{ id: idFor(1), board: "firewall", initials: "CRPNKM", score: 26406 }],
			caller,
		);

		expect(response.status).toBe(200);
		expect(response.headers.get("cache-control")).toBe("no-store");
		expect(await outcomes(response)).toEqual({ [idFor(1)]: "placed" });
		expect(await boards(caller)).toEqual({ firewall: [{ initials: "CRPNKM", score: 26406 }] });
	});

	it("ranks a board best first", async () => {
		const caller = newCaller();
		await submit(
			[
				{ id: idFor(1), board: "firewall", initials: "LOW", score: 100 },
				{ id: idFor(2), board: "firewall", initials: "TOP", score: 900 },
				{ id: idFor(3), board: "firewall", initials: "MID", score: 500 },
			],
			caller,
		);

		expect((await boards(caller)).firewall?.map((line) => line.initials)).toEqual([
			"TOP",
			"MID",
			"LOW",
		]);
	});

	it("keeps the earlier of two equal scores ahead", async () => {
		await env.DB.batch([
			env.DB.prepare(
				"INSERT INTO arcade_scores (id, board, initials, score, created_at) VALUES (?, ?, ?, ?, ?)",
			).bind(idFor(1), "firewall", "LATER", 500, "2026-02-01T00:00:00.000Z"),
			env.DB.prepare(
				"INSERT INTO arcade_scores (id, board, initials, score, created_at) VALUES (?, ?, ?, ?, ?)",
			).bind(idFor(2), "firewall", "FIRST", 500, "2026-01-01T00:00:00.000Z"),
		]);

		expect((await boards(newCaller())).firewall?.map((line) => line.initials)).toEqual([
			"FIRST",
			"LATER",
		]);
	});

	it("sends a deck's whole backlog in one request, across boards", async () => {
		const caller = newCaller();
		const response = await submit(
			[
				{ id: idFor(1), board: "firewall", initials: "AAA", score: 10 },
				{ id: idFor(2), board: "no-fly-zone", initials: "AAA", score: 20 },
				{ id: idFor(3), board: "night-circuit", initials: "AAA", score: 30 },
			],
			caller,
		);

		expect(Object.values(await outcomes(response))).toEqual(["placed", "placed", "placed"]);
		expect(Object.keys(await boards(caller))).toEqual(["firewall", "night-circuit", "no-fly-zone"]);
	});

	it("does not put a retried submission on the board twice", async () => {
		const caller = newCaller();
		const score = [{ id: idFor(1), board: "firewall", initials: "AAA", score: 10 }];
		await submit(score, caller);

		// The deck never saw the first answer and sends the same id again.
		expect(await outcomes(await submit(score, caller))).toEqual({ [idFor(1)]: "placed" });
		expect(await rowCount()).toBe(1);
	});
});

describe("a full board", () => {
	beforeEach(async () => {
		await resetTables();
		await fillBoard("firewall");
	});

	it("turns away a score that does not beat its last place, and writes nothing", async () => {
		const response = await submit(
			// 910 is the last place. Matching it is not beating it.
			[{ id: idFor(1), board: "firewall", initials: "AAA", score: 910 }],
			newCaller(),
		);

		expect(await outcomes(response)).toEqual({ [idFor(1)]: "not-placed" });
		expect(await rowCount()).toBe(BOARD_DEPTH);
	});

	it("drops its last place when a better score arrives", async () => {
		const caller = newCaller();
		const response = await submit(
			[{ id: idFor(1), board: "firewall", initials: "NEW", score: 955 }],
			caller,
		);

		expect(await outcomes(response)).toEqual({ [idFor(1)]: "placed" });
		expect(await rowCount()).toBe(BOARD_DEPTH);

		const scores = (await boards(caller)).firewall?.map((line) => line.score) ?? [];
		expect(scores).toHaveLength(BOARD_DEPTH);
		expect(scores).toContain(955);
		expect(scores).not.toContain(910);
	});

	it("reports a score pushed back off by the same batch as not placed", async () => {
		// Ten better scores follow the first one, so by the end it has gone.
		const batch = [
			{ id: idFor(1), board: "firewall", initials: "GONE", score: 1001 },
			...Array.from({ length: BOARD_DEPTH }, (_, index) => ({
				id: idFor(100 + index),
				board: "firewall",
				initials: "STAY",
				score: 2000 + index,
			})),
		];

		const result = await outcomes(await submit(batch, newCaller()));

		expect(result[idFor(1)]).toBe("not-placed");
		expect(result[idFor(100)]).toBe("placed");
		expect(await rowCount()).toBe(BOARD_DEPTH);
	});
});

describe("what the relay refuses", () => {
	beforeEach(resetTables);

	it.each([
		["a board no game keeps", { board: "made-up-game" }],
		["a per-track board, now that every game keeps one", { board: "beat-link/low-signal/easy" }],
		["a daily board whose day has passed", { board: `night-circuit-daily-${utcDay(3)}` }],
		["a lower-case name", { initials: "abc" }],
		["a name over six characters", { initials: "TOOLONG" }],
		["an empty name", { initials: "" }],
		["a name with markup in it", { initials: "<B>" }],
		["a score of zero", { score: 0 }],
		["a negative score", { score: -5 }],
		["a fractional score", { score: 10.5 }],
		["a score over the ceiling", { score: MAX_SCORE + 1 }],
		["a score sent as text", { score: "100" }],
	])("rejects %s, and writes nothing", async (_label, override) => {
		const response = await submit(
			[{ id: idFor(1), board: "firewall", initials: "AAA", score: 100, ...override }],
			newCaller(),
		);

		expect(response.status).toBe(200);
		expect(await outcomes(response)).toEqual({ [idFor(1)]: "rejected" });
		expect(await rowCount()).toBe(0);
	});

	it("rejects one bad score without losing the rest of the batch", async () => {
		const response = await submit(
			[
				{ id: idFor(1), board: "made-up-game", initials: "AAA", score: 100 },
				{ id: idFor(2), board: "firewall", initials: "AAA", score: 100 },
			],
			newCaller(),
		);

		expect(await outcomes(response)).toEqual({ [idFor(1)]: "rejected", [idFor(2)]: "placed" });
	});

	it.each([
		["no scores at all", {}],
		["an empty list", { scores: [] }],
		["scores that are not a list", { scores: "firewall" }],
		[
			"more scores than one request may carry",
			{
				scores: Array.from({ length: MAX_SUBMISSIONS + 1 }, (_, index) => ({
					id: idFor(index + 1),
					board: "firewall",
					initials: "AAA",
					score: 100,
				})),
			},
		],
		["a score that is not an object", { scores: [42] }],
		["a score with no id", { scores: [{ board: "firewall", initials: "AAA", score: 100 }] }],
		[
			"an id that is not a submission id",
			{ scores: [{ id: "../../etc", board: "firewall", initials: "AAA", score: 100 }] },
		],
	])("refuses %s outright", async (_label, body) => {
		const response = await SELF.fetch("https://example.com/v1/arcade/scores", {
			method: "POST",
			headers: jsonHeaders(newCaller()),
			body: JSON.stringify(body),
		});

		expect(response.status).toBe(400);
		expect(await response.json()).toMatchObject({ error: { code: "BAD_REQUEST" } });
		expect(await rowCount()).toBe(0);
	});

	it("throttles a caller that keeps posting", async () => {
		const caller = newCaller();
		const statuses: number[] = [];
		for (let attempt = 0; attempt < 7; attempt += 1) {
			const response = await submit(
				[{ id: idFor(attempt + 1), board: "firewall", initials: "AAA", score: 100 + attempt }],
				caller,
			);
			statuses.push(response.status);
		}

		expect(statuses.slice(0, 6)).toEqual([200, 200, 200, 200, 200, 200]);
		expect(statuses[6]).toBe(429);
		expect(await rowCount()).toBe(6);
	});
});

describe("the daily challenge", () => {
	beforeEach(resetTables);

	it("takes scores for today and yesterday", async () => {
		const response = await submit(
			[
				{ id: idFor(1), board: `night-circuit-daily-${utcDay()}`, initials: "AAA", score: 100 },
				{ id: idFor(2), board: `night-circuit-daily-${utcDay(1)}`, initials: "AAA", score: 100 },
			],
			newCaller(),
		);

		expect(Object.values(await outcomes(response))).toEqual(["placed", "placed"]);
	});

	it("stops showing a board once its day has passed, then deletes it on the next write", async () => {
		const caller = newCaller();
		const expired = `night-circuit-daily-${utcDay(3)}`;
		await env.DB.prepare(
			"INSERT INTO arcade_scores (id, board, initials, score, created_at) VALUES (?, ?, ?, ?, ?)",
		)
			.bind(idFor(1), expired, "OLD", 100, "2026-01-01T00:00:00.000Z")
			.run();

		expect(await boards(caller)).toEqual({});
		expect(await rowCount()).toBe(1);

		await submit([{ id: idFor(2), board: "firewall", initials: "AAA", score: 100 }], caller);

		expect(await rowCount()).toBe(1);
		expect(Object.keys(await boards(caller))).toEqual(["firewall"]);
	});
});

describe("the boards in the catalogue", () => {
	beforeEach(resetTables);

	it("are an empty set before anyone has played", async () => {
		expect(await boards(newCaller())).toEqual({});
	});

	it("carry a name and a score per line and nothing else", async () => {
		const caller = newCaller();
		await submit([{ id: idFor(1), board: "firewall", initials: "AAA", score: 100 }], caller);

		const [line] = (await boards(caller)).firewall ?? [];

		expect(Object.keys(line ?? {})).toEqual(["initials", "score"]);
	});

	it("leave the catalogue's tag alone while no board changes", async () => {
		// The deck's hourly check has to keep answering 304 between scores, or
		// every deck would download the catalogue every hour for nothing.
		const caller = newCaller();
		await submit([{ id: idFor(1), board: "firewall", initials: "AAA", score: 100 }], caller);

		await clearPublicReadCache();
		const first = await SELF.fetch("https://example.com/v1/catalog", { headers: caller });
		const tag = first.headers.get("etag") ?? "";

		await clearPublicReadCache();
		const second = await SELF.fetch("https://example.com/v1/catalog", {
			headers: { ...caller, "if-none-match": tag },
		});

		expect(second.status).toBe(304);
	});

	it("say how deep a board runs, so a deck knows the bar without a constant of its own", async () => {
		await clearPublicReadCache();
		const response = await SELF.fetch("https://example.com/v1/catalog", { headers: newCaller() });
		const body = (await response.json()) as { arcade: { depth: number } };

		expect(body.arcade.depth).toBe(BOARD_DEPTH);
	});

	it("never take the rest of the catalogue down with them", async () => {
		// A deploy that ran ahead of its migration: the table is not there.
		await env.DB.exec("ALTER TABLE arcade_scores RENAME TO arcade_scores_missing");
		try {
			await clearPublicReadCache();
			const response = await SELF.fetch("https://example.com/v1/catalog", {
				headers: newCaller(),
			});
			const body = (await response.json()) as Record<string, unknown>;

			expect(response.status).toBe(200);
			expect(body.menu).toBeDefined();
			// Absent rather than empty: a deck must not post to a relay that
			// cannot keep the score.
			expect(body.arcade).toBeUndefined();
		} finally {
			await env.DB.exec("ALTER TABLE arcade_scores_missing RENAME TO arcade_scores");
			await clearPublicReadCache();
		}
	});

	it("have no read route of their own", async () => {
		const response = await SELF.fetch("https://example.com/v1/arcade/scores", {
			headers: newCaller(),
		});

		expect(response.status).toBe(405);
		expect(response.headers.get("allow")).toBe("POST, OPTIONS");
	});
});

describe("moderation", () => {
	beforeEach(resetTables);

	it.each([
		["GET", "/v1/admin/arcade/scores"],
		["DELETE", `/v1/admin/arcade/scores/${idFor(1)}`],
	])("is closed to an unauthenticated %s %s", async (method, path) => {
		const response = await SELF.fetch(`https://example.com${path}`, { method });

		expect(response.status).toBe(401);
	});

	it("lists every score with its id", async () => {
		await submit([{ id: idFor(1), board: "firewall", initials: "AAA", score: 100 }], newCaller());

		const response = await SELF.fetch("https://example.com/v1/admin/arcade/scores", {
			headers: asAdmin(),
		});

		expect(response.status).toBe(200);
		expect(await response.json()).toMatchObject({
			items: [{ id: idFor(1), board: "firewall", initials: "AAA", score: 100 }],
		});
	});

	it("removes a score, and the next one down takes its place", async () => {
		const caller = newCaller();
		await submit(
			[
				{ id: idFor(1), board: "firewall", initials: "FAKE", score: 999999 },
				{ id: idFor(2), board: "firewall", initials: "REAL", score: 100 },
			],
			caller,
		);

		const response = await SELF.fetch(`https://example.com/v1/admin/arcade/scores/${idFor(1)}`, {
			method: "DELETE",
			headers: asAdmin(),
		});

		expect(response.status).toBe(200);
		expect(await boards(caller)).toEqual({ firewall: [{ initials: "REAL", score: 100 }] });
	});

	it("answers 404 for a score that is not on any board", async () => {
		const response = await SELF.fetch(`https://example.com/v1/admin/arcade/scores/${idFor(9)}`, {
			method: "DELETE",
			headers: asAdmin(),
		});

		expect(response.status).toBe(404);
		expect(await response.json()).toMatchObject({ error: { code: "SCORE_NOT_FOUND" } });
	});

	it("refuses an id that is not a submission id", async () => {
		const response = await SELF.fetch("https://example.com/v1/admin/arcade/scores/not-an-id", {
			method: "DELETE",
			headers: asAdmin(),
		});

		expect(response.status).toBe(400);
	});
});
