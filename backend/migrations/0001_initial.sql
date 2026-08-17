-- 0001_initial: staff profiles.
--
-- Mirrors the shape of the bundled staff_profiles.json so the importer is a
-- direct mapping rather than a second, divergent format. Fields the plugin
-- renders as optional live in `optional_json` as a JSON object, matching the
-- nested `optional` block in the source file.
--
-- `published` defaults to 0: an imported profile is invisible to public routes
-- until someone explicitly publishes it, so a person's portrait and character
-- name are never exposed by an import alone.

CREATE TABLE IF NOT EXISTS profiles (
    id              TEXT PRIMARY KEY,
    category        TEXT    NOT NULL,
    name            TEXT    NOT NULL,
    character_name  TEXT    NOT NULL,
    age             TEXT    NOT NULL DEFAULT '',
    affiliation     TEXT    NOT NULL DEFAULT '',
    occupation      TEXT    NOT NULL DEFAULT '',
    bio             TEXT    NOT NULL DEFAULT '',
    optional_json   TEXT,
    -- R2 object key, never an absolute URL. The public URL is composed at read
    -- time from PUBLIC_MEDIA_BASE_URL so the CDN origin can change freely.
    image_key       TEXT    NOT NULL DEFAULT '',
    request_label   TEXT    NOT NULL DEFAULT '',
    request_message TEXT    NOT NULL DEFAULT '',
    published       INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0, 1)),
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL
);

-- Serves the public list query: published rows of one category in display order.
CREATE INDEX IF NOT EXISTS profiles_category_published
    ON profiles (category, published, sort_order);
