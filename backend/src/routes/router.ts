import type { RequestContext } from "../types.js";

export type RouteHandler = (
	request: Request,
	context: RequestContext,
) => Response | Promise<Response>;

export interface Route {
	readonly method: "GET" | "POST" | "PUT" | "DELETE";
	/** Pattern with `:name` placeholders, e.g. `/v1/profiles/:id`. */
	readonly pattern: string;
	readonly handler: RouteHandler;
}

export type RouteMatch =
	| { readonly kind: "matched"; readonly route: Route; readonly params: Record<string, string> }
	| { readonly kind: "method-not-allowed"; readonly pattern: string; readonly allow: string[] }
	| { readonly kind: "not-found" };

const segmentsOf = (pathname: string): string[] =>
	pathname.split("/").filter((segment) => segment.length > 0);

/**
 * Decodes a path segment, falling back to the raw text.
 *
 * A malformed escape is not treated as an error here — the segment is passed
 * through so the route's own validation produces the standard error envelope
 * rather than a generic 500.
 */
function decodeSegment(segment: string): string {
	try {
		return decodeURIComponent(segment);
	} catch {
		return segment;
	}
}

function matchPattern(pattern: string, pathSegments: string[]): Record<string, string> | undefined {
	const patternSegments = segmentsOf(pattern);
	if (patternSegments.length !== pathSegments.length) {
		return undefined;
	}

	const params: Record<string, string> = {};
	for (let index = 0; index < patternSegments.length; index += 1) {
		const expected = patternSegments[index] as string;
		const actual = pathSegments[index] as string;

		if (expected.startsWith(":")) {
			params[expected.slice(1)] = decodeSegment(actual);
			continue;
		}

		if (expected !== actual) {
			return undefined;
		}
	}

	return params;
}

/**
 * Resolves a request to a route.
 *
 * Distinguishes "no such path" from "wrong method on a real path" so the
 * latter can answer 405 with an accurate `Allow` header.
 */
export function matchRoute(routes: readonly Route[], method: string, pathname: string): RouteMatch {
	const pathSegments = segmentsOf(pathname);
	const pathMatches: Route[] = [];

	for (const route of routes) {
		const params = matchPattern(route.pattern, pathSegments);
		if (params === undefined) {
			continue;
		}

		pathMatches.push(route);
		if (route.method === method) {
			return { kind: "matched", route, params };
		}
	}

	const first = pathMatches[0];
	if (first === undefined) {
		return { kind: "not-found" };
	}

	const allow: string[] = [...new Set(pathMatches.map((route) => route.method))];
	if (allow.includes("GET")) {
		allow.push("HEAD");
	}
	allow.push("OPTIONS");

	return { kind: "method-not-allowed", pattern: first.pattern, allow };
}
