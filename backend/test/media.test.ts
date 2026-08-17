import { env, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { inspectMedia } from "../src/security/media.js";
import { asAdmin, resetTables } from "./helpers.js";

/**
 * Image upload, storage, and serving.
 *
 * The images here are built byte by byte rather than mocked, so the signature
 * and header parsing is exercised against real format structures.
 */

/** Minimal but structurally valid PNG of the given size. */
function makePng(width: number, height: number): Uint8Array {
	const bytes = new Uint8Array(64);
	bytes.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], 0);
	const view = new DataView(bytes.buffer);
	view.setUint32(8, 13); // IHDR length
	bytes.set([0x49, 0x48, 0x44, 0x52], 12); // "IHDR"
	view.setUint32(16, width);
	view.setUint32(20, height);
	return bytes;
}

function makeJpeg(width: number, height: number): Uint8Array {
	const bytes = new Uint8Array(32);
	bytes.set([0xff, 0xd8, 0xff], 0);
	bytes[3] = 0xc0; // SOF0
	const view = new DataView(bytes.buffer);
	view.setUint16(4, 17); // segment length
	bytes[6] = 8; // precision
	view.setUint16(7, height);
	view.setUint16(9, width);
	return bytes;
}

function makeWebp(width: number, height: number): Uint8Array {
	const bytes = new Uint8Array(40);
	bytes.set([0x52, 0x49, 0x46, 0x46], 0); // "RIFF"
	bytes.set([0x57, 0x45, 0x42, 0x50], 8); // "WEBP"
	bytes.set([0x56, 0x50, 0x38, 0x58], 12); // "VP8X"
	const encoded = (value: number, offset: number): void => {
		const stored = value - 1;
		bytes[offset] = stored & 0xff;
		bytes[offset + 1] = (stored >> 8) & 0xff;
		bytes[offset + 2] = (stored >> 16) & 0xff;
	};
	encoded(width, 24);
	encoded(height, 27);
	return bytes;
}

function makeGif(width: number, height: number): Uint8Array {
	const bytes = new Uint8Array(16);
	bytes.set([0x47, 0x49, 0x46, 0x38, 0x39, 0x61], 0); // "GIF89a"
	const view = new DataView(bytes.buffer);
	view.setUint16(6, width, true);
	view.setUint16(8, height, true);
	return bytes;
}

/**
 * Builds a structurally valid MP4 box tree: ftyp + moov > trak > (tkhd, mdia > hdlr).
 * `handlers` decides which tracks exist, which is how the audio check is exercised.
 */
function makeMp4(
	width: number,
	height: number,
	handlers: readonly string[] = ["vide"],
): Uint8Array {
	const ascii = (text: string): number[] => [...text].map((character) => character.charCodeAt(0));

	const box = (type: string, payload: number[]): number[] => {
		const size = 8 + payload.length;
		return [
			(size >>> 24) & 0xff,
			(size >>> 16) & 0xff,
			(size >>> 8) & 0xff,
			size & 0xff,
			...ascii(type),
			...payload,
		];
	};

	const tkhdPayload = (): number[] => {
		const payload = new Array<number>(84).fill(0);
		// Version 0, so the fixed-point 16.16 dimensions sit at offset 76 and 80
		// of the payload (80 and 84 from the box start).
		const writeFixed = (value: number, offset: number): void => {
			payload[offset] = (value >>> 8) & 0xff;
			payload[offset + 1] = value & 0xff;
		};
		writeFixed(width, 76);
		writeFixed(height, 80);
		return payload;
	};

	const hdlrPayload = (handler: string): number[] => [
		0,
		0,
		0,
		0, // version + flags
		0,
		0,
		0,
		0, // pre_defined
		...ascii(handler),
	];

	const traks = handlers.flatMap((handler) =>
		box("trak", [...box("tkhd", tkhdPayload()), ...box("mdia", box("hdlr", hdlrPayload(handler)))]),
	);

	return new Uint8Array([...box("ftyp", ascii("isom")), ...box("moov", traks)]);
}

const upload = (path: string, body: Uint8Array, headers: Record<string, string> = {}) =>
	SELF.fetch(`https://example.com${path}`, {
		method: "POST",
		headers: asAdmin(headers),
		body,
	});

