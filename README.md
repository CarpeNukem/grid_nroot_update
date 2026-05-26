# TheGrid Updater

Dalamud plugin for keeping a configured Penumbra venue mod updated from GitHub releases and assigned to the loaded `Chromiel` NPC/mannequin through the `TheGrid` collection.

## Setup

1. Install and enable Penumbra.
2. Create a persistent Penumbra collection named `TheGrid`.
3. Load the plugin as a dev plugin.
4. Configure the release asset pattern if needed:

```text
/thegrid asset *.pmp
```

The release source is locked to `CarpeNukem/grid_nroot_update`. The plugin always checks GitHub's latest release and downloads the matching asset from that release.

## Commands

- `/thegrid status` prints current mapping status.
- `/thegrid asset <glob>` sets the release asset pattern. The default is `n_root_the_grid_beta.pmp`.
- `/thegrid update` checks GitHub's latest release and applies it if it has not already been applied.
- `/thegrid assign` reapplies `TheGrid` to currently loaded `Chromiel` objects.

## Notes

The plugin uses Penumbra's public IPC to install mod packages, enable the configured mod in an existing collection, set mod priority, and assign that collection to a loaded object. Current Penumbra API V5 exposes collection lookup and assignment, but not named collection creation, so `TheGrid` must exist before the first update.

## Dalamud Repository

Build the release package:

```text
dotnet build GridNrootUpdate.csproj -c Release
```

Upload `bin/Release/dist/GridNrootUpdate-0.1.0.0.zip` to a GitHub release named `plugin-v0.1.0`, then users can add this custom repository URL in Dalamud:

```text
https://raw.githubusercontent.com/CarpeNukem/grid_nroot_update/main/repo.json
```
