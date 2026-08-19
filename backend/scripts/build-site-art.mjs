import { mkdir, readdir, rm, stat, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

/**
 * Builds the web copies of the deck's artwork.
 *
 * The plugin's own art is sized for a game overlay and stored as PNG: the
 * rooftop header alone is 2.4 MB, which is fine on disk next to the plugin and
 * absurd to put in a Worker bundle or send to a phone. This produces WebP
 * derivatives small enough to ship inside the Worker, which is what lets the
 * site serve them from its own origin under `img-src 'self'`.
 *
 * The outputs are committed, so a normal deploy needs nothing from here. Re-run
 * it when the source art changes:
 *
 *   node scripts/build-site-art.mjs
 */

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const sourceDir = join(repoRoot, "img");
const outputDir = join(here, "..", "site", "img");

/**
 * The header photograph, resized to something a browser should actually be
 * asked to download. It is drawn behind text at low opacity, so it survives a
 * fairly aggressive quality setting without anyone noticing.
 */
const HERO = { source: "rooftop.png", width: 1280, quality: 70 };

/** The venue mark, kept lossless: it is flat colour with hard edges. */
const LOGO = { source: "grid.png", width: 320 };

/**
 * Tile art, one per view the site offers. These mirror the deck's home grid so
 * the two read as the same application.
 */
const TILES = ["menu", "wifi", "address", "broadcast", "services", "settings"];

const kb = (bytes) => `${(bytes / 1024).toFixed(1)} KB`;

async function emit(name, buffer) {
	const target = join(outputDir, name);
	await writeFile(target, buffer);
	console.log(`  ${name.padEnd(18)} ${kb(buffer.length)}`);
	return buffer.length;
}

async function main() {
	await rm(outputDir, { recursive: true, force: true });
	await mkdir(outputDir, { recursive: true });

	let total = 0;

	const heroSource = join(sourceDir, HERO.source);
	const before = (await stat(heroSource)).size;
	total += await emit(
		"rooftop.webp",
		await sharp(heroSource)
			.resize({ width: HERO.width, withoutEnlargement: true })
			.webp({ quality: HERO.quality })
			.toBuffer(),
	);
	console.log(`  (rooftop source was ${kb(before)})`);

	total += await emit(
		"grid.webp",
		await sharp(join(sourceDir, LOGO.source))
			.resize({ width: LOGO.width, withoutEnlargement: true })
			.webp({ lossless: true })
			.toBuffer(),
	);

	for (const tile of TILES) {
		total += await emit(
			`${tile}.webp`,
			await sharp(join(sourceDir, "buttons", `${tile}.png`))
				.webp({ quality: 82 })
				.toBuffer(),
		);
	}

	const written = await readdir(outputDir);
	console.log(`\n${written.length} files, ${kb(total)} total.`);
}

await main();
