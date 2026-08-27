import { etagFor } from "./http.js";

/**
 * An edge cache in front of the expensive public reads.
 *
 * The deck polls `/v1/catalog` once a minute for as long as it is loaded, and
 * every one of those requests ran four D1 queries and hashed the result — a 304
 * cost exactly as much database work as a 200, because the tag is computed from
 * the body and the body has to be built to compute it. A hundred decks left
 * running is 144,000 requests a day, and the rows read grow with the catalogue,
 * so the bill multiplies as the venue publishes more rather than staying flat.
 *
 * Every caller wants identical bytes — these reads are unauthenticated and vary
 * by nothing — so one computation can serve all of them. `Cache-Control` alone
 * does not achieve that: a Worker's response is not put in the CDN cache on its
 * own, so the cache is written and read here explicitly.
 *
 * What this does not do is make responses stale beyond the window below. The
 * entry expires on its own, which also covers the case a revision counter would
 * get wrong: a scheduled broadcast becomes visible with no write to notice.
 */

/**
 * How long a computed catalogue may serve from the edge.
 *
 * Thirty seconds collapses a hundred decks in one colo from a hundred
 * computations a minute to two, which is the whole win; going to sixty would
 * save one more and double the delay on an edit. Cache entries are per colo and
 * `caches.default.delete` is local to one, so there is no purge-on-write that
 * would work here — the window is the only freshness guarantee.
 */
export const PUBLIC_READ_EDGE_TTL_SECONDS = 30;

/** A body that is ready to serve, however it was obtained. */
export interface TaggedBody {
	readonly serialized: string;
	readonly etag: string;
	/** False when this request had to build it. Reported in the request log. */
	readonly cached: boolean;
}

/**
 * The cache key.
 *
 * Path and origin only. Dropping the query string keeps a caller from filling
 * the cache with variants of a route that ignores it, and dropping the headers
 * is what makes the entry shared: `If-None-Match` differs between callers and
 * must never be part of the key, or every deck would cache its own 304.
 */
const cacheKeyFor = (request: Request): Request => {
	const url = new URL(request.url);

	return new Request(`${url.origin}${url.pathname}`, { method: "GET" });
};

/**
 * Serves a public read from the edge cache, building it only on a miss.
 *
 * The cached entry holds the JSON and its ETag and nothing else: the response
 * the caller receives is always constructed fresh, so it carries this request's
 * own id and the standard headers rather than a copy of someone else's.
 */
export async function cachedRead(
	request: Request,
	build: () => Promise<unknown>,
): Promise<TaggedBody> {
	const cache = typeof caches === "undefined" ? undefined : caches.default;
	const key = cacheKeyFor(request);

	if (cache !== undefined) {
		const hit = await cache.match(key);
		const cachedTag = hit?.headers.get("etag");
		if (hit !== undefined && cachedTag !== null && cachedTag !== undefined) {
			return { serialized: await hit.text(), etag: cachedTag, cached: true };
		}
	}

	const serialized = JSON.stringify(await build());
	const etag = await etagFor(serialized);

	if (cache !== undefined) {
		// The stored copy carries the edge lifetime, which is shorter than what
		// the caller is told; a client may hold its own copy for longer because
		// it revalidates with the tag anyway.
		const entry = new Response(serialized, {
			headers: new Headers({
				"content-type": "application/json; charset=utf-8",
				"cache-control": `public, max-age=${PUBLIC_READ_EDGE_TTL_SECONDS}`,
				etag,
			}),
		});

		// Awaited rather than deferred with waitUntil. The write only happens on a
		// miss — twice a minute per colo, by construction — so the microseconds
		// are irrelevant, and deferring it makes the write race anything that
		// looks at the cache immediately afterwards. A test suite does exactly
		// that, and found this.
		await cache.put(key, entry);
	}

	return { serialized, etag, cached: false };
}
