-- 0002_menu_and_news: the drinks card and venue announcements.
--
-- Menu columns mirror the DrinkMenuItem record the plugin renders today
-- (name, price, image, ingredients, description, taste) so the importer is a
-- direct mapping rather than a reinterpretation.
--
-- `price_gil` is stored as an integer rather than the display string "10 000".
-- The separator is presentation, and an integer keeps the data sortable and
-- comparable; the API returns both the number and a formatted label.

CREATE TABLE IF NOT EXISTS menu_items (
    id            TEXT PRIMARY KEY,
    name          TEXT    NOT NULL,
    price_gil     INTEGER NOT NULL DEFAULT 0 CHECK (price_gil >= 0),
    ingredients   TEXT    NOT NULL DEFAULT '',
    description   TEXT    NOT NULL DEFAULT '',
    taste         TEXT    NOT NULL DEFAULT '',
    -- R2 object key for remote art. Empty until Phase 4 uploads exist.
    image_key     TEXT    NOT NULL DEFAULT '',
    -- Filename of the art bundled in the plugin, e.g. 'frostbite.png'. Lets a
    -- remote record fall back to bundled art instead of rendering blank.
    bundled_image TEXT    NOT NULL DEFAULT '',
    published     INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0, 1)),
    sort_order    INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL,
    -- Verified email of the last editor. Audit trail only; never public.
    updated_by    TEXT    NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS menu_items_published
    ON menu_items (published, sort_order);

-- Venue announcements. `published_at` is the date shown to readers and drives
-- ordering; `created_at` is the row's own history and is never displayed.
-- Pinned posts sort above the rest regardless of date.
CREATE TABLE IF NOT EXISTS news_posts (
    id            TEXT PRIMARY KEY,
    title         TEXT    NOT NULL,
    summary       TEXT    NOT NULL DEFAULT '',
    body          TEXT    NOT NULL DEFAULT '',
    image_key     TEXT    NOT NULL DEFAULT '',
    bundled_image TEXT    NOT NULL DEFAULT '',
    pinned        INTEGER NOT NULL DEFAULT 0 CHECK (pinned IN (0, 1)),
    published_at  TEXT    NOT NULL,
    published     INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0, 1)),
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL,
    updated_by    TEXT    NOT NULL DEFAULT ''
);

-- Serves the public feed: published posts, pinned first, newest first.
CREATE INDEX IF NOT EXISTS news_posts_published
    ON news_posts (published, pinned, published_at);

-- Profiles gain the same bundled-art fallback and audit column as the new tables.
ALTER TABLE profiles ADD COLUMN bundled_image TEXT NOT NULL DEFAULT '';
ALTER TABLE profiles ADD COLUMN updated_by TEXT NOT NULL DEFAULT '';
