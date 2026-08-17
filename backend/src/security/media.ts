import { ApiError, ErrorCode } from "./errors.js";
import { LIMITS } from "./validate.js";

/**
 * Image validation for uploads.
 *
 * The declared Content-Type is treated as a hint and nothing more. Everything
 * that matters is read from the bytes themselves: the format comes from the
 * file signature, and the dimensions from the format's own header. A file that
 * claims to be a PNG but is not, or whose header cannot be read, is rejected —
 * these objects are served to a game client, so "we could not tell what this
 * is" must never resolve to "upload it anyway".
 */

export const ALLOWED_MEDIA_GROUPS = ["profiles", "menu", "news"] as const;
export type MediaGroup = (typeof ALLOWED_MEDIA_GROUPS)[number];

export const MEDIA_LIMITS = {
	maxBytes: LIMITS.mediaMaxBytes,
	/** Animated flyers get more room; a still image has no excuse to be this big. */
	maxAnimatedBytes: 12 * 1024 * 1024,
	minDimension: 16,
	/** Generous enough for a full-bleed event flyer, short of a decompression bomb. */
	maxDimension: 4096,
} as const;

export type MediaFormat = "png" | "jpeg" | "webp" | "gif" | "mp4";

export interface InspectedMedia {
	readonly format: MediaFormat;
	readonly extension: "png" | "jpg" | "webp" | "gif" | "mp4";
	readonly contentType: string;
	readonly width: number;
	readonly height: number;
	/** True for GIF and MP4 — a client that cannot animate needs to know. */
	readonly animated: boolean;
}

const invalidMedia = (message: string, context?: Record<string, unknown>): ApiError =>
	new ApiError(400, ErrorCode.BAD_REQUEST, message, context);

const startsWith = (bytes: Uint8Array, signature: readonly number[], offset = 0): boolean =>
	signature.every((byte, index) => bytes[offset + index] === byte);

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const JPEG_SIGNATURE = [0xff, 0xd8, 0xff];
const RIFF_SIGNATURE = [0x52, 0x49, 0x46, 0x46];
const WEBP_SIGNATURE = [0x57, 0x45, 0x42, 0x50];
const GIF87A_SIGNATURE = [0x47, 0x49, 0x46, 0x38, 0x37, 0x61];
const GIF89A_SIGNATURE = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];
/** ISO base media files carry "ftyp" at offset 4, whatever the brand. */
const FTYP_SIGNATURE = [0x66, 0x74, 0x79, 0x70];

/** PNG stores width and height as big-endian 32-bit values in the IHDR chunk. */
function readPngDimensions(bytes: Uint8Array): { width: number; height: number } | undefined {
	if (bytes.length < 24) {
		return undefined;
	}

	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	return { width: view.getUint32(16), height: view.getUint32(20) };
}

/**
 * Walks JPEG segments to the start-of-frame marker.
 *
 * Dimensions are not at a fixed offset in a JPEG — the frame header sits after
 * a variable run of metadata segments, so the markers have to be followed.
 */
function readJpegDimensions(bytes: Uint8Array): { width: number; height: number } | undefined {
	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	let offset = 2;

	while (offset + 9 < bytes.length) {
		if (bytes[offset] !== 0xff) {
			return undefined;
		}

		const marker = bytes[offset + 1] as number;
		const length = view.getUint16(offset + 2);

		// SOF0..SOF15, excluding the non-frame markers in that range.
		const isStartOfFrame =
			marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc;

		if (isStartOfFrame) {
			return { height: view.getUint16(offset + 5), width: view.getUint16(offset + 7) };
		}

		if (length < 2) {
			return undefined;
		}

		offset += 2 + length;
	}

	return undefined;
}

