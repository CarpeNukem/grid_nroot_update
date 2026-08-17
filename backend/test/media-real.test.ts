import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { inspectMedia } from "../src/security/media.js";

/**
 * The inspector against genuinely encoded files rather than hand-built fixtures.
 *
 * A synthetic MP4 contains only the boxes the parser looks for. A real one is
 * full of `mdat`, `free`, `udta`, and edit lists it has to walk past, so this is
 * what proves the box walker works on files people will actually upload.
 *
 * The two fixtures are the same source remuxed with and without its audio
 * track, so they differ in exactly the thing being tested.
 */

const fixture = (name: string): Uint8Array => {
	const base64 = env.TEST_MEDIA_FIXTURES[name];
	if (base64 === undefined) {
		throw new Error(`Missing media fixture: ${name}`);
	}

	return Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
};

/**
 * The fixtures are binaries generated locally and deliberately not committed —
 * see test/fixtures/README.md. Without them these tests skip rather than fail,
 * so a fresh clone still runs green; the synthetic MP4 tests in media.test.ts
 * cover the parser either way.
 */
const hasFixtures =
	env.TEST_MEDIA_FIXTURES.silent === undefined
		? Object.keys(env.TEST_MEDIA_FIXTURES).length >= 2
		: true;

describe.skipIf(!hasFixtures)("real encoded media", () => {
	it("reads track dimensions from a real MP4", () => {
		expect(inspectMedia(fixture("silent.mp4"))).toMatchObject({
			format: "mp4",
			extension: "mp4",
			contentType: "video/mp4",
			width: 220,
			height: 360,
			animated: true,
		});
	});

	it("rejects a real MP4 that carries sound", () => {
		expect(() => inspectMedia(fixture("withaudio.mp4"))).toThrow(/no audio track/i);
	});

	it("tells the two apart by their audio track alone", () => {
		// Same video stream, same dimensions; only the `soun` handler differs.
		expect(() => inspectMedia(fixture("silent.mp4"))).not.toThrow();
		expect(() => inspectMedia(fixture("withaudio.mp4"))).toThrow();
	});
});
