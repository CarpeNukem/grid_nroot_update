# The Grid Cyberdeck

Dalamud plugin for The Grid venue. It opens a cyberdeck-style app with venue address navigation, syncshell information, drinks menu, Discord link, local network presence, and Penumbra update tools.

## Setup

1. Install and enable Penumbra.
2. Load the plugin as a dev plugin or install it from the custom repository.
3. Use Settings > Update or `/thegrid update` to install/update the venue mod. The mod is organized under the `TheGrid` folder in Penumbra.
4. To enable/assign the venue collection, create a new, unassigned persistent Penumbra collection named `TheGrid`, then use Settings > Assign or run Update again.

The release source is locked to `CarpeNukem/grid_nroot_update`. The plugin checks GitHub releases and downloads the configured venue mod asset.

## Commands

- `/thegrid` opens The Grid Cyberdeck.
- `/thegrid update` checks GitHub releases and applies the latest matching venue mod.
- `/thegrid config` opens the configuration window.

## Notes

The plugin uses Penumbra's public IPC to install mod packages, organize the mod in Penumbra's mod tree, enable the configured mod in an existing collection, set mod priority, and assign that collection to a loaded object. Current Penumbra API V5 exposes collection lookup and assignment, but not named persistent collection creation, so `TheGrid` must exist before assignment but not before import.

## Dalamud Repository

Build the release package:

```text
dotnet build GridNrootUpdate.csproj -c Release
```

Upload `bin/Release/dist/GridNrootUpdate-0.4.2.zip` to a GitHub release named `plugin-v0.4.2`, then users can add this custom repository URL in Dalamud:

```text
https://raw.githubusercontent.com/CarpeNukem/grid_nroot_update/main/repo.json
```
