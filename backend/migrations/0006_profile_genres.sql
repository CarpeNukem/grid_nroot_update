-- 0006_profile_genres: what a DJ plays.
--
-- Free text rather than a list of tags: venue sets are described the way the
-- resident describes them ("dark synth, EBM, occasional breakcore"), and a tag
-- vocabulary would have to be curated and would still not fit.
--
-- Applies to every profile so nothing has to special-case the column, but only
-- DJs are offered it in the admin tool and only DJs render it.
ALTER TABLE profiles ADD COLUMN genres TEXT NOT NULL DEFAULT '';
