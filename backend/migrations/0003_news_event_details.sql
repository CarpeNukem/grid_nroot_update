-- 0003_news_event_details: event date, link, and flyer for announcements.
--
-- `event_at` is when the thing happens; `published_at` is when the post becomes
-- visible. They are genuinely different — a flyer for Saturday goes up on
-- Tuesday — and conflating them would either hide the post until the night of
-- the event or sort the feed by the wrong key.
--
-- Stored as an ISO 8601 UTC instant, not as the Discord `<t:...>` string. The
-- Discord form is a rendering of an instant, the same way "15 000" is a
-- rendering of 15000 gil, so the API derives it at read time and can also
-- accept it as input. Empty string means the announcement has no event date.
ALTER TABLE news_posts ADD COLUMN event_at TEXT NOT NULL DEFAULT '';

-- Optional outbound link, e.g. a Discord event or booking page. Validation
-- restricts this to https so a stored value can never be a javascript:, data:,
-- or file: URI that a client might open.
ALTER TABLE news_posts ADD COLUMN link TEXT NOT NULL DEFAULT '';

-- Anchor text for the link. Without it a client has to render a raw URL.
ALTER TABLE news_posts ADD COLUMN link_label TEXT NOT NULL DEFAULT '';

-- Flyer art reuses the existing image_key / bundled_image columns: image_key is
-- the R2 object once Phase 4 exists, bundled_image the fallback shipped in the
-- plugin. The news API exposes them as flyerUrl / flyerImage.

-- Upcoming events, soonest first, for a client that wants a schedule view
-- rather than the reverse-chronological feed.
CREATE INDEX IF NOT EXISTS news_posts_event
    ON news_posts (published, event_at);
