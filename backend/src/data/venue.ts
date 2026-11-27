/**
 * The venue's address, as the relay publishes it.
 *
 * The Grid moves house now and then. Its address used to be compiled into the
 * deck, so a move needed a release before any guest's NAVIGATE, venue check or
 * venue activators followed it. Now a published page with the id
 * {@link VENUE_ADDRESS_PAGE} carries it: the deck takes the address from the
 * page's body, as does the public site, and both fall back to the address built
 * into them when there is no such page.
 *
 * The deck only trusts an address it can read in full, so this refuses any
 * other when the page is saved, instead of letting the deck quietly ignore it:
 * an optional data centre, a world, a housing district, a ward and a plot, as
 * in "Light Raiden Mist W29 P2" or "Raiden Mist Ward 29 Plot 2".
 */

/** The page whose body is the venue's address. */
export const VENUE_ADDRESS_PAGE = "address";

/** Data centres: written before a world, never standing for one. */
const DATA_CENTRES = new Set([
	"aether",
	"chaos",
	"crystal",
	"dynamis",
	"elemental",
	"gaia",
	"light",
	"mana",
	"materia",
	"meteor",
	"primal",
]);

/** A housing district, as the deck's parser knows them. */
const DISTRICT = String.raw`Mist|The\s+Mist|(?:The\s+)?Lavender\s+Beds|Lavender|(?:The\s+)?Goblet|Shirogane|Empyreum`;

const ADDRESS = new RegExp(
	String.raw`^(?:([A-Za-z]+)\s+)?([A-Za-z][A-Za-z'-]*)\s+(${DISTRICT})\s+(?:W|Ward\s*)(\d{1,2})\s+(?:P|Plot\s*)(\d{1,2})$`,
	"i",
);

/** Wards in a housing district, and plots in a ward, subdivision included. */
const MAX_WARD = 30;
const MAX_PLOT = 60;

/**
 * The address in the given text, its spacing tidied, or null when it is not
 * one the deck could read in full.
 */
export function parseVenueAddress(text: string): string | null {
	const address = text.trim().replace(/\s+/g, " ");
	const match = ADDRESS.exec(address);
	if (match === null) {
		return null;
	}

	const [, centre, world, , ward, plot] = match;
	if (centre !== undefined && !DATA_CENTRES.has(centre.toLowerCase())) {
		return null;
	}

	if (
		world === undefined ||
		world.toLowerCase() === "the" ||
		DATA_CENTRES.has(world.toLowerCase())
	) {
		return null;
	}

	const wardNumber = Number(ward);
	const plotNumber = Number(plot);
	if (wardNumber < 1 || wardNumber > MAX_WARD || plotNumber < 1 || plotNumber > MAX_PLOT) {
		return null;
	}

	return address;
}