describe("inspectMedia", () => {
	it("reads PNG dimensions", () => {
		expect(inspectMedia(makePng(800, 600))).toMatchObject({
			format: "png",
			extension: "png",
			contentType: "image/png",
			width: 800,
			height: 600,
		});
	});

	it("reads JPEG dimensions from the frame header", () => {
		expect(inspectMedia(makeJpeg(1024, 768))).toMatchObject({
			format: "jpeg",
			extension: "jpg",
			width: 1024,
			height: 768,
		});
	});

	it("reads WebP dimensions", () => {
		expect(inspectMedia(makeWebp(640, 480))).toMatchObject({
			format: "webp",
			extension: "webp",
			width: 640,
			height: 480,
		});
	});

	it("rejects a file that is not an image", () => {
		expect(() => inspectMedia(new TextEncoder().encode("<svg onload=alert(1)>"))).toThrow();
	});

	it("rejects an empty file", () => {
		expect(() => inspectMedia(new Uint8Array(0))).toThrow();
	});

	it("rejects a PNG signature with an unreadable header", () => {
		// Correct magic bytes, truncated before IHDR: extension spoofing by
		// prefixing a valid signature must not get through.
		const truncated = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

		expect(() => inspectMedia(truncated)).toThrow();
	});

	it("rejects images outside the dimension bounds", () => {
		expect(() => inspectMedia(makePng(8, 8))).toThrow();
		expect(() => inspectMedia(makePng(9000, 9000))).toThrow();
	});

	it("reads GIF dimensions and marks it animated", () => {
		expect(inspectMedia(makeGif(500, 400))).toMatchObject({
			format: "gif",
			extension: "gif",
			contentType: "image/gif",
			width: 500,
			height: 400,
			animated: true,
		});
	});

	it("reads MP4 track dimensions and marks it animated", () => {
		expect(inspectMedia(makeMp4(1280, 720))).toMatchObject({
			format: "mp4",
			extension: "mp4",
			contentType: "video/mp4",
			width: 1280,
			height: 720,
			animated: true,
		});
	});

	it("rejects an MP4 carrying an audio track", () => {
		// The whole point of "soundless": a muted player is not a guarantee, so
		// a file with a `soun` handler never reaches storage.
		expect(() => inspectMedia(makeMp4(1280, 720, ["vide", "soun"]))).toThrow(/no audio track/i);
	});

	it("accepts an MP4 with several video tracks and no audio", () => {
		expect(inspectMedia(makeMp4(640, 360, ["vide", "vide"]))).toMatchObject({ animated: true });
	});

	it("rejects an MP4 with no readable track", () => {
		expect(() => inspectMedia(makeMp4(0, 0, []))).toThrow();
	});

	it("marks still images as not animated", () => {
		expect(inspectMedia(makePng(64, 64)).animated).toBe(false);
		expect(inspectMedia(makeJpeg(64, 64)).animated).toBe(false);
	});
});

describe("media upload", () => {
	beforeEach(resetTables);

	it("stores an image under a content-addressed key", async () => {
		const response = await upload("/v1/admin/media/news/neon-night", makePng(800, 600));

		expect(response.status).toBe(201);
		const body = (await response.json()) as { key: string; url: string; width: number };

		expect(body.key).toMatch(/^news\/neon-night\/[a-f0-9]{64}\.png$/);
		expect(body.url).toContain(body.key);
		expect(body.width).toBe(800);
		expect(await env.MEDIA.head(body.key)).not.toBeNull();
	});

	it("gives identical bytes the same key", async () => {
		const first = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};
		const second = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};

		expect(second.key).toBe(first.key);
	});

	it("gives different bytes a different key", async () => {
		const first = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};
		const second = (await (await upload("/v1/admin/media/news/a", makePng(65, 65))).json()) as {
			key: string;
		};

		expect(second.key).not.toBe(first.key);
	});

	it("requires authentication", async () => {
		const response = await SELF.fetch("https://example.com/v1/admin/media/news/a", {
			method: "POST",
			body: makePng(64, 64),
		});

		expect(response.status).toBe(401);
	});

	it("ignores a lying content type", async () => {
		// Claims to be a PNG, is actually text. The signature decides.
		const response = await upload(
			"/v1/admin/media/news/a",
			new TextEncoder().encode("not really a png"),
			{ "content-type": "image/png" },
		);

		expect(response.status).toBe(400);
	});

	it("rejects an unknown media group", async () => {
		const response = await upload("/v1/admin/media/secrets/a", makePng(64, 64));

		expect(response.status).toBe(400);
	});

	it("rejects a traversal attempt in the record id", async () => {
		const response = await upload("/v1/admin/media/news/..%2F..%2Fetc", makePng(64, 64));

		expect(response.status).toBe(400);
	});

	it("stores a GIF flyer under a .gif key", async () => {
		const body = (await (
			await upload("/v1/admin/media/news/neon-night", makeGif(500, 400))
		).json()) as { key: string; animated: boolean };

		expect(body.key).toMatch(/^news\/neon-night\/[a-f0-9]{64}\.gif$/);
		expect(body.animated).toBe(true);
	});

	it("stores a soundless MP4 flyer under an .mp4 key", async () => {
		const body = (await (
			await upload("/v1/admin/media/news/neon-night", makeMp4(1280, 720))
		).json()) as { key: string; animated: boolean; contentType: string };

		expect(body.key).toMatch(/^news\/neon-night\/[a-f0-9]{64}\.mp4$/);
		expect(body.contentType).toBe("video/mp4");
		expect(body.animated).toBe(true);
	});

	it("refuses an MP4 with sound", async () => {
		const response = await upload(
			"/v1/admin/media/news/neon-night",
			makeMp4(1280, 720, ["vide", "soun"]),
		);

		expect(response.status).toBe(400);
		expect(await response.json()).toMatchObject({
			error: { message: expect.stringMatching(/no audio track/i) },
		});
		expect((await env.MEDIA.list()).objects).toHaveLength(0);
	});

	it("serves an MP4 with its own content type", async () => {
		const { key } = (await (await upload("/v1/admin/media/news/a", makeMp4(640, 360))).json()) as {
			key: string;
		};

		const response = await SELF.fetch(`https://example.com/media/${key}`);

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("video/mp4");
	});

	it("rejects an oversized declared length", async () => {
		const response = await upload("/v1/admin/media/news/a", makePng(64, 64), {
			"content-length": String(50 * 1024 * 1024),
		});

		expect([400, 413]).toContain(response.status);
	});
});

