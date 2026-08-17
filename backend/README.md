# The Grid Cyberdeck — backend

Cloudflare Worker API backing the Cyberdeck plugin's **menu**, **profiles**, and
**news**. TypeScript, D1 for structured data, R2 for images.

**Nothing here is deployed.** This is a local-only project: no Cloudflare
resources exist, no account is configured, and no production ids or secrets are
committed. Creating cloud resources requires explicit approval.

## Status

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Local scaffold, health route, migrations, tests | done |
| 2 | Public read API with ETag/304 | done |
| 3 | D1 schema and repeatable seed importer | done |
| 5 | Admin CRUD behind Cloudflare Access | done (routes; admin UI not built) |
| 4 | R2 image upload, validation, and serving | done (plugin asset cache still needed to render them) |
| 6 | Plugin client, disk cache, bundled fallback | not started |
| 7 | Aggregate attendance (optional) | not started |

## Requirements

Node 20 or newer. Everything else is a local dev dependency — no global installs.

## Commands

```bash
npm install
npm run check      # format check, typecheck, tests
npm run dev        # wrangler dev on http://127.0.0.1:8787
```

First-time local database setup:

```bash
npm run db:migrate:local
npm run seed:local
```

`seed:local` validates the bundled data and applies it. `seed:build` writes
`.wrangler/seed.sql` without applying it, if you want to read the SQL first.

The test suite applies migrations itself, so `npm test` needs no setup.

## API

### Public reads

All are `GET`, return UTF-8 JSON, carry a strong `ETag`, and honour
`If-None-Match` with `304`. They serve **published records only**.

| Route | Returns |
| --- | --- |
| `/v1/health` | Liveness and schema version. Never cached. |
| `/v1/catalog` | Everything in one response — what the plugin should fetch. |
| `/v1/profiles` | Published profiles. `?category=<slug>` filters. |
| `/v1/profiles/:id` | One profile. |
| `/v1/menu` | The drinks card, in curated order. |
| `/v1/menu/:id` | One drink. |
| `/v1/news` | Announcements: pinned first, then newest first. |
| `/v1/news/:id` | One announcement. |

`updatedAt` in a collection response is the newest `updated_at` among its rows,
**not** the time of the request. That is deliberate: the ETag is a hash of the
response bytes, so stamping the current time into the body would change the hash
every request and no client would ever get a `304`.

### Admin

Every route below requires an authenticated administrator and is `no-store`.
`<resource>` is one of `profiles`, `menu`, `news`.

| Route | Effect |
| --- | --- |
| `GET /v1/admin/<resource>` | List **including unpublished**. Storage shape. |
| `GET /v1/admin/<resource>/:id` | One record. |
| `POST /v1/admin/<resource>` | Create. `409` if the id already exists. |
| `PUT /v1/admin/<resource>/:id` | Create or replace. Path id wins over the body. |
| `DELETE /v1/admin/<resource>/:id` | Delete. |
| `POST /v1/admin/<resource>/:id/publish` | `{"published": true｜false}` |

Admin reads return the raw storage row — snake_case, including `published`,
`updated_at`, and `updated_by` — because that is what an editing UI needs.
Public reads return the camelCase public shape and never include those fields.

### Media

| Route | Effect |
| --- | --- |
| `POST /v1/admin/media/<group>/<id>` | Upload. Raw image body, no multipart. Returns the key and URL. |
| `DELETE /v1/admin/media/<group>/<id>/<file>` | Remove one object. |
| `GET /media/<group>/<id>/<file>` | Public. Served from R2, immutable, `ETag`/304. |

`<group>` is `profiles`, `menu`, or `news`.

```bash
curl -X POST -H "x-dev-admin-email: editor@thegrid.test" \
  --data-binary @flyer.png \
  http://127.0.0.1:8787/v1/admin/media/news/neon-rooftop-night
```

The response gives a `key` to store on the record (`flyerKey`, or `imageKey`
elsewhere). The Worker serves R2 itself rather than the bucket being public, so
the whole path works against `wrangler dev` with no bucket or custom domain.

**The declared Content-Type is ignored.** Format comes from the file signature
and dimensions from the format's own header — PNG IHDR, JPEG SOF markers, WebP
VP8/VP8L/VP8X, GIF logical screen, MP4 `tkhd`. A file whose header cannot be
read is rejected rather than stored, so prefixing a valid signature to something
else does not get through.

