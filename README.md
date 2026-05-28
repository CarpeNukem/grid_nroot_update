# The Grid Cyberdeck

Dalamud plugin for The Grid venue. It opens a cyberdeck-style app with venue address navigation, syncshell information, drinks menu, Discord link, local network presence, and Penumbra update tools.

## Setup

1. Install and enable Penumbra.
2. Create a new, unassigned persistent Penumbra collection named `TheGrid`.
3. Load the plugin as a dev plugin or install it from the custom repository.
4. Use Settings > Update or `/thegrid update` to install/update the venue mod and assign the collection.

The release source is locked to `CarpeNukem/grid_nroot_update`. The plugin checks GitHub releases and downloads the configured venue mod asset.

## Commands

- `/thegrid` opens The Grid Cyberdeck.
- `/thegrid update` checks GitHub releases and applies the latest matching venue mod.
- `/thegrid config` opens the configuration window.

## Notes

The plugin uses Penumbra's public IPC to install mod packages, enable the configured mod in an existing collection, set mod priority, and assign that collection to a loaded object. Current Penumbra API V5 exposes collection lookup and assignment, but not named collection creation, so `TheGrid` must exist before the first update.

## Dalamud Repository

Build the release package:

```text
dotnet build GridNrootUpdate.csproj -c Release
```

Upload `bin/Release/dist/GridNrootUpdate-0.4.2.zip` to a GitHub release named `plugin-v0.4.2`, then users can add this custom repository URL in Dalamud:

```text
https://raw.githubusercontent.com/CarpeNukem/grid_nroot_update/main/repo.json
```