describe("media serving", () => {
	beforeEach(resetTables);

	it("serves a stored image with immutable caching", async () => {
		const { key } = (await (
			await upload("/v1/admin/media/news/neon-night", makePng(800, 600))
		).json()) as { key: string };

		const response = await SELF.fetch(`https://example.com/media/${key}`);

		expect(response.status).toBe(200);
		expect(response.headers.get("content-type")).toBe("image/png");
		expect(response.headers.get("cache-control")).toContain("immutable");
		expect(response.headers.get("x-content-sha256")).toBe(key.split("/")[2]?.split(".")[0]);
		expect((await response.arrayBuffer()).byteLength).toBe(64);
	});

	it("answers 304 for a matching ETag", async () => {
		const { key } = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};

		const first = await SELF.fetch(`https://example.com/media/${key}`);
		const etag = first.headers.get("etag") as string;
		const second = await SELF.fetch(`https://example.com/media/${key}`, {
			headers: { "if-none-match": etag },
		});

		expect(second.status).toBe(304);
	});

	it("needs no authentication", async () => {
		const { key } = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};

		expect((await SELF.fetch(`https://example.com/media/${key}`)).status).toBe(200);
	});

	it("returns 404 for an object that does not exist", async () => {
		const response = await SELF.fetch(`https://example.com/media/news/a/${"0".repeat(64)}.png`);

		expect(response.status).toBe(404);
	});

	it("refuses a malformed key rather than passing it to storage", async () => {
		for (const path of ["/media/news/a/not-a-hash.png", "/media/secrets/a/x.png"]) {
			expect((await SELF.fetch(`https://example.com${path}`)).status).toBe(404);
		}
	});
});

describe("media deletion", () => {
	beforeEach(resetTables);

	it("removes a stored image", async () => {
		const { key } = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};

		const response = await SELF.fetch(`https://example.com/v1/admin/media/${key}`, {
			method: "DELETE",
			headers: asAdmin(),
		});

		expect(response.status).toBe(200);
		expect(await env.MEDIA.head(key)).toBeNull();
		expect((await SELF.fetch(`https://example.com/media/${key}`)).status).toBe(404);
	});

	it("requires authentication", async () => {
		const { key } = (await (await upload("/v1/admin/media/news/a", makePng(64, 64))).json()) as {
			key: string;
		};

		const response = await SELF.fetch(`https://example.com/v1/admin/media/${key}`, {
			method: "DELETE",
		});

		expect(response.status).toBe(401);
		expect(await env.MEDIA.head(key)).not.toBeNull();
	});

	it("returns 404 for an object that does not exist", async () => {
		const response = await SELF.fetch(
			`https://example.com/v1/admin/media/news/a/${"0".repeat(64)}.png`,
			{ method: "DELETE", headers: asAdmin() },
		);

		expect(response.status).toBe(404);
	});
});