Accepted: **PNG, JPEG, WebP, GIF, and soundless MP4**. 16–4096 px per side, 4 MB
for stills and 12 MB for animated.

**"Soundless" is enforced, not trusted.** The MP4 box tree is walked and every
track's handler inspected; a single `soun` track rejects the upload. A muted
player is not the same guarantee — these files are played by clients we do not
control, and a venue flyer must never make noise at someone. Tested against real
remuxed files that differ only by their audio track.

The upload response includes `animated`, and the key's extension carries the
same signal, so a client that cannot animate can decide before downloading. The
Cyberdeck cannot draw a GIF or MP4 inline (ImGui has no frame timeline and no
video decoder) — it downloads and hash-verifies them, then offers to open the
local file outside the game.

Keys are `<group>/<id>/<sha256>.<ext>`, built entirely from validated path
segments and the content hash — never from a client-supplied filename. That
makes traversal impossible and every object immutable, so uploads can be cached
for a year and a replaced image is always a new key. The response carries
`x-content-sha256` so a client can verify what it downloaded.

### Errors

Always `{ "error": { "code": "...", "message": "..." } }`. Causes, stack traces,
and diagnostic context go to the structured log, never the response.

## Editing model

- **Publishing is always explicit.** `published` cannot be set through create or
  update — it has its own route. Editing a profile can never expose it, and
  re-running the importer never publishes anything.
- **New records arrive unpublished.** The column defaults to `0` with a
  `CHECK` constraint, so this holds even for a hand-written `INSERT`.
- **POST will not overwrite.** A retried create returns `409` rather than
  clobbering a record someone else added. `PUT` is the create-or-replace verb.
- **Unknown fields are rejected.** A typo'd key fails loudly instead of being
  silently dropped on save.
- **Future-dated news stays hidden** until its `publishedAt` passes, so an
  announcement can be written and published ahead of the night it belongs to.

## Announcements

An announcement carries two independent dates:

- `publishedAt` — when the post becomes visible. Future-dated posts stay hidden
  until then, so a flyer for Saturday can go up on Tuesday.
- `eventAt` — when the thing actually happens. Optional; omitted entirely from
  the response when unset.

`eventAt` is stored as an ISO 8601 UTC instant and **accepts a pasted Discord
timestamp** on input — `<t:1786222800:F>`, any style suffix, or none. The
response returns both forms:

```json
{
  "eventAt": "2026-08-08T21:00:00.000Z",
  "eventDiscord": "<t:1786222800:F>",
  "link": "https://discord.gg/example",
  "linkLabel": "RSVP on Discord",
  "flyerImage": "rooftop.png"
}
```

`eventDiscord` is a rendering of the instant, the same way `priceLabel` renders
`priceGil` — Discord shows it in each reader's own timezone, which is what a
venue with guests across every region wants. Storing the instant rather than the
string keeps the data sortable and lets the plugin render a real local date.

`link` is **https only**. A client may hand it to the operating system to open,
so the scheme is an allowlist — `javascript:`, `data:`, and `file:` URIs are
rejected, as are embedded `user:pass@` credentials. Relaxing this to allow plain
http is a one-line change in `optionalLink`, but it is a downgrade.

The flyer uses the shared image fields, exposed on news as `flyerUrl` (from R2)
and `flyerImage` (bundled fallback). `flyerKey`/`flyerImage` are accepted on
input, with `imageKey`/`bundledImage` as aliases.

## Images

Two separate fields, and the distinction matters:

- `image_key` — an R2 object key such as `menu/frostbite/<sha256>.png`.
  Content-hashed and immutable. Set by media upload, not by hand. The public
  `imageUrl` is composed from it at read time using `PUBLIC_MEDIA_BASE_URL`, so
  the CDN origin can change without a migration. Absent key → no `imageUrl`.
- `bundled_image` — the filename of art already shipped inside the plugin, e.g.
  `frostbite.png`. Lets a remote record fall back to bundled art rather than
  rendering blank.

Today every record uses `bundled_image`; `image_key` stays empty until Phase 4
uploads exist. That is why `mediaRevision` currently reads `none`.

**This is a real limitation for announcements.** Profiles and drinks change
rarely, so bundled art is fine for them. Event flyers are new art every time —
and until R2 upload exists, a new flyer means shipping a plugin release. Phase 4
is therefore a prerequisite for news being genuinely useful, not an optional
extra. The schema is ready for it; the upload path is not built.