/** WebP has three container shapes; each stores its size differently. */
function readWebpDimensions(bytes: Uint8Array): { width: number; height: number } | undefined {
	if (bytes.length < 30) {
		return undefined;
	}

	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const chunk = String.fromCharCode(...bytes.slice(12, 16));

	if (chunk === "VP8X") {
		// Extended: 24-bit little-endian, stored as (dimension - 1).
		const width = 1 + (view.getUint8(24) | (view.getUint8(25) << 8) | (view.getUint8(26) << 16));
		const height = 1 + (view.getUint8(27) | (view.getUint8(28) << 8) | (view.getUint8(29) << 16));
		return { width, height };
	}

	if (chunk === "VP8 ") {
		// Lossy: 14-bit dimensions after the start code.
		return {
			width: view.getUint16(26, true) & 0x3fff,
			height: view.getUint16(28, true) & 0x3fff,
		};
	}

	if (chunk === "VP8L") {
		// Lossless: 14-bit dimensions packed into four bytes after the signature.
		const bits = view.getUint32(21, true);
		return { width: 1 + (bits & 0x3fff), height: 1 + ((bits >> 14) & 0x3fff) };
	}

	return undefined;
}

/** GIF stores its logical screen size as two little-endian 16-bit values. */
function readGifDimensions(bytes: Uint8Array): { width: number; height: number } | undefined {
	if (bytes.length < 10) {
		return undefined;
	}

	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	return { width: view.getUint16(6, true), height: view.getUint16(8, true) };
}

interface Mp4Box {
	readonly type: string;
	readonly start: number;
	readonly end: number;
}

/**
 * Lists the ISO base media boxes within a range.
 *
 * MP4 is a tree of length-prefixed boxes; nothing is at a fixed offset, so both
 * the track dimensions and the audio check require walking it.
 */
function readMp4Boxes(bytes: Uint8Array, from: number, to: number): Mp4Box[] {
	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const boxes: Mp4Box[] = [];
	let offset = from;

	while (offset + 8 <= to) {
		let size = view.getUint32(offset);
		const type = String.fromCharCode(...bytes.slice(offset + 4, offset + 8));
		let headerSize = 8;

		if (size === 1) {
			// 64-bit size. Anything needing the high word is far past our limit.
			if (offset + 16 > to || view.getUint32(offset + 8) !== 0) {
				return boxes;
			}
			size = view.getUint32(offset + 12);
			headerSize = 16;
		} else if (size === 0) {
			size = to - offset;
		}

		if (size < headerSize || offset + size > to) {
			return boxes;
		}

		boxes.push({ type, start: offset + headerSize, end: offset + size });
		offset += size;
	}

	return boxes;
}

const findBox = (boxes: readonly Mp4Box[], type: string): Mp4Box | undefined =>
	boxes.find((box) => box.type === type);

interface Mp4Info {
	readonly width: number;
	readonly height: number;
	readonly hasAudio: boolean;
}

/**
 * Reads track dimensions and detects audio.
 *
 * "Soundless" is enforced rather than trusted: every track's handler is
 * inspected, and a single `soun` track rejects the upload. A muted player is
 * not the same guarantee — this file will be played by clients we do not
 * control, and a venue flyer must never make noise at someone.
 */
function readMp4Info(bytes: Uint8Array): Mp4Info | undefined {
	const top = readMp4Boxes(bytes, 0, bytes.length);
	const moov = findBox(top, "moov");
	if (moov === undefined) {
		return undefined;
	}

	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	let width = 0;
	let height = 0;
	let hasAudio = false;

	for (const trak of readMp4Boxes(bytes, moov.start, moov.end).filter(
		(box) => box.type === "trak",
	)) {
		const trakBoxes = readMp4Boxes(bytes, trak.start, trak.end);

		const tkhd = findBox(trakBoxes, "tkhd");
		if (tkhd !== undefined) {
			// Fixed-point 16.16 at the end of the header. Counting the payload:
			// 4 version/flags + 20 times and ids + 8 reserved + 8 layer/group/
			// volume/reserved + 36 matrix = 76. Version 1 widens creation,
			// modification, and duration by 4 bytes each, moving it to 88.
			const version = view.getUint8(tkhd.start);
			const dimensionOffset = tkhd.start + (version === 1 ? 88 : 76);
			if (dimensionOffset + 8 <= tkhd.end) {
				width = Math.max(width, Math.round(view.getUint32(dimensionOffset) / 65536));
				height = Math.max(height, Math.round(view.getUint32(dimensionOffset + 4) / 65536));
			}
		}

		const mdia = findBox(trakBoxes, "mdia");
		if (mdia === undefined) {
			continue;
		}

		const hdlr = findBox(readMp4Boxes(bytes, mdia.start, mdia.end), "hdlr");
		if (hdlr !== undefined && hdlr.start + 12 <= hdlr.end) {
			const handler = String.fromCharCode(...bytes.slice(hdlr.start + 8, hdlr.start + 12));
			if (handler === "soun") {
				hasAudio = true;
			}
		}
	}

	if (width <= 0 || height <= 0) {
		return undefined;
	}

	return { width, height, hasAudio };
}

