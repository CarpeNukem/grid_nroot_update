/**
 * Discord attachment addresses, and keeping them working.
 *
 * Discord signs every attachment address it hands out:
 *
 *   https://cdn.discordapp.com/attachments/<channel>/<attachment>/<file>?ex=…&is=…&hm=…
 *
 * `ex` is when the signature runs out, as hex Unix seconds, about a day after
 * it was issued. Past that the CDN answers "This content is no longer
 * available", so a short link that only remembered the pasted address would go
 * dead a day after it was made.
 *
 * Discord will re-sign an address on request for a bot. The short link keeps
 * the address as pasted and asks for a fresh signature when the one it has is
 * close to running out — see `destinationFor` in `links.ts`.
 */

/** The hosts Discord serves attachments from. The second is its resizing proxy. */
const CDN_HOSTS: ReadonlySet<string> = new Set(["cdn.discordapp.com", "media.discordapp.net"]);

/** The query parameters that make up the signature. Anything else is part of the request. */
const SIGNATURE_PARAMS: readonly string[] = ["ex", "is", "hm"];

const REFRESH_ENDPOINT = "https://discord.com/api/v10/attachments/refresh-urls";

/** A message attachment on Discord's CDN — the kind of address Discord can re-sign. */
export function isDiscordAttachment(url: URL): boolean {
	return (
		url.protocol === "https:" &&
		CDN_HOSTS.has(url.hostname) &&
		url.pathname.startsWith("/attachments/")
	);
}

/**
 * The address without its signature: the same file however often it is re-signed.
 *
 * Other parameters stay — on the media proxy `width` and `format` change what
 * is served, so two addresses that differ in them are two different links.
 */
export function withoutSignature(url: URL): string {
	const stripped = new URL(url.href);
	for (const name of SIGNATURE_PARAMS) {
		stripped.searchParams.delete(name);
	}
	stripped.hash = "";

	return stripped.href;
}

/**
 * When a signed address stops working, or `undefined` if it carries no
 * readable signature — which, for a Discord attachment, means it already does
 * not work outside Discord.
 */
export function signatureExpiry(address: string): Date | undefined {
	let expiry: string | null;
	try {
		expiry = new URL(address).searchParams.get("ex");
	} catch {
		return undefined;
	}

	if (expiry === null || !/^[0-9a-f]{1,12}$/i.test(expiry)) {
		return undefined;
	}

	return new Date(Number.parseInt(expiry, 16) * 1000);
}

/** Why a refresh came back empty-handed. Logged; the link still redirects. */
export type RefreshFailure =
	| { readonly reason: "rejected"; readonly status: number }
	| { readonly reason: "unreadable" }
	| { readonly reason: "unreachable" };

export type RefreshResult =
	| { readonly ok: true; readonly url: string }
	| ({ readonly ok: false } & RefreshFailure);

/**
 * Asks Discord to re-sign one attachment address.
 *
 * It takes a bot token (`DISCORD_BOT_TOKEN`). The endpoint is one Discord's
 * own clients use and is not in its published reference, so a refusal is
 * logged and the link falls back to the last address it had rather than
 * failing outright. Discord answers with the address re-signed, and that answer is
 * only accepted if it is still an attachment on Discord's CDN — the redirect
 * goes wherever this returns, so it must not be able to point anywhere else.
 *
 * Parameters on the original that are not part of the signature (the media
 * proxy's `width`, `format` and so on) are carried across if Discord's answer
 * drops them.
 */
export async function refreshAttachmentUrl(
	address: string,
	token: string,
	userAgentUrl: string,
): Promise<RefreshResult> {
	let response: Response;
	try {
		response = await fetch(REFRESH_ENDPOINT, {
			method: "POST",
			headers: {
				authorization: `Bot ${token}`,
				"content-type": "application/json",
				// The form Discord asks bots to identify themselves with.
				"user-agent": `DiscordBot (${userAgentUrl}, 1)`,
			},
			body: JSON.stringify({ attachment_urls: [address] }),
		});
	} catch {
		return { ok: false, reason: "unreachable" };
	}

	if (!response.ok) {
		return { ok: false, reason: "rejected", status: response.status };
	}

	let refreshed: URL;
	try {
		const body = (await response.json()) as {
			refreshed_urls?: { refreshed?: unknown }[];
		};
		const value = body.refreshed_urls?.[0]?.refreshed;
		if (typeof value !== "string") {
			return { ok: false, reason: "unreadable" };
		}
		refreshed = new URL(value);
	} catch {
		return { ok: false, reason: "unreadable" };
	}

	if (!isDiscordAttachment(refreshed) || signatureExpiry(refreshed.href) === undefined) {
		return { ok: false, reason: "unreadable" };
	}

	const original = new URL(address);
	for (const [name, value] of original.searchParams) {
		if (!SIGNATURE_PARAMS.includes(name) && !refreshed.searchParams.has(name)) {
			refreshed.searchParams.set(name, value);
		}
	}

	return { ok: true, url: refreshed.href };
}
