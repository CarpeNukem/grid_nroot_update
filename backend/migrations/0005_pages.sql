-- 0005_pages: editable prose blocks.
--
-- Some deck screens are writing, not records: the Wi-Fi screen is two syncshell
-- ids and a short set of house rules. Modelling that as a structured collection
-- would fight it — the text changes shape more often than any schema would.
--
-- `body` is markdown. The deck renders a deliberate subset (headings, bold,
-- italic, lists, links, code, rules); anything else degrades to plain text
-- rather than showing raw syntax.

CREATE TABLE IF NOT EXISTS pages (
    id         TEXT PRIMARY KEY,
    title      TEXT    NOT NULL,
    body       TEXT    NOT NULL DEFAULT '',
    published  INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT    NOT NULL,
    updated_at TEXT    NOT NULL,
    updated_by TEXT    NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS pages_published ON pages (published, sort_order);