## Layout

```text
src/
  index.ts              dispatch, error envelope, CORS selection, logging
  http.ts               JSON responses, ETag/304, body size cap
  types.ts              Env bindings and per-request context
  routes/
    router.ts           pattern matching, 404 vs 405
    health.ts           GET /v1/health
    public/             catalog, profiles, menu, news
    admin/              resource.ts builds CRUD; index.ts wires the three
  data/
    schema.ts           row and public types
    validation.ts       write-payload rules, shared with the importer
    profiles.ts menu.ts news.ts    parameterised SQL
  security/
    auth.ts             Cloudflare Access JWT verification
    errors.ts validate.ts headers.ts
  observability/log.ts  structured request logs
migrations/             0001 profiles, 0002 menu + news
scripts/import-seed.ts  bundled data -> validated SQL
seed/                   menu_items.json, news_posts.json
test/                   vitest, running inside workerd
```

## Security

- **The plugin is an untrusted public client.** No permanent write key ships in
  the DLL. Every public route assumes modified clients and bots.
- **Admin is a separate boundary.** Cloudflare Access fronts `/v1/admin/*`, and
  the Worker re-verifies the JWT itself — signature against the team's published
  keys, then issuer, audience, expiry, and the staff email allowlist. A
  client-supplied email header proves nothing on its own.
- **Admin CORS is exact-origin**, never the public wildcard, and an unrecognised
  Origin gets no CORS headers at all. The policy is chosen from the path in the
  entry point, so a handler cannot pick the wrong one.
- **All SQL is parameterised** and every column list is written out literally.
  No identifier is ever assembled from a request.
- **Identifiers are allowlisted** (`^[a-z0-9]+(-[a-z0-9]+)*$`), which also keeps
  `..` and path separators out of derived R2 keys. Bundled art filenames and
  media keys have their own patterns.
- **Bodies are capped** at 64 KB, checked against both the declared
  `Content-Length` and the actual byte count.
- **Errors are envelopes, not diagnostics.** Only `ApiError` messages reach
  clients; anything else becomes a generic 500.
- Logs carry request id, route pattern, status, and duration — never bodies,
  query strings, tokens, or profile request messages.

### Local development sign-in

`wrangler dev` cannot mint an Access token, so development accepts an
`x-dev-admin-email` header instead. It is gated on `ENVIRONMENT` — a wrangler
var, not a request value — **and** on the address already being in
`ADMIN_ALLOWED_EMAILS`, so it cannot be reached on a deployed Worker even if the
header is sent. A test asserts this.

```bash
curl -H "x-dev-admin-email: editor@thegrid.test" http://127.0.0.1:8787/v1/admin/menu
```

## Configuration

Non-secret values live in `wrangler.jsonc` under `vars`. Secrets are read from
`.dev.vars` locally (git-ignored) and `wrangler secret put` when deployed. See
`.dev.vars.example` for the expected names.

`database_id` in `wrangler.jsonc` is a zeroed placeholder. `wrangler dev` and
the tests use a local SQLite file and ignore it; a real id is only needed for
`--remote` and must not be committed.

> If `wrangler dev` behaves as though `.dev.vars` is missing, check for an
> orphaned dev server still holding the port — it reloads changed code but keeps
> the `.dev.vars` it read at startup.

## Data sources

- Profiles import from the repo's own `staff_profiles.json`. There is no second
  profile format to keep in sync; the importer reads `image` as the bundled art
  filename exactly as that file writes it.
- The drinks card lives in `seed/menu_items.json`, transcribed from the
  `DrinkMenuItem` array in `CyberdeckWindow.cs`. Prices are stored as integers
  (`15000`); the display form `"15 000"` is derived at read time.
- `seed/news_posts.json` is empty on purpose. Announcements are real venue
  communications and should be written through the admin API, not invented in a
  seed file.

Tests validate every bundled seed record against the same validators the admin
API uses, so a malformed seed file fails `npm test`.

## Cloud resources

Not to be run without approval from Carpe Nukem — listed for review:

```text
npx wrangler login
npx wrangler d1 create grid-cyberdeck
npx wrangler r2 bucket create grid-cyberdeck-media
npx wrangler d1 migrations apply grid-cyberdeck --remote
npx wrangler deploy
```

Development and production must use separate D1 databases and R2 buckets, and
D1 should be exported before any destructive migration.
