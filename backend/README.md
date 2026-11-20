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

### Arcade scores

The arcade's venue-wide high-score boards. Shaped around traffic: the venue is
on the free plan, and a feature every deck uses must not add a request every
deck makes.

- **Reading costs nothing.** There is no arcade read route. The boards are the
  `arcade` field of `/v1/catalog`, which every deck already fetches, so showing
  them adds no request. What it does add is bytes: a new score changes the
  catalogue, so each deck's next check is a full response rather than a `304`.
- **Writing is one request, and only for a score that would make a board.** The
  deck compares against the board it already has and posts nothing otherwise.
  Scores it could not send go up together in the next request.
- **A board keeps its best ten.** Every write trims the board, so the table
  stays a few dozen rows and the catalogue's extra query stays cheap.

```json
"arcade": {
  "depth": 10,
  "boards": {
    "firewall": [{ "initials": "CRPNKM", "score": 26406 }]
  }
}
```

| Route | Effect |
| --- | --- |
| `POST /v1/arcade/scores` | Public. `{"scores": [{"id", "board", "initials", "score"}]}`, up to 25. |
| `GET /v1/admin/arcade/scores` | Every score with its id, for moderation. |
| `DELETE /v1/admin/arcade/scores/:id` | Removes one. The next score down moves up. |

`POST` answers `{"results": [{"id", "outcome"}]}`, where the outcome is `placed`,
`not-placed` or `rejected`. All three are final: a deck never resends a score
that was answered for.

- `id` is 32 hex characters chosen by the deck, so a retry of a request whose
  answer was lost does not put the run on the board twice.
- `board` is a game's id from the allowlist in `src/data/arcade.ts`, or today's
  or yesterday's daily board (UTC): `night-circuit-daily-<yyyymmdd>`,
  `neon-nibbles-daily-<yyyymmdd>`, or `no-fly-zone-v2.daily.<yyyymmdd>`.
  All current cabinets are registered, including LAST CALL and MAX-TAC;
  versioned game IDs retain separate rankings. **A new game needs its
  id added there and a deploy** before its scores are accepted; until then they
  are `rejected` and stay on the player's machine.
- `initials` is one to six of `A-Z`, `0-9` and `-`. `score` is a whole number
  from 1 to 999,999,999.
- A malformed envelope or id is a `400`. A bad board, name or score fails that
  one entry and not the batch.
- Throttled to six a minute per address, on top of the public limit.

**These scores are not trustworthy and are not meant to be.** The games run on
the player's machine, so a forged score only needs a `curl`. Nothing identifies
a player either: a row is a name typed at a cabinet, a number and a time, with
no character, account or address. Moderation is the admin `DELETE`, which the
admin page's **Arcade** tab puts behind a Remove button on each line of each
board. The change is in the catalogue within half a minute, and reaches each
deck the next time it checks in, which for a deck left closed is up to an hour.

### Pool hall

Multiplayer 8-ball, 9-ball and free pyramid for the arcade's pool table, with room to watch:
one hibernating Durable Object, `PoolHall` (`src/pool/hall.ts`), holding the
lobby and every table. Shaped around the free plan like the boards:

- **Players are characters.** A deck connects as the character logged in,
  `name=Forename Surname@World` (URL-encoded), and that is who the lobby, the
  table and the opponent see. The hall still takes the initials decks sent
  before (up to six of `A-Z`, `0-9`, `-`), so a deck not yet updated keeps
  playing. The card hall takes characters only.
- **One Worker request per player per visit.** `GET /v1/pool?name=...` with a
  WebSocket upgrade, plus `&v=5` for the protocol the deck speaks and
  `&resume=<seat token>` to get back to a kept seat.
  Everything after that is messages on the socket, which go to the Durable
  Object and never through the Worker again.
- **About two incoming messages a shot.** The shooter's deck plays the shot and
  sends it, then sends the table once the balls stop. The hall checks the turn
  and the shot number, stores the settled table and passes both on. It never
  simulates anything. Cue previews add at most two and a half a second while a
  player aims. Incoming messages are what the hall is billed for, at twenty to
  a request; outgoing ones are free. A measured game: 36 shots, 74 messages,
  17 KB.
