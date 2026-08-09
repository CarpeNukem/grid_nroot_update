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
- `/thegrid config`, `/grid config`, or `/cyberdeck config` opens the configuration window.
- `/thegrid vault`, `/grid vault`, or `/cyberdeck vault` opens the encrypted Cipher Vault authentication terminal.

The Cipher Vault creates a persistent seeded intrusion run with randomized technical packet frames, manual multi-layer decoding, forensic honeypots, trace penalties, graded clearance, and an S-rank encrypted payload. Closing the terminal preserves the current run; completing or explicitly aborting it permits a newly generated archive.

## Notes

The plugin uses Penumbra's public IPC to install venue mod packages and apply them to nearby venue mannequins. Existing permanent collections named `Grid`, `TheGrid`, or `The Grid` are supported. When none exists, the plugin uses a managed temporary collection and recreates it as needed.

## Dalamud Repository

Build the release package:

```text
dotnet build GridNrootUpdate.csproj -c Release
```

Upload `bin/Release/dist/GridNrootUpdate-0.8.1.zip` to a GitHub release named `plugin-v0.8.1`, then users can add this custom repository URL in Dalamud:

```text
https://raw.githubusercontent.com/CarpeNukem/grid_nroot_update/main/repo.json
```