/**
 * Identifies and validates uploaded media.
 *
 * Throws with a client-safe message for anything that is not a supported still
 * image or soundless animation within the configured bounds.
 */
export function inspectMedia(bytes: Uint8Array): InspectedMedia {
	if (bytes.length === 0) {
		throw invalidMedia("The uploaded file is empty.");
	}

	let format: MediaFormat;
	let dimensions: { width: number; height: number } | undefined;

	if (startsWith(bytes, PNG_SIGNATURE)) {
		format = "png";
		dimensions = readPngDimensions(bytes);
	} else if (startsWith(bytes, JPEG_SIGNATURE)) {
		format = "jpeg";
		dimensions = readJpegDimensions(bytes);
	} else if (startsWith(bytes, RIFF_SIGNATURE) && startsWith(bytes, WEBP_SIGNATURE, 8)) {
		format = "webp";
		dimensions = readWebpDimensions(bytes);
	} else if (startsWith(bytes, GIF87A_SIGNATURE) || startsWith(bytes, GIF89A_SIGNATURE)) {
		format = "gif";
		dimensions = readGifDimensions(bytes);
	} else if (startsWith(bytes, FTYP_SIGNATURE, 4)) {
		format = "mp4";
		const info = readMp4Info(bytes);
		if (info?.hasAudio === true) {
			throw invalidMedia("Video flyers must have no audio track.");
		}
		dimensions = info;
	} else {
		throw invalidMedia("Only PNG, JPEG, WebP, GIF, and soundless MP4 files are accepted.");
	}

	const animated = format === "gif" || format === "mp4";
	const sizeLimit = animated ? MEDIA_LIMITS.maxAnimatedBytes : MEDIA_LIMITS.maxBytes;
	if (bytes.length > sizeLimit) {
		throw invalidMedia(
			`That file is larger than the ${Math.round(sizeLimit / (1024 * 1024))} MB limit.`,
			{ bytes: bytes.length },
		);
	}

	if (dimensions === undefined) {
		throw invalidMedia("That file's header could not be read.", { format });
	}

	const { width, height } = dimensions;
	if (width < MEDIA_LIMITS.minDimension || height < MEDIA_LIMITS.minDimension) {
		throw invalidMedia(`Media must be at least ${MEDIA_LIMITS.minDimension}px on each side.`, {
			width,
			height,
		});
	}

	if (width > MEDIA_LIMITS.maxDimension || height > MEDIA_LIMITS.maxDimension) {
		throw invalidMedia(`Media must be no more than ${MEDIA_LIMITS.maxDimension}px on each side.`, {
			width,
			height,
		});
	}

	const extension = format === "jpeg" ? "jpg" : format;
	return {
		format,
		extension,
		contentType: format === "mp4" ? "video/mp4" : `image/${format}`,
		width,
		height,
		animated,
	};
}

/** Lowercase hex SHA-256 of the bytes, used as the object's immutable name. */
export async function sha256Hex(bytes: Uint8Array): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", bytes as BufferSource);
	return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/**
 * Builds the object key.
 *
 * The key is `<group>/<slug>/<sha256>.<ext>` — entirely derived from validated
 * inputs and the content hash, never from a client-supplied filename. That is
 * what makes traversal impossible and makes every object immutable: different
 * bytes always mean a different key, so a replaced image can never be served
 * stale from a cache.
 */
export function buildMediaKey(
	group: MediaGroup,
	ownerId: string,
	digest: string,
	extension: string,
): string {
	return `${group}/${ownerId}/${digest}.${extension}`;
}
