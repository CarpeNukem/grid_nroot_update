import { SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { parsePageInput } from "../src/data/validation.js";
import { parseVenueAddress, VENUE_ADDRESS_PAGE } from "../src/data/venue.js";
import { asAdmin, jsonHeaders, resetTables } from "./helpers.js";

/**
 * The venue's address, published as the `address` page so a move needs no
 * deck release. The deck and the site trust it only when they can read it in
 * full, so the relay refuses anything else when the page is saved.
 */

describe("the venue's address", () => {
	it("reads a world, a district, a ward and a plot, with or without the data centre", () => {
		expect(parseVenueAddress("Light Raiden Mist W29 P2")).toBe("Light Raiden Mist W29 P2");
		expect(parseVenueAddress("Raiden Mist Ward 29 Plot 2")).toBe("Raiden Mist Ward 29 Plot 2");
		expect(parseVenueAddress("  Light   Raiden\tMist W29\nP2  ")).toBe("Light Raiden Mist W29 P2");
		expect(parseVenueAddress("Light Raiden The Lavender Beds W1 P60")).toBe(
			"Light Raiden The Lavender Beds W1 P60",
		);
		expect(parseVenueAddress("Aether Gilgamesh The Goblet Ward30 Plot1")).toBe(
			"Aether Gilgamesh The Goblet Ward30 Plot1",
		);
		expect(parseVenueAddress("Raiden Shirogane W5 P31")).toBe("Raiden Shirogane W5 P31");
	});

	it("refuses an address the deck could not read in full", () => {
		for (const address of [
			"",
			"Raiden Mist",
			"Mist W29 P2",
			"Light Mist W29 P2",
			"Raiden Ul'dah W29 P2",
			"Light Raiden Mist W0 P2",
			"Light Raiden Mist W31 P2",
			"Light Raiden Mist W29 P61",
			"Light Raiden Mist P2 W29",
			"Nowhere Raiden Mist W29 P2",
			"Light Raiden Mist W29 P2 **come by**",
			"[Light Raiden Mist W29 P2](https://example.com)",
		]) {
			expect(parseVenueAddress(address), address).toBeNull();
		}
	});

	it("tidies the address page's body, and leaves every other page alone", () => {
		expect(
			parsePageInput({ title: "Address", body: " Light  Raiden Mist W29 P2 " }, VENUE_ADDRESS_PAGE)
				.body,
		).toBe("Light Raiden Mist W29 P2");
		expect(parsePageInput({ title: "Wi-Fi", body: "Not an address at all." }, "wifi").body).toBe(
			"Not an address at all.",
		);
	});

	it("refuses an address page whose body is not an address", () => {
		expect(() =>
			parsePageInput({ title: "Address", body: "Come to Mist!" }, VENUE_ADDRESS_PAGE),
		).toThrow(/venue's address/);
		expect(() => parsePageInput({ id: VENUE_ADDRESS_PAGE, title: "Address" })).toThrow(
			/venue's address/,
		);
	});
});

describe("publishing the venue's address", () => {
	beforeEach(resetTables);

	const send = (method: string, path: string, body?: unknown): Promise<Response> =>
		SELF.fetch(`https://example.com${path}`, {
			method,
			headers: body === undefined ? asAdmin() : jsonHeaders(asAdmin()),
			...(body === undefined ? {} : { body: JSON.stringify(body) }),
		});

	it("refuses to save a page that would leave decks without an address", async () => {
		const response = await send("POST", "/v1/admin/pages", {
			id: VENUE_ADDRESS_PAGE,
			title: "Address",
			body: "Ward 29, the big one",
		});

		expect(response.status).toBe(400);
		expect(await response.json()).toMatchObject({ error: { code: "BAD_REQUEST" } });
	});

	it("publishes the address in the catalogue for the deck and the site", async () => {
		const created = await send("POST", "/v1/admin/pages", {
			id: VENUE_ADDRESS_PAGE,
			title: "Address",
			body: "Light  Raiden Mist W29 P2",
		});
		expect(created.status).toBe(201);
		expect(
			(await send("POST", `/v1/admin/pages/${VENUE_ADDRESS_PAGE}/publish`, { published: true }))
				.status,
		).toBe(200);

		const catalog = (await (await SELF.fetch("https://example.com/v1/catalog")).json()) as {
			pages: { id: string; body: string }[];
		};
		expect(catalog.pages.find((page) => page.id === VENUE_ADDRESS_PAGE)).toMatchObject({
			body: "Light Raiden Mist W29 P2",
		});
	});
});
