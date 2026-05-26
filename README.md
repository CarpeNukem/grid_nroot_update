# TheGrid Updater

Dalamud plugin for keeping a configured Penumbra venue mod updated from GitHub releases and assigned to the loaded `Chromiel` NPC/mannequin through the `TheGrid` collection.

## Setup

1. Install and enable Penumbra.
2. Create a persistent Penumbra collection named `TheGrid`.
3. Load the plugin as a dev plugin.
4. Configure the release source in game:

```text
/thegrid repo owner/repository
/thegrid asset *.pmp
/thegrid version 1.0.0
```

The default release tag is `v{version}`. For `1.0.0`, the plugin downloads assets from the GitHub release tag `v1.0.0`.

## Commands

- `/thegrid status` prints current mapping status.
- `/thegrid repo <owner>/<repo>` sets the GitHub release repository.
- `/thegrid asset <glob>` sets the release asset pattern, for example `TheGrid-*.pmp`.
- `/thegrid version <version>` bumps the desired version and queues update reconciliation.
- `/thegrid update` forces a redownload/reapply for the current desired version.
- `/thegrid assign` reapplies `TheGrid` to currently loaded `Chromiel` objects.

## Notes

The plugin uses Penumbra's public IPC to install mod packages, enable the configured mod in an existing collection, set mod priority, and assign that collection to a loaded object. Current Penumbra API V5 exposes collection lookup and assignment, but not named collection creation, so `TheGrid` must exist before the first update.
