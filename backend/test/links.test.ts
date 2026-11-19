import { env, fetchMock, SELF } from "cloudflare:test";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { destinationFor, type ShortLinkRow } from "../src/data/links.js";
import { asAdmin, jsonHeaders, resetTables } from "./helpers.js";

/**
 * Short links: grid.nroot.io/s/<code>.
 *
 * Pinned here: only an administrator can make one, nothing but a plain web
 * address can be shortened, and a link to a Discord attachment keeps working
 * after the copied address expires — re-signed through Discord at most once
 * per signature, and never retried on every click when Discord refuses.
 */

interface AdminLink {
	code: string;
	target: string;
	short_url: string;
	discord: boolean;
	existing: boolean;
	created_by: string;
}

/**
 * A caller of its own for each redirect.
 *
 * The public read limiter is live in the test pool and counts per address.
 */
let callers = 0;
const newCaller = (): Record<string, string> => {
	callers += 1;

	return { "cf-connecting-ip": `192.0.2.${callers}` };
};

const shorten = (body: unknown, headers: Record<string, string> = asAdmin()): Promise<Response> =>
	SELF.fetch("https://example.com/v1/admin/links", {
		method: "POST",
		headers: jsonHeaders(headers),
		body: JSON.stringify(body),
	});

const made = async (body: unknown): Promise<AdminLink> => {
	const response = await shorten(body);
	expect(response.status).toBe(201);

	return (await response.json()) as AdminLink;
};

const follow = (code: string): Promise<Response> =>
	SELF.fetch(`https://example.com/s/${code}`, { redirect: "manual", headers: newCaller() });

/** Unix seconds as Discord writes them in a signature: hex. */
const hex = (date: Date): string => Math.floor(date.getTime() / 1000).toString(16);

const hoursFromNow = (hours: number): Date => new Date(Date.now() + hours * 3_600_000);

/** A signed attachment address, expiring when given. */
function discordUrl(
	expiresAt: Date,
	{
		host = "cdn.discordapp.com",
		file = "flyer.png",
		signature = "ab",
		extra = "",
	}: { host?: string; file?: string; signature?: string; extra?: string } = {},
): string {
	const issued = new Date(expiresAt.getTime() - 86_400_000);

	return `https://${host}/attachments/1100000000000000000/1200000000000000000/${file}?ex=${hex(expiresAt)}&is=${hex(issued)}&hm=${signature.repeat(32)}${extra}`;
}

const REFRESH_PATH = "/api/v10/attachments/refresh-urls";

/** Answers the next call to Discord's re-signing endpoint for `original`. */
function discordAnswers(original: string, status: number, body: unknown): void {
	fetchMock
		.get("https://discord.com")
		.intercept({
			path: REFRESH_PATH,
			method: "POST",
			headers: { authorization: "Bot test-bot-token" },
			body: (sent: string) => JSON.parse(sent).attachment_urls?.[0] === original,
		})
		.reply(status, JSON.stringify(body), { headers: { "content-type": "application/json" } });
}

/**
 * Runs `clicks` with a re-signing answer waiting that must go unused.
 *
 * A call that reached the endpoint with no answer set up would fail as
 * unreachable, and the link would fall back to the address it had — the same
 * redirect a test of "no call" expects. So instead an answer is left out for
 * it, the test checks it is still there afterwards, and then spends it.
 */
async function withoutDiscordCall(original: string, clicks: () => Promise<void>): Promise<void> {
	const trap = discordUrl(hoursFromNow(24), { file: "trap.png" });
	discordAnswers(original, 200, { refreshed_urls: [{ original, refreshed: trap }] });

	await clicks();
	expect(fetchMock.pendingInterceptors()).toHaveLength(1);

	await fetch(`https://discord.com${REFRESH_PATH}`, {
		method: "POST",
		headers: { authorization: "Bot test-bot-token" },
		body: JSON.stringify({ attachment_urls: [original] }),
	});
}

const storedRefresh = async (code: string): Promise<string | null> => {
	const row = await env.DB.prepare("SELECT refreshed_url FROM short_links WHERE code = ?1")
		.bind(code)
		.first<{ refreshed_url: string | null }>();

	return row?.refreshed_url ?? null;
};

beforeAll(() => {
	// No test here may reach the real Discord: any call without an answer set
	// up for it fails the request, and so the test.
	fetchMock.activate();
	fetchMock.disableNetConnect();
});

afterEach(() => {
	fetchMock.assertNoPendingInterceptors();
});

afterAll(() => {
	fetchMock.deactivate();
});

beforeEach(resetTables);

