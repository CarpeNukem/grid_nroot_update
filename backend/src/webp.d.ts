/** Wrangler's Data module rule turns imported .webp files into ArrayBuffers. */
declare module "*.webp" {
	const contents: ArrayBuffer;
	export default contents;
}
