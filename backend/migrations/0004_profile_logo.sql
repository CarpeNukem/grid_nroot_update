-- 0004_profile_logo: a second image slot for profiles.
--
-- A portrait and a brand mark are different things: a resident DJ has a logo
-- as well as a photo, and squeezing both into one image slot would mean
-- choosing between them. Both are optional, so a profile with only a portrait
-- behaves exactly as before.
--
-- Same split as everywhere else — `logo_key` is the R2 object, `logo_bundled`
-- is art shipped inside the plugin to fall back to.
ALTER TABLE profiles ADD COLUMN logo_key TEXT NOT NULL DEFAULT '';
ALTER TABLE profiles ADD COLUMN logo_bundled TEXT NOT NULL DEFAULT '';
