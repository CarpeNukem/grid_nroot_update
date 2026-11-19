-- 0008_short_links: grid.nroot.io/s/<code>, a link shortener for staff.
--
-- One row per short link. Only an administrator can make one (the admin API,
-- behind Access); anyone can follow one.
--
-- `target` is the address exactly as it was pasted. `canonical` is the same
-- address with anything that changes between copies of it removed — for a
-- Discord attachment, the expiring signature — so pasting the same file twice
-- finds the link already made instead of minting a second one.
--
-- Discord signs attachment addresses for about a day. `refreshed_url` holds
-- the latest re-signed copy the Worker fetched from Discord, so following a
-- link costs a call to Discord at most once a day rather than once a click.
CREATE TABLE short_links (
	code TEXT PRIMARY KEY NOT NULL,
	target TEXT NOT NULL,
	canonical TEXT NOT NULL,
	refreshed_url TEXT,
	created_by TEXT NOT NULL,
	created_at TEXT NOT NULL
);

-- The duplicate check when a link is made.
CREATE INDEX short_links_canonical ON short_links (canonical);
