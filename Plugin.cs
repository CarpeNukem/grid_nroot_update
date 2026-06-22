using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GridNrootUpdate;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/thegrid";

    private readonly CancellationTokenSource lifetime = new();
    private readonly GitHubReleaseClient github = new();
    private readonly PenumbraIpc penumbra;
    private readonly CyberdeckWindow cyberdeckWindow;
    private readonly ConfigWindow configWindow;
    private readonly object modAddedLock = new();
    private volatile bool reconcileQueued;
    private volatile bool reconcileForceDownload;
    private volatile bool reconcileRunning;
    private volatile bool updateCheckRunning;
    private bool venueUpdateCheckDoneThisZone;
    private bool startupActionDone;
    private uint lastAutoOpenedTerritory;
    private bool modAddedSubscribed;
    private CancellationTokenSource? zoneTickCts;
    private TaskCompletionSource<string>? pendingModAdded;
    private bool? cachedPenumbraAvailable;
    private long lastPenumbraAvailableCheckTick;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<PluginService>();
        Config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        Config.GetPrimaryMapping();
        Config.Save();

        penumbra = new PenumbraIpc(pluginInterface);
        var (textures, textureLoadSource) = LoadTextures();

        configWindow = new ConfigWindow(
            Config,
            () => QueueReconcile(),
            () => _ = AssignAllAsync(lifetime.Token));

        cyberdeckWindow = new CyberdeckWindow(
            Config,
            penumbra,
            textures,
            textureLoadSource,
            () => QueueReconcile(),
            () => QueueReconcile(forceDownload: true),
            () => _ = AssignAllAsync(lifetime.Token),
            () => CheckForUpdates(),
            () => configWindow.IsOpen = true);

        PluginService.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open The Grid cyberdeck app and manage venue sync/update tools.",
            ShowInHelp = true,
        });

        PluginService.ClientState.Login += OnLogin;
        PluginService.ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginService.Framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        if (PluginService.ClientState.IsLoggedIn)
        {
            QueueStartupAction();
            QueueVenueUpdateCheck();
            QueueVenueAutoOpenCheck();
        }

        if (!Config.FirstRunCompleted)
            OpenMainUi();
    }

    public PluginConfig Config { get; }

    public void Dispose()
    {
        lifetime.Cancel();
        PluginService.Framework.Update -= OnFrameworkUpdate;
        PluginService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        PluginService.ClientState.Login -= OnLogin;
        if (modAddedSubscribed)
            penumbra.UnsubscribeModAdded(OnPenumbraModAdded);
        PluginService.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginService.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginService.PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginService.Commands.RemoveHandler(CommandName);
        github.Dispose();
        zoneTickCts?.Dispose();
        lifetime.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var split = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var subCommand = split.Length == 0 ? string.Empty : split[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "":
                OpenMainUi();
                break;
            case "update":
                QueueReconcile();
                PluginService.Chat.Print("Update queued. Checking Penumbra and latest The Grid release...", "TheGrid");
                break;
            case "config":
                OpenConfigUi();
                break;
            default:
                PluginService.Chat.PrintError($"Unknown command '{args}'. Use /thegrid, /thegrid update, or /thegrid config.", "TheGrid");
                break;
        }
    }

    private void OpenMainUi()
        => cyberdeckWindow.IsOpen = true;

    private void TrySubscribePenumbraEvents()
    {
        if (modAddedSubscribed)
            return;

        try
        {
            penumbra.SubscribeModAdded(OnPenumbraModAdded);
            modAddedSubscribed = true;
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not subscribe to Penumbra ModAdded yet.");
        }
    }

    private void OpenConfigUi()
        => configWindow.IsOpen = true;

    private void DrawUi()
    {
        cyberdeckWindow.Draw();
        configWindow.Draw();
    }

    private void OnLogin()
    {
        startupActionDone = false;
        QueueStartupAction();
        QueueVenueAutoOpenCheck();
    }

    private void OnTerritoryChanged(uint _)
    {
        venueUpdateCheckDoneThisZone = false;
        QueueVenueUpdateCheck();
        QueueVenueAutoOpenCheck();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (reconcileQueued && !reconcileRunning)
        {
            reconcileQueued = false;
            reconcileRunning = true;
            Task.Run(ReconcileAsync, lifetime.Token);
        }
    }

    private void QueueUpdateCheck()
    {
        if (updateCheckRunning)
            return;

        updateCheckRunning = true;
        Task.Run(async () =>
        {
            try
            {
                var mapping = Config.GetPrimaryMapping();
                if (string.IsNullOrWhiteSpace(mapping.LastAppliedVersion))
                {
                    PluginService.Log.Information("Check for updates skipped: no version applied yet (press Update to install).");
                    cyberdeckWindow.PendingUpdateVersion = null;
                    return;
                }

                PluginService.Log.Information("Checking for updates (manual mode); installed v{Installed}.", mapping.LastAppliedVersion);
                var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, lifetime.Token).ConfigureAwait(false);

                if (!string.Equals(latestAsset.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    PluginService.Log.Information("New update found: v{Version} (installed v{Installed}).", latestAsset.Version, mapping.LastAppliedVersion);
                    cyberdeckWindow.PendingUpdateVersion = latestAsset.Version;
                    PluginService.Chat.Print($"The Grid mod update available: v{latestAsset.Version}. Open Settings and press Update to install.", "TheGrid");
                }
                else
                {
                    PluginService.Log.Information("No update: already on latest release v{Version}.", latestAsset.Version);
                    cyberdeckWindow.PendingUpdateVersion = null;
                }
            }
            catch (Exception ex)
            {
                PluginService.Log.Debug(ex, "Manual mode update check failed.");
            }
            finally
            {
                updateCheckRunning = false;
            }
        }, lifetime.Token);
    }

    private void CheckForUpdates()
    {
        if (updateCheckRunning)
            return;

        updateCheckRunning = true;
        PluginService.Log.Information("Check for updates requested.");
        PluginService.Chat.Print("Checking for The Grid mod updates...", "TheGrid");
        Task.Run(async () =>
        {
            try
            {
                var mapping = Config.GetPrimaryMapping();
                var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, lifetime.Token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(mapping.LastAppliedVersion))
                {
                    PluginService.Log.Information("New update found: v{Version} (nothing applied yet).", latestAsset.Version);
                    cyberdeckWindow.PendingUpdateVersion = latestAsset.Version;
                    PluginService.Chat.Print($"Latest release v{latestAsset.Version} is available (not applied yet). Press Update to install.", "TheGrid");
                }
                else if (!string.Equals(latestAsset.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    PluginService.Log.Information("New update found: v{Version} (installed v{Installed}).", latestAsset.Version, mapping.LastAppliedVersion);
                    cyberdeckWindow.PendingUpdateVersion = latestAsset.Version;
                    PluginService.Chat.Print($"Update available: v{latestAsset.Version} (installed v{mapping.LastAppliedVersion}). Press Update to install.", "TheGrid");
                }
                else
                {
                    PluginService.Log.Information("No update: already on latest release v{Version}.", latestAsset.Version);
                    cyberdeckWindow.PendingUpdateVersion = null;
                    PluginService.Chat.Print($"You're on the latest release: v{latestAsset.Version}.", "TheGrid");
                }
            }
            catch (Exception ex)
            {
                PluginService.Log.Warning(ex, "Check for updates failed.");
                PluginService.Chat.PrintError($"Check for updates failed: {ex.Message}", "TheGrid");
            }
            finally
            {
                updateCheckRunning = false;
            }
        }, lifetime.Token);
    }

    private void QueueStartupAction()
    {
        // Poll until Penumbra is ready before firing the initial reconcile or update check.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            PluginService.Framework.RunOnTick(
                TryStartupAction,
                delay: TimeSpan.FromSeconds(attempt * 2),
                cancellationToken: lifetime.Token);
        }
    }

    private void TryStartupAction()
    {
        if (startupActionDone)
            return;

        if (!IsPenumbraAvailable())
            return;

        startupActionDone = true;
        TrySubscribePenumbraEvents();
        if (Config.FullAuto)
            QueueReconcile();
        else
            QueueUpdateCheck();
    }

    private void QueueReconcile(bool forceDownload = false)
    {
        if (forceDownload)
            reconcileForceDownload = true;

        reconcileQueued = true;
    }

    private void QueueVenueUpdateCheck()
    {
        zoneTickCts?.Cancel();
        zoneTickCts?.Dispose();
        zoneTickCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

        var cts = zoneTickCts;
        // The object table populates a few seconds after a territory change, so poll a few times.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            PluginService.Framework.RunOnTick(
                TryVenueUpdateCheck,
                delay: TimeSpan.FromSeconds(attempt * 2),
                cancellationToken: cts.Token);
        }
    }

    private void TryVenueUpdateCheck()
    {
        if (venueUpdateCheckDoneThisZone)
            return;

        var mapping = Config.GetPrimaryMapping();
        if (!IsTargetNpcPresent(mapping.NpcName))
            return;

        venueUpdateCheckDoneThisZone = true;

        PluginService.Log.Information(
            "Mannequin '{Npc}' found in territory {Territory}; running update check ({Mode}).",
            mapping.NpcName,
            PluginService.ClientState.TerritoryType,
            Config.FullAuto ? "automatic mode" : "manual mode");

        if (Config.FullAuto)
        {
            QueueReconcile();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(mapping.LastAppliedVersion))
                _ = AssignAllAsync(lifetime.Token);
            QueueUpdateCheck();
        }
    }

    private static bool IsTargetNpcPresent(string npcName)
    {
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            if (IsTargetNpc(PluginService.Objects[i], npcName))
                return true;
        }

        return false;
    }

    private void QueueVenueAutoOpenCheck()
    {
        if (!Config.AutoOpenOnVenueAddress)
            return;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            PluginService.Framework.RunOnTick(() =>
            {
                TryAutoOpenForVenueObject();
            }, delay: TimeSpan.FromSeconds(attempt * 2), cancellationToken: lifetime.Token);
        }
    }

    private void TryAutoOpenForVenueObject()
    {
        var territory = PluginService.ClientState.TerritoryType;
        if (lastAutoOpenedTerritory == territory)
            return;

        var mapping = Config.GetPrimaryMapping();
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            if (!IsTargetNpc(PluginService.Objects[i], mapping.NpcName))
                continue;

            lastAutoOpenedTerritory = territory;
            OpenMainUi();
            return;
        }
    }

    private static (Dictionary<string, ISharedImmediateTexture> Textures, string Source) LoadTextures()
    {
        var loaded = new Dictionary<string, ISharedImmediateTexture>(StringComparer.OrdinalIgnoreCase);
        var source = "No image directory found.";

        foreach (var directory in GetTextureSearchDirectories())
        {
            LoadTextureIfExists(loaded, Path.Combine(directory, "grid.png"));

            var imageDirectory = Path.Combine(directory, "img");
            if (!Directory.Exists(imageDirectory))
                continue;

            foreach (var path in Directory.EnumerateFiles(imageDirectory, "*.png"))
                LoadTextureIfExists(loaded, path);

            source = imageDirectory;
            if (loaded.ContainsKey("map.png") && loaded.ContainsKey("address.png"))
                break;
        }

        PluginService.Log.Information("Loaded {Count} The Grid image asset(s) from {Source}.", loaded.Count, source);
        return (loaded, source);
    }

    private static IEnumerable<string> GetTextureSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetTextureSearchDirectoriesCore())
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string?> GetTextureSearchDirectoriesCore()
    {
        yield return PluginService.PluginInterface.AssemblyLocation.DirectoryName;
        yield return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        yield return AppContext.BaseDirectory;

        var current = PluginService.PluginInterface.AssemblyLocation.Directory;
        for (var i = 0; i < 4 && current is not null; i++, current = current.Parent)
            yield return current.FullName;
    }

    private static void LoadTextureIfExists(Dictionary<string, ISharedImmediateTexture> loaded, string path)
    {
        if (File.Exists(path))
            loaded[Path.GetFileName(path)] = PluginService.TextureProvider.GetFromFile(path);
    }

    private static string EscapeChatCommandArgument(string value)
        => value.Replace("\"", string.Empty, StringComparison.Ordinal);

    private async Task ReconcileAsync()
    {
        try
        {
            if (!IsPenumbraAvailable())
            {
                SetAllStatus("Penumbra IPC is not available. Install and enable Penumbra, then run /thegrid update.");
                return;
            }

            TrySubscribePenumbraEvents();

            var forceDownload = reconcileForceDownload;
            reconcileForceDownload = false;

            var canAssign = await ReconcileMappingAsync(Config.GetPrimaryMapping(), lifetime.Token, forceDownload).ConfigureAwait(false);

            Config.Save();
            if (canAssign)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token).ConfigureAwait(false);
                await AssignAllAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PluginService.Log.Error(ex, "TheGrid reconciliation failed.");
            PluginService.Chat.PrintError($"Update failed: {ex.Message}", "TheGrid");
        }
        finally
        {
            reconcileRunning = false;
            cyberdeckWindow.PendingUpdateVersion = null;
        }
    }

    private async Task<bool> ReconcileMappingAsync(ModMapping mapping, CancellationToken cancellationToken, bool forceDownload)
    {
        var cacheDirectory = Path.Combine(PluginService.PluginInterface.ConfigDirectory.FullName, "cache");
        PluginService.Log.Information("Checking for updates; matching asset pattern '{Pattern}'.", mapping.AssetPattern);
        var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, cancellationToken).ConfigureAwait(false);

        if (!forceDownload)
        {
            var installedMods = penumbra.GetModList();
            var installedModDirectory = FindInstalledModDirectory(mapping, installedMods);
            var alreadyKnownLatest = string.Equals(latestAsset.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase);
            var missingVersionRecord = string.IsNullOrWhiteSpace(mapping.LastAppliedVersion);
            if (installedModDirectory is not null && (alreadyKnownLatest || missingVersionRecord))
            {
                mapping.ModDirectory = installedModDirectory;
                mapping.ModName = installedMods[installedModDirectory];
                return ReconcileAlreadyImportedMapping(mapping, latestAsset.Version, installedModDirectory);
            }
        }

        if (!forceDownload && string.Equals(latestAsset.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return ReconcileAlreadyAppliedMapping(mapping, latestAsset.Version);
        }

        PluginService.Log.Information(
            "New update found: v{Version} (installed v{Installed}); {Mode} - downloading '{Asset}'.",
            latestAsset.Version,
            string.IsNullOrWhiteSpace(mapping.LastAppliedVersion) ? "none" : mapping.LastAppliedVersion,
            Config.FullAuto ? "automatic mode" : "manual mode",
            latestAsset.Name);
        var download = await github.DownloadReleaseAssetAsync(mapping, latestAsset, cacheDirectory, cancellationToken).ConfigureAwait(false);

        var previousModDirectory = NormalizeManagedModDirectory(mapping.ModDirectory);
        mapping.ModDirectory = previousModDirectory;
        var previousModName = mapping.ModName;
        var modsBeforeInstall = penumbra.GetModList();
        DeleteExistingManagedModBeforeInstall(previousModDirectory, previousModName, modsBeforeInstall);
        modsBeforeInstall = penumbra.GetModList();

        PrepareForModAdded();
        var installCode = penumbra.InstallMod(download.Path);
        if (!IsSuccess(installCode))
            throw new InvalidOperationException($"Penumbra rejected package '{Path.GetFileName(download.Path)}' with code {installCode}.");

        var addedDirectory = await WaitForModAddedAsync(cancellationToken).ConfigureAwait(false);
        var modsAfterInstall = penumbra.GetModList();

        var modDirectory = TryResolveModDirectory(mapping, modsAfterInstall, modsBeforeInstall, addedDirectory);
        var collection = FindCollection(mapping.CollectionName);
        if (modDirectory is not null)
        {
            mapping.ModDirectory = modDirectory;
            OrganizeModInPenumbra(mapping, modDirectory);

            if (collection is not null)
                EnableImportedMod(mapping, collection.Value, modDirectory);
        }
        else
        {
            PluginService.Log.Warning("Penumbra accepted package {Package}, but the imported mod was not visible in the IPC mod list immediately after import.", Path.GetFileName(download.Path));
        }

        mapping.LastAppliedVersion = download.Version;
        mapping.LastStatus = BuildReconcileStatus(mapping, download.Version, modDirectory, collection is not null);
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
        return collection is not null;
    }

    private bool ReconcileAlreadyImportedMapping(ModMapping mapping, string version, string modDirectory)
    {
        var collection = FindCollection(mapping.CollectionName);
        OrganizeModInPenumbra(mapping, modDirectory);

        if (collection is not null)
            EnableImportedMod(mapping, collection.Value, modDirectory);

        mapping.LastAppliedVersion = version;
        mapping.LastStatus = BuildReconcileStatus(mapping, version, modDirectory, collection is not null, alreadyApplied: true);
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
        return collection is not null;
    }

    private bool ReconcileAlreadyAppliedMapping(ModMapping mapping, string version)
    {
        var mods = penumbra.GetModList();
        var modDirectory = FindInstalledModDirectory(mapping, mods);
        var collection = FindCollection(mapping.CollectionName);

        if (modDirectory is not null)
        {
            mapping.ModDirectory = modDirectory;
            OrganizeModInPenumbra(mapping, modDirectory);

            if (collection is not null)
                EnableImportedMod(mapping, collection.Value, modDirectory);
        }

        mapping.LastStatus = BuildReconcileStatus(mapping, version, modDirectory, collection is not null, alreadyApplied: true);
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
        return collection is not null;
    }

    private void EnableImportedMod(ModMapping mapping, (Guid Id, string Name) collection, string modDirectory)
    {
        var enableCode = penumbra.TrySetMod(collection.Id, modDirectory, mapping.ModName, true);
        if (!IsSuccess(enableCode))
            PluginService.Log.Warning("Could not enable mod {ModDirectory} in {Collection}. Penumbra code {Code}.", modDirectory, mapping.CollectionName, enableCode);

        var priorityCode = penumbra.TrySetModPriority(collection.Id, modDirectory, mapping.ModName, mapping.Priority);
        if (!IsSuccess(priorityCode))
            PluginService.Log.Warning("Could not set mod priority for {ModDirectory}. Penumbra code {Code}.", modDirectory, priorityCode);
    }

    private void OrganizeModInPenumbra(ModMapping mapping, string modDirectory)
    {
        if (string.IsNullOrWhiteSpace(mapping.PenumbraFolderPath))
            return;

        var folder = mapping.PenumbraFolderPath.Trim().Trim('/', '\\');
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            var targetPath = $"{folder}/{mapping.ModName}";
            var pathCode = penumbra.SetModPath(modDirectory, mapping.ModName, targetPath);
            if (!IsSuccess(pathCode))
                PluginService.Log.Warning("Could not move mod {ModDirectory} to Penumbra path {Path}. Penumbra code {Code}.", modDirectory, targetPath, pathCode);
        }
        catch (Exception ex)
        {
            PluginService.Log.Warning(ex, "Could not move mod {ModDirectory} into the configured Penumbra folder.", modDirectory);
        }
    }

    private static string BuildReconcileStatus(ModMapping mapping, string version, string? modDirectory, bool collectionFound, bool alreadyApplied = false)
    {
        if (modDirectory is null)
            return alreadyApplied
                ? $"Latest release {version} already applied, but the imported mod is not visible in Penumbra IPC."
                : $"Imported latest release {version}; Penumbra did not expose the mod in IPC immediately.";

        var prefix = alreadyApplied
            ? $"Latest release {version} already imported"
            : $"Imported latest release {version}";
        var folderText = string.IsNullOrWhiteSpace(mapping.PenumbraFolderPath)
            ? "in Penumbra"
            : $"under {mapping.PenumbraFolderPath}";

        return collectionFound
            ? $"{prefix} {folderText}; enabled in collection '{mapping.CollectionName}'."
            : $"{prefix} {folderText}. The mod can be imported without the collection, but assignment requires a persistent Penumbra collection named '{mapping.CollectionName}'.";
    }

    private void PrepareForModAdded()
    {
        lock (modAddedLock)
        {
            pendingModAdded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task<string?> WaitForModAddedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<string>? waiter;
        lock (modAddedLock)
        {
            waiter = pendingModAdded;
        }

        if (waiter is null)
            return null;

        try
        {
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
            return completed == waiter.Task
                ? await waiter.Task.ConfigureAwait(false)
                : null;
        }
        finally
        {
            lock (modAddedLock)
            {
                if (ReferenceEquals(pendingModAdded, waiter))
                    pendingModAdded = null;
            }
        }
    }

    private void OnPenumbraModAdded(string modDirectory)
    {
        lock (modAddedLock)
        {
            pendingModAdded?.TrySetResult(modDirectory);
        }
    }

    private void DeleteExistingManagedModBeforeInstall(string previousModDirectory, string previousModName, Dictionary<string, string> installedMods)
    {
        if (string.IsNullOrWhiteSpace(previousModDirectory))
            return;

        if (!installedMods.ContainsKey(previousModDirectory))
        {
            var duplicateDirectories = installedMods.Keys
                .Where(IsManagedDuplicateDirectory)
                .ToList();

            foreach (var duplicateDirectory in duplicateDirectories)
                DeleteManagedMod(duplicateDirectory, previousModName);

            return;
        }

        DeleteManagedMod(previousModDirectory, previousModName);
    }

    private void DeleteManagedMod(string modDirectory, string modName)
    {
        var deleteCode = penumbra.DeleteMod(modDirectory, modName);
        if (IsSuccess(deleteCode))
        {
            PluginService.Log.Information("Deleted old managed Penumbra mod {ModDirectory} before update.", modDirectory);
            return;
        }

        PluginService.Log.Warning("Could not delete old managed Penumbra mod {ModDirectory} before update. Penumbra code {Code}.", modDirectory, deleteCode);
    }

    private static string NormalizeManagedModDirectory(string modDirectory)
        => IsManagedDuplicateDirectory(modDirectory) ? "n_root_the_grid" : modDirectory;

    private static bool IsManagedDuplicateDirectory(string modDirectory)
        => modDirectory.StartsWith("n_root_the_grid (", StringComparison.OrdinalIgnoreCase);

    private async Task AssignAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PluginService.Framework.RunOnFrameworkThread(() =>
            {
                AssignMapping(Config.GetPrimaryMapping());

                Config.Save();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PluginService.Log.Error(ex, "TheGrid assignment failed.");
            PluginService.Chat.PrintError($"Assignment failed: {ex.Message}", "TheGrid");
        }
    }

    private void AssignMapping(ModMapping mapping)
    {
        cyberdeckWindow.InstallStatusItems.Clear();
        cyberdeckWindow.InstallStatusTimestamp = Environment.TickCount64;

        if (!IsPenumbraAvailable())
        {
            mapping.LastStatus = "Penumbra IPC is not available.";
            cyberdeckWindow.InstallStatusItems.Add((false, "Penumbra not available"));
            return;
        }

        var collection = FindCollection(mapping.CollectionName);
        if (collection is null)
        {
            mapping.LastStatus = $"Collection '{mapping.CollectionName}' does not exist.";
            cyberdeckWindow.InstallStatusItems.Add((false, $"Collection '{mapping.CollectionName}' not found"));
            PluginService.Chat.PrintError($"Collection '{mapping.CollectionName}' does not exist. Create a persistent Penumbra collection named '{mapping.CollectionName}', then press Install.", "TheGrid");
            return;
        }

        var modDirectory = FindInstalledModDirectory(mapping, penumbra.GetModList());
        if (modDirectory is not null)
        {
            mapping.ModDirectory = modDirectory;
            OrganizeModInPenumbra(mapping, modDirectory);
            EnableImportedMod(mapping, collection.Value, modDirectory);
            cyberdeckWindow.InstallStatusItems.Add((true, $"Mod enabled in '{mapping.CollectionName}'"));
        }
        else
        {
            cyberdeckWindow.InstallStatusItems.Add((false, "Mod not found in Penumbra"));
        }

        var assigned = 0;
        var assignedObjectAddresses = new List<nint>();
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            var gameObject = PluginService.Objects[i];
            if (gameObject is null || !IsTargetNpc(gameObject, mapping.NpcName))
                continue;

            var (errorCode, _) = penumbra.SetCollectionForObject(i, collection.Value.Id);
            if (IsSuccess(errorCode))
            {
                assigned++;
                if (gameObject.Address != IntPtr.Zero)
                    assignedObjectAddresses.Add(gameObject.Address);
            }
            else
            {
                PluginService.Log.Warning("Could not assign collection {Collection} to {Npc} at object index {Index}: {Code}", mapping.CollectionName, mapping.NpcName, i, errorCode);
            }
        }


        if (assigned > 0)
        {
            foreach (var address in assignedObjectAddresses)
            {
                try
                {
                    penumbra.RedrawObject(address);
                }
                catch (Exception ex)
                {
                    PluginService.Log.Debug(ex, "Could not redraw assigned NPC {Npc}.", mapping.NpcName);
                }
            }

            cyberdeckWindow.InstallStatusItems.Add((true, $"Assigned to {assigned} '{mapping.NpcName}' object(s)"));
        }
        else
        {
            cyberdeckWindow.InstallStatusItems.Add((null, $"NPC '{mapping.NpcName}' not in range"));
        }

        mapping.LastStatus = string.Join(" ", cyberdeckWindow.InstallStatusItems.Select(s => s.Label + "."));
    }

    private (Guid Id, string Name)? FindCollection(string collectionName)
        => penumbra.GetCollectionsByIdentifier(collectionName)
            .FirstOrDefault(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase)) is var collection && collection.Id != Guid.Empty
                ? collection
                : null;

    internal static string? FindInstalledModDirectory(ModMapping mapping, Dictionary<string, string> mods)
    {
        if (mods.ContainsKey(mapping.ModDirectory))
            return mapping.ModDirectory;

        var duplicateDirectory = mods.Keys.FirstOrDefault(IsManagedDuplicateDirectory);
        if (!string.IsNullOrEmpty(duplicateDirectory))
            return duplicateDirectory;

        var byName = mods.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, mapping.ModName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Value, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Key, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            kvp.Value.Contains(mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Contains(mapping.Name, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrEmpty(byName.Key) ? null : byName.Key;
    }

    private static string? TryResolveModDirectory(ModMapping mapping, Dictionary<string, string> mods, Dictionary<string, string> modsBeforeInstall, string? addedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(addedDirectory) && mods.ContainsKey(addedDirectory))
            return addedDirectory;

        if (mods.ContainsKey(mapping.ModDirectory))
            return mapping.ModDirectory;

        var addedMod = mods.FirstOrDefault(kvp => !modsBeforeInstall.ContainsKey(kvp.Key));
        if (!string.IsNullOrEmpty(addedMod.Key))
        {
            mapping.ModName = addedMod.Value;
            return addedMod.Key;
        }

        var byName = mods.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, mapping.ModName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Value, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Key, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            kvp.Value.Contains(mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Contains(mapping.Name, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(byName.Key))
            return byName.Key;

        return null;
    }

    private static bool IsTargetNpc(IGameObject? gameObject, string npcName)
    {
        if (gameObject is null)
            return false;

        if (gameObject.ObjectKind is ObjectKind.Pc)
            return false;

        return string.Equals(gameObject.Name.TextValue, npcName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPenumbraAvailable()
    {
        var now = Environment.TickCount64;
        if (cachedPenumbraAvailable is null || now - lastPenumbraAvailableCheckTick > 5000)
        {
            cachedPenumbraAvailable = penumbra.IsAvailable();
            lastPenumbraAvailableCheckTick = now;
        }

        return cachedPenumbraAvailable.Value;
    }

    private static bool IsSuccess(int penumbraCode)
        => penumbraCode is 0 or 1;

    private void SetAllStatus(string status)
    {
        Config.GetPrimaryMapping().LastStatus = status;

        Config.Save();
        PluginService.Chat.PrintError(status, "TheGrid");
    }
}