describe("making a short link", () => {
	it("makes a six-character code that redirects to the address", async () => {
		const link = await made({ url: "https://example.org/menu?night=friday" });

		expect(link.code).toMatch(/^[a-km-np-z2-9]{6}$/);
		expect(link.short_url).toBe(`http://127.0.0.1:8787/s/${link.code}`);
		expect(link.target).toBe("https://example.org/menu?night=friday");
		expect(link.discord).toBe(false);
		expect(link.created_by).toBe("editor@thegrid.test");

		const response = await follow(link.code);
		expect(response.status).toBe(302);
		expect(response.headers.get("location")).toBe("https://example.org/menu?night=friday");
		expect(response.headers.get("referrer-policy")).toBe("no-referrer");
		// Short enough that a re-signed Discord address cannot expire behind it.
		expect(response.headers.get("cache-control")).toBe("private, max-age=300");
	});

	it("finds a code whatever its case", async () => {
		const link = await made({ url: "https://example.org/" });

		const response = await follow(link.code.toUpperCase());
		expect(response.status).toBe(302);
		expect(response.headers.get("location")).toBe("https://example.org/");
	});

	it("takes a chosen code, once", async () => {
		const link = await made({ url: "https://example.org/a", code: "Rooftop-Flyer" });
		expect(link.code).toBe("rooftop-flyer");
		expect(link.short_url).toBe("http://127.0.0.1:8787/s/rooftop-flyer");

		const taken = await shorten({ url: "https://example.org/b", code: "rooftop-flyer" });
		expect(taken.status).toBe(409);

		const response = await follow("rooftop-flyer");
		expect(response.headers.get("location")).toBe("https://example.org/a");
	});

	it.each(["-dash-first", "dash-last-", "has space", "under_score", "x".repeat(41), 42])(
		"refuses %j as a code",
		async (code) => {
			const response = await shorten({ url: "https://example.org/", code });
			expect(response.status).toBe(400);
		},
	);

	it.each([
		"javascript:alert(1)",
		"data:text/html,hello",
		"/relative/path",
		"ftp://example.org/file",
		"https://user:secret@example.org/",
		"",
		"   ",
		`https://example.org/${"a".repeat(2100)}`,
		"http://127.0.0.1:8787/s/abcdef",
		42,
		null,
	])("refuses %j as an address", async (url) => {
		const response = await shorten({ url });
		expect(response.status).toBe(400);
	});

	it("refuses fields it does not know", async () => {
		const response = await shorten({ url: "https://example.org/", expires: "never" });
		expect(response.status).toBe(400);
	});

	it("hands back the link a Discord file already has, whatever its signature", async () => {
		const first = await made({ url: discordUrl(hoursFromNow(20)) });
		expect(first.discord).toBe(true);

		// The same file copied again a day later: new signature, same attachment.
		const again = await shorten({ url: discordUrl(hoursFromNow(44), { signature: "cd" }) });
		expect(again.status).toBe(200);
		const repeat = (await again.json()) as AdminLink;
		expect(repeat.existing).toBe(true);
		expect(repeat.code).toBe(first.code);

		const other = await made({ url: discordUrl(hoursFromNow(20), { file: "menu.png" }) });
		expect(other.code).not.toBe(first.code);
	});

	it("gives a chosen code even to an address that already has a link", async () => {
		const first = await made({ url: "https://example.org/" });
		const named = await made({ url: "https://example.org/", code: "home" });

		expect(named.code).toBe("home");
		expect(named.code).not.toBe(first.code);
	});
});

describe("managing short links", () => {
	it("is for administrators only", async () => {
		const create = await shorten({ url: "https://example.org/" }, {});
		expect(create.status).toBe(401);

		const list = await SELF.fetch("https://example.com/v1/admin/links");
		expect(list.status).toBe(401);

		const remove = await SELF.fetch("https://example.com/v1/admin/links/abcdef", {
			method: "DELETE",
		});
		expect(remove.status).toBe(401);
	});

	it("lists newest first, and says Discord links will be re-signed", async () => {
		await env.DB.batch(
			[
				["older1", "2026-11-01T00:00:00.000Z"],
				["newer2", "2026-11-02T00:00:00.000Z"],
			].map(([code, createdAt]) =>
				env.DB.prepare(
					`INSERT INTO short_links (code, target, canonical, created_by, created_at)
					VALUES (?1, 'https://example.org/', 'https://example.org/', 'editor@thegrid.test', ?2)`,
				).bind(code, createdAt),
			),
		);

		const response = await SELF.fetch("https://example.com/v1/admin/links", {
			headers: asAdmin(),
		});
		expect(response.status).toBe(200);
		const body = (await response.json()) as { items: AdminLink[]; discordRefresh: boolean };

		expect(body.items.map((item) => item.code)).toEqual(["newer2", "older1"]);
		expect(body.items[0]?.short_url).toBe("http://127.0.0.1:8787/s/newer2");
		expect(body.discordRefresh).toBe(true);
	});

	it("deletes a link, after which it does not exist", async () => {
		const link = await made({ url: "https://example.org/" });

		const remove = () =>
			SELF.fetch(`https://example.com/v1/admin/links/${link.code}`, {
				method: "DELETE",
				headers: asAdmin(),
			});

		expect((await remove()).status).toBe(200);
		expect((await remove()).status).toBe(404);

		const response = await follow(link.code);
		expect(response.status).toBe(404);
		expect(((await response.json()) as { error: { code: string } }).error.code).toBe(
			"LINK_NOT_FOUND",
		);
	});

	it.each(["nothere", "not_a_code", "-x-"])("answers 404 for /s/%s", async (code) => {
		const response = await follow(code);
		expect(response.status).toBe(404);
	});

	it("hands out production links on the public hostname", () => {
		const production = env.TEST_WRANGLER_CONFIG.env?.production?.vars ?? {};
		expect(production.SHORT_LINK_BASE_URL).toBe("https://grid.nroot.io/s");
	});
});