- **Nothing between messages.** The hibernation API keeps the sockets open at
  the edge while the object is evicted, so an idle table costs no duration.
  Keep-alives are protocol pings, answered by the runtime; a text `ping` gets a
  `pong` from the auto-response, also without waking it. Tables live in the
  object's SQLite storage and each socket carries its own attachment, so
  nothing is lost when it sleeps.

| Deck sends | Hall does |
| --- | --- |
| `open` `{private, rules?, assist?}` | Opens a table with a four-letter code and seats the sender. `rules` is `"eight"`, `"nine"` or `"pyramid"` (free pyramid, protocol 3 only), `"eight"` when left out. `assist` is `true` or `false`, `true` when left out: `false` is a tournament table, where both decks draw only the cue ball's line and no collision predictions. Anything else in either is refused (`bad-message`). The table keeps both for good, rematches included. Public tables are listed in the lobby. |
| `join` `{code}` | Seats the sender opposite the host and starts the game, host to break. A deck on protocol 2 is refused a pyramid table, and a deck of other physics any table (`needs-update`, `older-deck`). |
| `watch` `{code}` | Takes the sender to the table with that code to watch it: open, in play or over, private ones too, with the same refusals as `join`. |
| `aim` `{seq, a, p, cx?, cy?, k?, s?}` | Passes a cue preview to the opponent and the watchers. Dropped if closer than 250 ms to the last. |
| `shot` `{seq, shot}` | Passes the shot to the opponent and the watchers. Only the player whose turn it is, for the shot the table is on. |
| `settle` `{seq, turn, winner, state}` | Stores the settled table, moves the turn on, ends the game on a winner, and passes it on to the opponent and the watchers. |
| `rematch` | Racks a new game once both players ask. The break alternates. |
| `leave` | Gives up the seat. A game in play goes to the opponent. A watcher just stops watching. |
| `sync` | Sends the sender's table again, a watcher's included. |

`k` is the called pocket, a whole number from 0 to 5, on a preview or inside a
shot. Left out, nothing is called; any other value makes the shot `bad-message`
and drops the preview, like any other bad field. `s` is the ball struck, a whole
number from 0 to 15, on a preview or inside a shot, the same way: left out, it
is the cue ball, 0, and only free pyramid strikes another. The settled `state`
is the decks' own, and carries free pyramid's points; the hall does not read it.

The hall sends `welcome` (protocol version, seat token, lobby), `lobby`,
`table` (one seat's view: rules, assist, players, watchers, status, seed, shot
number, turn, winner, settled state), the opponent's `aim`/`shot`/`settle`,
`closed` `{code, why}` when a table is cleared away, and `error` with a code.

- **The lobby.** Up to 20 public tables, newest first: open ones whose host is
  there, to join, and games in play, to watch. Finished games are not listed.
  Each entry is `{code, host, rules, assist, status, players, watchers}`, with
  `status` `waiting` or `playing` and `players` the two names, `null` for an
  empty chair. It is sent again whenever one of those changes, a watcher count
  included.
