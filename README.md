# The Grid Cyberdeck

Dalamud plugin for The Grid venue. It opens a cyberdeck-style app with venue address navigation, syncshell information, drinks menu, Discord link, local network presence, and Penumbra update tools.

## Setup

1. Install and enable Penumbra.
2. Load the plugin as a dev plugin or install it from the custom repository.
3. Open Settings and press **Install**, or use `/grid update`. The plugin downloads the venue mod, installs it in Penumbra, and completes setup automatically.
4. If a permanent Penumbra collection named `Grid` or `TheGrid` exists, the plugin uses it. Otherwise, it creates and manages a temporary collection automatically.

The release source is locked to `CarpeNukem/grid_nroot_update`. The plugin checks GitHub releases and downloads the configured venue mod asset.

## Commands

- `/thegrid`, `/grid`, or `/cyberdeck` opens The Grid Cyberdeck.
- `/thegrid update`, `/grid update`, or `/cyberdeck update` checks GitHub releases and applies the latest matching venue mod.
- `/thegrid repair`, `/grid repair`, or `/cyberdeck repair` downloads the current venue mod again, reinstalls it, retires the copy it replaces, and redoes the collection, mannequin and furniture setup. Use it when the venue looks wrong and you do not want to work out why.
- `/thegrid config`, `/grid config`, or `/cyberdeck config` opens the configuration window.
- `/thegrid vault`, `/grid vault`, or `/cyberdeck vault` opens the encrypted Cipher Vault authentication terminal.

Every command answers in chat, and an install reports each step there as well as in the deck: the release found, the download with its size and progress at every quarter, the Penumbra import, the setup, and the result.

The Cipher Vault creates a persistent seeded intrusion run with randomized technical packet frames, manual multi-layer decoding, forensic honeypots, trace penalties, graded clearance, and an S-rank encrypted payload. Closing the terminal preserves the current run; completing or explicitly aborting it permits a newly generated archive.

## Notes

The plugin uses Penumbra's public IPC to install venue mod packages and apply them to nearby venue mannequins. Existing permanent collections named `Grid`, `TheGrid`, or `The Grid` are supported. When none exists, the plugin uses a managed temporary collection and recreates it as needed.

When a new edition of the venue pack installs, the previous pack's settings are copied onto the new one in every collection, so the venue stays enabled wherever you had it. Older `Grid CityScape` editions are then removed from Penumbra and from disk, but only when their own meta.json carries a plain release number lower than the new pack's. Anything else named like a venue pack is switched off in the Grid collection and kept: the legacy `n_root_the_grid` and `TheGrid` packs, and any build whose version has a suffix (a work in progress such as `1.4.1-halloween.4`). Mods that are not venue packs are never touched. A repair is the one case where a pack with the same version as the new one is removed, and only when it is the copy the plugin itself installed before.

Packs ticked under **Other venue packs in Penumbra** in Settings are protected: the updater never removes them, switches them off, or mistakes them for the installed pack. This is for an edition under development kept beside the release pack.

The venue is at Light / Raiden / Mist, Ward 29, Plot 2, a large house, since 2026-11-27 (it was Ward 2, Plot 6). The address is built into the deck, and a published `address` page on the relay overrides it, so the venue can move again without a release; the deck keeps the last address the relay gave it. An install that saved the old address moves to the new one when it loads. Where you are is decided by that address: the game's own housing address has to match it, ward and plot included, and the venue mannequin counts as evidence only where no full address can be read. Rooms in the venue's house count as the venue; rooms in other houses and apartments never do.

At The Grid, the venue's interactive spots are marked with an activation zone: a holographic marker floating over the machine. The deck finds them itself, every second, so a zone follows its machine if it is moved:

- **Vending machines.** Activation zones are disabled for now.
- **Arcade cabinets.** The row of six cabinets the venue mod draws in place of a mannequin's Copper Earrings. Each cabinet gets a marker above its marquee, placed from the mannequin's own position, facing and size. Clicking one opens the game it runs. The games are a closed module, `GridArcade.dll`, shipped in the release package; a build without it has no arcade zones.
- **The pool table.** Any Billiard Table furnishing (item 44896) gets a marker over it that opens POOL: 8-ball, 9-ball or free pyramid (its own twelve-foot table, tight pockets, any ball struck at any other, first to eight points) against other players in the venue, watching their games, or practice alone or against the computer. It is found by the furniture itself, so placing, moving or adding a table needs nothing else. `/grid pool` opens the same table from anywhere.
- **The card tables.** Any Everkeep Dining Table furnishing (item 51258) on the house's top floor gets a CARDS marker over it that opens the card room, where you pick a game: BLACKJACK, alone at a private table, with friends who have its code, or at a public one with up to four others (chips are play money); HOLD'EM, Texas Hold'em, no limit, for two to six with play chips, at blinds and starting chips picked when the table is opened; or BLACK ICE, the Grid's liar's game for two to four. At Hold'em and BLACK ICE bots fill empty seats if the host wants them. Like the pool table, the tables are found by the furniture itself, so they can be moved or added; the same table on a lower floor is just a table. The CARDS markers float over the booths, higher than the pool table's so they clear seated players, and show from as far as the pool table's and the cabinets' do, on the same floor. (In the old house they were beacons, seen from 45 yalms and from another floor so guests downstairs knew the upstairs tables were open; the new house has no upstairs to call them to.) `/grid debug furniture` (Debug builds) lists what the game says the furniture near you is, and every table in the house floor by floor. `/grid cards` opens the card room from anywhere, and `/grid blackjack`, `/grid holdem` (or `/grid poker`) and `/grid blackice` (or `/grid ice`) go straight to a game. Every game is dealt by the relay's card hall, so no deck ever holds a card it should not see. Players are their characters.

Click a marker while standing within 3 yalms of the zone and the machine opens; from further away the marker tells you to move closer. Walking away closes the window again, and walking away from the pool table leaves a game in play to the opponent; walking away from a card table closes the card room, or the game picked there, which gives up the seat. The zones can be turned off under **Activation zones** in Settings.

The arcade's high scores are shared across The Grid. A cabinet's table shows the venue's best five with your own scores among them, and when a score of yours would make the venue's board it is posted to the relay: the name you signed at the cabinet and the score, nothing else. The boards arrive with the content the deck already fetches, so showing them costs the relay no extra request, and a score that would not make the board is never sent. Every score you sign is also kept in `arcade_scores.json` in the plugin's config folder. Turn **Shared arcade high scores** off in Settings and nothing is posted and the tables show only your own.

With automatic updates off, a release that is waiting to be installed is still downloaded at login, so pressing **Update** later is an install rather than a wait. The cached file is used only while it is exactly the size GitHub reports for the asset.

## Dalamud Repository

Build the release package:

```text
dotnet build GridNrootUpdate.csproj -c Release
```

When the private arcade repository is checked out beside this one as `../grid_arcade`, the build compiles it too and puts `GridArcade.dll` in the package. Without it, the package is built without the arcade.

Upload `bin/Release/dist/GridNrootUpdate-2.0.6.zip` to a GitHub release named `plugin-v2.0.6`, then bump the Grid entry in the n_root feed to match. That feed is what users add in Dalamud:

```text
https://raw.githubusercontent.com/CarpeNukem/n_root/main/repo.json
```
