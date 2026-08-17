/** Wrangler's Text module rule turns imported .html files into strings. */
declare module "*.html" {
	const contents: string;
	export default contents;
}