describe("Discord attachments", () => {
	it("goes straight to a signature with time left, without asking Discord", async () => {
		const pasted = discordUrl(hoursFromNow(20));
		const link = await made({ url: pasted });

		await withoutDiscordCall(pasted, async () => {
			const response = await follow(link.code);
			expect(response.headers.get("location")).toBe(pasted);
		});
	});

	it("re-signs an expired address once, then reuses it", async () => {
		const pasted = discordUrl(hoursFromNow(-30));
		const fresh = discordUrl(hoursFromNow(24), { signature: "ef" });
		const link = await made({ url: pasted });

		discordAnswers(pasted, 200, { refreshed_urls: [{ original: pasted, refreshed: fresh }] });
		const first = await follow(link.code);
		expect(first.status).toBe(302);
		expect(first.headers.get("location")).toBe(fresh);
		expect(await storedRefresh(link.code)).toBe(fresh);

		await withoutDiscordCall(pasted, async () => {
			const second = await follow(link.code);
			expect(second.headers.get("location")).toBe(fresh);
		});
	});

	it("re-signs ahead of time when under an hour is left", async () => {
		const pasted = discordUrl(new Date(Date.now() + 20 * 60_000));
		const fresh = discordUrl(hoursFromNow(24), { signature: "ef" });
		const link = await made({ url: pasted });

		discordAnswers(pasted, 200, { refreshed_urls: [{ original: pasted, refreshed: fresh }] });
		const response = await follow(link.code);
		expect(response.headers.get("location")).toBe(fresh);
	});

	it("keeps the media proxy's options if Discord's answer drops them", async () => {
		const pasted = discordUrl(hoursFromNow(-30), {
			host: "media.discordapp.net",
			extra: "&width=640&format=webp",
		});
		const fresh = discordUrl(hoursFromNow(24), { host: "media.discordapp.net", signature: "ef" });
		const link = await made({ url: pasted });

		discordAnswers(pasted, 200, { refreshed_urls: [{ original: pasted, refreshed: fresh }] });
		const location = new URL((await follow(link.code)).headers.get("location") ?? "");

		expect(location.searchParams.get("width")).toBe("640");
		expect(location.searchParams.get("format")).toBe("webp");
		expect(location.searchParams.get("hm")).toBe("ef".repeat(32));
	});

	it("falls back to the last address when Discord refuses, and does not ask again at once", async () => {
		const pasted = discordUrl(hoursFromNow(-30));
		const link = await made({ url: pasted });

		discordAnswers(pasted, 401, { message: "401: Unauthorized", code: 0 });
		const first = await follow(link.code);
		expect(first.status).toBe(302);
		expect(first.headers.get("location")).toBe(pasted);
		expect(await storedRefresh(link.code)).toBeNull();

		// Backing off: a second click inside the window makes no call at all.
		await withoutDiscordCall(pasted, async () => {
			const second = await follow(link.code);
			expect(second.headers.get("location")).toBe(pasted);
		});
	});

	it("will not redirect anywhere but Discord's CDN, whatever the answer says", async () => {
		const pasted = discordUrl(hoursFromNow(-30));
		const link = await made({ url: pasted });

		discordAnswers(pasted, 200, {
			refreshed_urls: [
				{ original: pasted, refreshed: "https://elsewhere.example/flyer.png?ex=ffffffff" },
			],
		});
		const response = await follow(link.code);
		expect(response.headers.get("location")).toBe(pasted);
	});

	it("without a bot token, sends visitors to the address as pasted", async () => {
		const pasted = discordUrl(hoursFromNow(-30));
		const row: ShortLinkRow = {
			code: "notoken",
			target: pasted,
			canonical: pasted,
			refreshed_url: null,
			created_by: "editor@thegrid.test",
			created_at: new Date().toISOString(),
		};

		const { DISCORD_BOT_TOKEN: _token, ...withoutToken } = env;
		const destination = await destinationFor(row, withoutToken, new Date());

		expect(destination).toEqual({ url: pasted });
	});
});