- **Watching.** A watcher is a socket at a table without a seat. Its `table`
  view has `seat: null` and `watching: true` (a player's has `false`), and it
  is sent every `aim`, `shot` and `settle` the opponent is, in the same shape.
  It has no say in the game: a shot, settle or rematch from it is refused
  (`not-seated`), a preview dropped, and it cannot open, join or watch another
  table (`already-seated`) until it sends `leave`. A table takes 8 watchers; the
  ninth is refused (`no-room`). Watchers count toward the 200 sockets. When
  both players have left, watchers get `closed` with `why: "empty"` and are
  back in the lobby. A table cleared for sitting idle sends `why: "idle"`.
- **Protocol versions.** The hall speaks 5: free pyramid's corner pockets
  corrected to a 5.5 mm corridor, on top of 4's balls that part along the line
  between their centres as they touch, a change to how the decks play every
  shot, 3's free pyramid and the ball a shot strikes and 2's 9-ball,
  called pockets, watching and tables without aim assist. A deck says its
  version when it connects (`v=5`) and `welcome.v` is that version back, down
  to 2, the hall's newest for a deck newer still. A deck that says nothing is
  one from before free pyramid, on 2: it is never listed a pyramid table,
  cannot open one (`bad-message`) and is refused one's code (`needs-update`),
  since it would read the table as 8-ball. That is the choice made over bumping
  every deck: an old deck is not asked to update until it meets a game it
  cannot play.
- **One physics to a table.** Both decks at a table play every shot
  themselves, so a table records how the deck that opened it plays a shot
  (`physics`: 1 for protocols 2 and 3, 2 for 4, 3 for 5; a table stored before
  it was recorded is 1). Only decks of that physics are listed it, seated at it
  or let watch it: a deck of older physics is refused with `needs-update`, one
  of newer physics with `older-deck`. So decks on 2 and 3 still play each
  other, and decks on 5 play decks on 5.
- A deck refuses to play on any version but its own and asks to be updated, so
  a deck still on 1 (welcomed with 2) is told to update, and a deck on 5
  meeting a hall not yet updated waits for it: the relay goes out before the
  deck. The hall only carries `assist`; the decks draw it.
- **Dropped connections.** A seat is kept for a minute. A deck that reconnects
  with its seat token gets its table back. After that, a game in play goes to the
  player who stayed, and an open table is cleared. A table nobody has touched
  for two hours is cleared too. Both happen on the object's one alarm.
- **Its own budget.** Text frames only, up to 4,096 characters, a settled state
  up to 2,500. A socket sending more than 40 messages in 10 seconds is closed
  (1008), and so is one sending an oversized frame (1009) or binary (1003).
  At most 200 sockets and 64 tables. Connections are throttled to ten a minute
  per address, on top of the public limit.
- **Not trustworthy, and not scored.** The shooter's deck judges its own shots,
  so a modified deck could claim anything. Nothing is ranked, and nothing
  identifies a player beyond the character name their deck reads from the
  game, which a modified deck could fake.

### Card hall

The arcade's card games: blackjack, Texas Hold'em, and BLACK ICE, the liar's
game, picked at any of the venue's card tables. One hibernating
Durable Object, `CardHall` (`src/cards/hall.ts`), holds the card lobby and
every card table. It is built like the pool hall, with one difference that
matters: **the hall deals.** A pool table hides nothing, so each deck can play
every shot itself. A card table has a shoe, a hole card, other players' hands,
the rest of a Hold'em deck, and at BLACK ICE where each player's black ICE
sits; a deck that held any of
that could be read by anyone who decompiles the module. So every shuffle (the
platform's cryptographic randomness), every deal, every move and every
judgement happens in the hall, and a deck is only ever sent what its seat may
see. Each game's rules are a module of their own, met through `games.ts`:
`blackjack.ts`, `holdem.ts` (with `poker.ts`, which ranks hands) and
`blackice.ts`. The hall seats players, passes each game its
moves, runs the clocks and sends each seat its view.

- **Players are characters**, as at the pool hall, and nothing else: each
  name is 2 to 15 capitalised letters, apostrophes or hyphens, then `@` and a
  one-word home world, as `Forename Surname@World`. Initials are refused.
- **One Worker request per player per visit.** `GET /v1/cards?name=...`, with
  `&resume=<seat token>` to get back to a kept seat, metered on a connect limit
  of its own (`CARD_CONNECT_LIMITER`, ten a minute per address).
- **Two or three incoming messages a round.** A bet, then a move or two. The
  hall answers every change with the whole table, which costs nothing to send.
  Measured: 2.2 to 2.6 messages a round over twelve solo rounds.
- **Nothing between messages**, as in the pool hall: hibernation, auto-answered
  `ping`, tables in SQLite (`card_tables`), the shoe included.

| Deck sends | Hall does |
| --- | --- |
| `open` `{private, game?}` | Opens a table with a four-letter code and seats the sender. `game` is `"blackjack"` (the default), `"holdem"` or `"blackice"`, for the table's whole life. Public tables are listed in the lobby. |
| `join` `{code}` | Seats the sender in the first empty seat: five at blackjack, six at Hold'em, four at BLACK ICE. During a hand or a game they sit out until the next one. |
| `bet` `{amount}` | Places, changes or (with 0) takes back the sender's stake for the next round: 10 to 500, in tens, no more than their chips. Deals at once when everyone seated has bet; otherwise the others have 15 seconds from the first bet. |
| `act` `{move, seq}` | `hit`, `stand`, `double` or `split`, on the hand on turn, only from its player and only against the table's current `seq`, so a double click is one move (`out-of-order`). |
| `topup` | Back to 1,000 chips, only once the player cannot cover the smallest bet (`has-chips`). |
| `leave` | Gives up the seat, and the stake on it. Play moves on without them. |
| `sync` | Sends the sender's table again. |

`bet`, `act` and `topup` are blackjack's; Hold'em's and BLACK ICE's are below. A message
meant for another game's table is refused (`unknown-message`).

The hall sends `welcome` (protocol version 1, seat token, blackjack's limits,
the lobby), `lobby` (public tables with a person there: code, game, the seats'
names, bots included, whether a hand is in play), `table` after every change,
`closed` `{code, why}`, and `error` with a code. Every `table` names its game,
and the rest of it is that game's view.

#### Blackjack

- **What a seat sees.** `table` holds the phase (`betting` or `playing`), the
  round and `seq`, `left` (milliseconds on the clock, or null), the dealer's
  cards with the hole card as `null` while it is down, `shoe` (a count, never
  the cards), the hand on turn, the sender's own `moves`, and each seat's name,
  chips, next bet and hands. A hand has its cards, stake, whether it was
  doubled or split, and once the round settles its `result` (`blackjack`,
  `win`, `push`, `lose`, `bust`) and what it `paid`, stake included. The last
  round's hands stay up, with their results, until the next deal. Cards are two
  characters, rank then suit: `AS`, `TD`, `9H`.
- **The rules.** Six decks, a fresh shoe once fewer than 78 cards are left.
  Dealer stands on all 17s and peeks under an ace or a ten. Blackjack pays 3 to
  2. Double on any two cards and after a split; split any pair, tens of any
  kind included, to four hands; split aces take one card each, and 21 on one is
  not blackjack. No insurance and no surrender. Chips are play money: 1,000 to
  sit down with, nothing carried between tables, nothing ranked.
- **The clocks.** Bets close 15 seconds after the first one; a player has 30
  seconds a decision before their hand stands. A player who is away does not
  hold up the bets, and their turn stands on its clock. A seat is kept for a
  minute, then given up with its stake; a table with nobody left, or untouched
  for two hours, is cleared. All of it runs on the object's one alarm.
#### BLACK ICE

The Grid's liar's game, for two to four. Twenty cards: six each of queens,
kings and aces, and two jokers, which are wild. Each round every player still
in is dealt five and the hall names a table rank. A player lays one to three
cards face down, claiming they are all that rank, or calls LIAR on the last
cards laid. A call turns them over: if all were the rank or jokers the caller
was wrong, otherwise the liar was caught, and whoever lost jacks into the ICE.
Each player's ICE has six slots, one of them black, hidden by the hall when the
game starts; each jack-in tries the next slot, so the odds climb from one in
six to certain. A player on the black slot flatlines and is out; the last one
standing wins. A player with no cards is passed over, and when nobody but the
player on turn has any, they must call.

| Deck sends | Hall does |
| --- | --- |
| `addbot` | Fills the first empty seat with a bot (`Glitch@Ice`, `Daemon@Ice`, ...). Only the host, the person in the lowest seat, and only between games (`not-host`, `not-waiting`). |
| `removebot` `{seat}` | Takes that seat's bot away, on the same terms (`no-bot`). |
| `start` | Starts a game, or the next one, with everyone seated: fresh ICE, a black slot hidden in each. The host only, with at least two seated, bots counting (`too-few`). |
| `play` `{cards, seq}` | Lays the cards at those places in the sender's hand, one to three of them, on their turn (`bad-play`, `out-of-order`). |
| `call` `{seq}` | Calls LIAR on the last cards laid, on the sender's turn and not on their own (`cannot-call`). |

- **What a seat sees.** The phase (`waiting`, `playing`, `reveal` while a call
  plays out, `over`), the round, `seq`, `left`, the host's seat, the table rank,
  whose turn it is, how many cards are down, who laid the last ones and how
  many, the sender's own `hand` and `moves`, and each seat's name, whether it is
  a bot, how many cards it holds, how many slots it has tried, and whether it
  is out. Laid cards are a count until a call: `reveal` then holds the caller,
  the liar, the cards, whether they were true, the victim, the slot tried and
  whether it flatlined them, and stays up through the reveal and to the end of
  a game it ended. Nobody's hand but the sender's, and nobody's black slot, is
  ever sent. Cards are `QS`, `KH`, `AD` and so on, and the jokers `ZR` and `ZB`.
- **Bots** play on the hall's own clock, a second and a half to three after
  their turn comes, so the table can follow them, and know only what a player
  in their seat would: their own hand and what was laid. A bot calls a claim it
  can prove false (more of the rank laid than it cannot see), and otherwise with
  a chance that grows as the claim gets less likely; it lays true cards when it
  has them, sometimes slipping a false one in, and bluffs with one or two when
  it has none. A table with no person left at it, only bots, is cleared away.
- **The clocks.** A player has 30 seconds a decision; when it runs out the hall
  lays their first card, or calls if that is all they can do. A call's reveal
  lasts five seconds before the next deal. Someone who leaves during a game is
  out of it: their cards go with them and their last play can no longer be
  called.

#### Hold'em

Texas Hold'em, no limit, for two to six, for play chips: everyone sits down
with 1,000, and the blinds are 5 and 10, fixed. Once two players with chips
are seated, a hand is dealt three seconds later, and the next one six seconds
after each showdown, for as long as two can play. Two cards each face down,
then the flop, turn and river face up, with a round of betting before each and
after the last. A raise is at least the size of the last full one, up to all a
player has; an all-in short of that does not reopen the betting to those who
have already acted on the street. Heads up, the button posts the small blind
and acts first before the flop. The best five of seven win; a tie splits the
pot, the odd chip to the first winner left of the button, and a player all in
can win only what they matched, the rest going into side pots.

| Deck sends | Hall does |
| --- | --- |
| `act` `{move, to?, seq}` | `fold`, `check`, `call`, or `raise` with `to`, what the sender's bet on this street comes to: at least the least raise, at most everything, or all in for less (`bad-raise`, `cannot`, `out-of-order`). On the sender's turn only. |
| `rebuy` | Back to 1,000 chips, only once the sender has run out and is not still in a hand (`has-chips`). |
| `addbot` | Fills the first empty seat with a bot (`Chrome@Grid`, `Vex@Grid`, ...), which plays from the next hand. The host only, the person in the lowest seat, at any time. |
| `removebot` `{seat}` | Sends that seat's bot away; in the middle of a hand it folds first (`no-bot`). |

- **What a seat sees.** The phase (`waiting`, `betting`, `runout` while the
  board is dealt out to players all in, `showdown`), the hand number, `seq`,
  `left`, the host, the blinds and the starting stack, the button and the
  blinds' seats, the street, the board, whose turn it is, the bet to match, the
  pot gathered so far, the sender's own two cards (`hole`) and `moves` (fold,
  check, the price of a call, and the least and most a raise may come to), and
  each seat's name, bot or not, stack, bet on this street, whether they are in
  the hand, folded or all in, what they last did, and how many cards they hold.
  Nobody's cards but the sender's, and never the deck, until they are turned
  over: everyone still in shows theirs once all but one are all in, and at a
  showdown. `result` then holds each pot, who took it and with what hand
  (`PAIR OF KINGS`, `SEVENS FULL OF TWOS`), each hand turned over with the five
  cards that made it, and what each seat won; a hand everyone folded shows
  nothing.
- **Bots** play on the hall's clock, a second or two after their turn comes,
  and know only their own cards and the board: they deal the unseen cards out
  at random a few hundred times against as many hands as are still in to
  judge their chances, then bet a good hand, call when the price is right,
  bluff now and then when nobody has bet, and otherwise check or fold. A bot
  that runs out buys back in by itself.
- **The clocks.** A player has 30 seconds a decision, then checks if they can
  and folds if not; one who is away gets three seconds, and is not dealt into
  the next hand until they are back. Someone who leaves mid-hand folds, and
  what they put in stays in the pots. A player who runs out sits out until
  they buy back in.

#### Both

- **Its own budget.** Text frames only, up to 512 characters. A socket sending
  more than 60 messages in 10 seconds is closed (1008); a fast player alone
  sends around twenty. At most 200 sockets and 64 tables.

### Short links

`https://grid.nroot.io/s/<code>` redirects (302) to an address staff chose:
a TinyURL for the venue, mostly for Discord attachments. Links are made in the
admin panel's **Links** tab (paste, Enter, copy) or through the admin API.
Anyone can follow one, but only an administrator can make one, so strangers
can't use the domain to disguise their own addresses.

| Route | Effect |
| --- | --- |
| `GET /s/:code` | Public. 302 to the target; `404 LINK_NOT_FOUND` if there is none. Case-insensitive. |
| `GET /v1/admin/links` | Every link, newest first, plus `discordRefresh` (whether a bot token is set). |
| `POST /v1/admin/links` | `{"url": "https://…", "code": "optional"}`. `201` new, `200` if the address already has a link, `409` if a chosen code is taken. |
| `DELETE /v1/admin/links/:code` | Remove. The link 404s from then on. |

- Generated codes are six characters of lowercase letters and digits, without
  `l`, `1`, `o` or `0`. A chosen code is 1–40 lowercase letters, digits or
  inner dashes.
- Only absolute `http`/`https` addresses are accepted. No embedded
  credentials, no links to other short links, and at most 2048 characters.
- Links cannot be edited. A link already posted in a channel can never quietly
  start pointing somewhere else; make a new one instead.
- The same Discord file pasted twice (even with a different signature) gives
  back the link it already has.

**Discord attachments expire.** Discord signs each copied attachment address
(`?ex=…&is=…&hm=…`), and the signature runs out after about a day. With a
`DISCORD_BOT_TOKEN` secret set, a short link to a Discord file asks Discord
for a fresh signature when the one it holds has less than an hour left. It then
keeps working for as long as the message stays on Discord. Without the token,
the link only works until the copied address expires, and the admin panel says
so. The re-signing endpoint (`POST /api/v10/attachments/refresh-urls`) is not in
Discord's published reference. If Discord refuses, the link falls back to the
last address it held and waits ten minutes before asking again.

Setting up the token:

1. In the Discord Developer Portal, create an application, add a bot, and copy
   its token. It needs no permissions or intents.
2. `npx wrangler secret put DISCORD_BOT_TOKEN --env production`
3. If links still don't refresh, look for `short-link-refresh-failed` in the
   Worker logs. If it shows `rejected` with a 403, invite the bot to the server
   the files are posted in.

**Free plan cost.** A click is one Worker request and one D1 row read by
primary key. A Discord link adds one call to Discord and one D1 write about once
a day, and only on days someone follows it. Simultaneous clicks share one
refresh. The refusal back-off is kept in memory, so it costs no writes.
Browsers may reuse a redirect for five minutes (`private, max-age=300`), which
saves a request on every repeat click. This is also the longest a deleted link
keeps working in a browser that had already followed it. Clicks count toward
the public read limit (60 a minute per address) like any other public route.

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
    public/             catalog, profiles, menu, news, arcade (score submit)
    admin/              resource.ts builds CRUD; index.ts wires the three;
                        arcade.ts lists and removes scores
  data/
    schema.ts           row and public types
    validation.ts       write-payload rules, shared with the importer
    profiles.ts menu.ts news.ts    parameterised SQL
    arcade.ts           score boards: allowlist, limits, SQL
  pool/                 the pool hall: protocol.ts, hall.ts (Durable Object)
  cards/                the card hall: protocol.ts, games.ts (where the hall
                        meets each game), blackjack.ts, holdem.ts and
                        blackice.ts (each game's rules, deals and bots),
                        poker.ts (ranks Hold'em hands), hall.ts (Durable Object)
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
- **There is exactly one anonymous write**, the arcade score submission. It can
  only add a row to an allowlisted board that trims itself to ten, it is
  throttled separately, and everything it stores is checked against a narrow
  pattern first. See "Arcade scores" above.
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
