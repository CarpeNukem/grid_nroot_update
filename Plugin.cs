using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using NativeHousingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager;

namespace GridNrootUpdate;

public sealed class Plugin : IDalamudPlugin
{
    private const string PrimaryCommandName = "/grid";
    private static readonly string[] CommandNames = [PrimaryCommandName, "/thegrid", "/cyberdeck"];
    private static readonly string[] DataCenterNames =
    [
        "aether",
        "chaos",
        "crystal",
        "dynamis",
        "elemental",
        "gaia",
        "light",
        "mana",
        "materia",
        "meteor",
        "primal",
    ];
    private static readonly (string Canonical, string[] Aliases)[] HousingDistrictAliases =
    [
        ("Mist", ["mist", "the mist"]),
        ("The Lavender Beds", ["lavender beds", "the lavender beds", "lavender"]),
        ("The Goblet", ["goblet", "the goblet"]),
        ("Shirogane", ["shirogane"]),
        ("Empyreum", ["empyreum"]),
    ];

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
        var loadedConfig = pluginInterface.GetPluginConfig() as PluginConfig;
        Config = loadedConfig ?? new PluginConfig();
        var primaryMapping = Config.GetPrimaryMapping();
        RecoverMissingVersionFromConfigFile(pluginInterface, primaryMapping);
        PluginService.Log.Information(
            "Loaded config ({Source}); stored mod version v{Version}.",
            loadedConfig is null ? "new config" : "saved config",
            string.IsNullOrWhiteSpace(primaryMapping.LastAppliedVersion) ? "none" : NormalizeVersionForComparison(primaryMapping.LastAppliedVersion));
        Config.Save();

        penumbra = new PenumbraIpc(pluginInterface);
        var (textures, textureLoadSource) = LoadTextures();

        configWindow = new ConfigWindow(
            Config,
            () => QueueReconcile(),
            () => _ = AssignAllAsync(lifetime.Token),
            OnAutoOpenSettingChanged);

        cyberdeckWindow = new CyberdeckWindow(
            Config,
            penumbra,
            textures,
            textureLoadSource,
            () => QueueReconcile(),
            () => QueueReconcile(forceDownload: true),
            () => _ = AssignAllAsync(lifetime.Token),
            () => RunUpdateCheck(silent: false),
            () => configWindow.IsOpen = true,
            OnAutoOpenSettingChanged,
            IsPenumbraAvailable);

        foreach (var commandName in CommandNames)
        {
            PluginService.Commands.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open The Grid Cyberdeck. Aliases: /thegrid, /cyberdeck. Subcommands: update, config.",
                ShowInHelp = commandName == PrimaryCommandName,
            });
        }

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
        foreach (var commandName in CommandNames)
            PluginService.Commands.RemoveHandler(commandName);
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
                PluginService.Chat.PrintError($"Unknown command '{args}'. Use /thegrid, /grid, or /cyberdeck with optional update/config.", "TheGrid");
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
            PluginService.Log.Warning(ex, "Could not subscribe to Penumbra ModAdded event; mod install detection will rely on IPC list polling.");
            PluginService.Chat.PrintError("TheGrid: Penumbra event subscription failed — mod installs may not be detected reliably. Try reloading the plugin.", "TheGrid");
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
        lastAutoOpenedTerritory = 0;
        QueueStartupAction();
        QueueVenueUpdateCheck();
        QueueVenueAutoOpenCheck();
    }

    private void OnTerritoryChanged(uint _)
    {
        venueUpdateCheckDoneThisZone = false;
        lastAutoOpenedTerritory = 0;
        QueueVenueUpdateCheck();
        QueueVenueAutoOpenCheck();
    }

    private void OnAutoOpenSettingChanged(bool enabled)
    {
        if (!enabled)
            return;

        lastAutoOpenedTerritory = 0;
        QueueVenueAutoOpenCheck(immediate: true);
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

    private void RunUpdateCheck(bool silent)
    {
        if (updateCheckRunning)
            return;

        updateCheckRunning = true;
        if (!silent)
        {
            PluginService.Log.Information("Check for updates requested.");
            PluginService.Chat.Print("Checking for The Grid mod updates...", "TheGrid");
        }

        Task.Run(async () =>
        {
            try
            {
                var mapping = Config.GetPrimaryMapping();
                var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, lifetime.Token).ConfigureAwait(false);
                var hasVersion = !string.IsNullOrWhiteSpace(mapping.LastAppliedVersion);
                var upToDate = hasVersion && VersionsEqual(latestAsset.Version, mapping.LastAppliedVersion);

                if (upToDate)
                {
                    PluginService.Log.Information("No update: already on latest v{Version}.", latestAsset.Version);
                    cyberdeckWindow.PendingUpdateVersion = null;
                    if (!silent)
                        PluginService.Chat.Print($"You're on the latest release: v{latestAsset.Version}.", "TheGrid");
                }
                else
                {
                    PluginService.Log.Information("Update available: v{Latest} (installed: {Installed}).", latestAsset.Version, hasVersion ? mapping.LastAppliedVersion : "none");
                    cyberdeckWindow.PendingUpdateVersion = latestAsset.Version;
                    var message = hasVersion
                        ? $"Update available: v{latestAsset.Version} (installed v{mapping.LastAppliedVersion}). Press Update to install."
                        : $"The Grid mod v{latestAsset.Version} is available (not installed). Press Update to install.";
                    PluginService.Chat.Print(message, "TheGrid");
                }
            }
            catch (Exception ex)
            {
                if (silent)
                    PluginService.Log.Debug(ex, "Passive update check failed.");
                else
                {
                    PluginService.Log.Warning(ex, "Check for updates failed.");
                    PluginService.Chat.PrintError($"Check for updates failed: {ex.Message}", "TheGrid");
                }
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
            RunUpdateCheck(silent: true);
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
        // The object table populates after a territory change, and housing mannequins can lag behind the zone load.
        for (var attempt = 1; attempt <= 12; attempt++)
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
        if (IsKnownWrongVenueAddress(out var mismatchReason))
            PluginService.Log.Debug("Venue address check reported a mismatch after finding mannequin '{Npc}'; continuing with mannequin assignment: {Reason}", mapping.NpcName, mismatchReason);

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
            var token = lifetime.Token;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                await AssignAllAsync(token).ConfigureAwait(false);
            }, token);
            RunUpdateCheck(silent: true);
        }
    }

    private static bool IsTargetNpcPresent(string npcName)
        => FindTargetNpcObjects(npcName, out _).Count > 0;

    private void QueueVenueAutoOpenCheck(bool immediate = false)
    {
        if (!Config.AutoOpenOnVenueAddress)
            return;

        if (immediate)
        {
            PluginService.Framework.RunOnTick(
                TryAutoOpenForVenueObject,
                delay: TimeSpan.Zero,
                cancellationToken: lifetime.Token);
        }

        for (var attempt = 1; attempt <= 12; attempt++)
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
        foreach (var _ in FindTargetNpcObjects(mapping.NpcName, out _))
        {
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
                SetAllStatus("Penumbra IPC is not available. Install and enable Penumbra, then run /grid update.");
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
            var alreadyKnownLatest = VersionsEqual(latestAsset.Version, mapping.LastAppliedVersion);
            var missingVersionRecord = string.IsNullOrWhiteSpace(mapping.LastAppliedVersion);
            PluginService.Log.Information(
                "Update decision: latest v{Latest}, stored v{Installed}, installed mod {InstalledModDirectory}, force download {ForceDownload}.",
                NormalizeVersionForComparison(latestAsset.Version),
                string.IsNullOrWhiteSpace(mapping.LastAppliedVersion) ? "none" : NormalizeVersionForComparison(mapping.LastAppliedVersion),
                installedModDirectory ?? "not found",
                forceDownload);
            if (installedModDirectory is not null && alreadyKnownLatest)
            {
                mapping.ModDirectory = installedModDirectory;
                mapping.ModName = installedMods[installedModDirectory];
                return ReconcileAlreadyImportedMapping(mapping, latestAsset.Version, installedModDirectory);
            }

            if (installedModDirectory is not null && missingVersionRecord)
                PluginService.Log.Information("Stored version is missing; installed Penumbra mod {ModDirectory} exists but its release version is unknown, refreshing from GitHub.", installedModDirectory);
        }

        if (!forceDownload && VersionsEqual(latestAsset.Version, mapping.LastAppliedVersion))
        {
            return ReconcileAlreadyAppliedMapping(mapping, latestAsset.Version);
        }

        PluginService.Log.Information(
            "{Reason}: v{Version} (stored v{Installed}); {Mode} - downloading '{Asset}'.",
            forceDownload ? "Forced reinstall requested" : "New update found",
            NormalizeVersionForComparison(latestAsset.Version),
            string.IsNullOrWhiteSpace(mapping.LastAppliedVersion) ? "none" : NormalizeVersionForComparison(mapping.LastAppliedVersion),
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

            CleanupCacheDirectory(cacheDirectory, download.Path);
        }
        else
        {
            PluginService.Log.Warning("Penumbra accepted package {Package}, but the imported mod was not visible in the IPC mod list immediately after import.", Path.GetFileName(download.Path));
        }

        mapping.LastAppliedVersion = NormalizeVersionForComparison(download.Version);
        mapping.LastStatus = BuildReconcileStatus(mapping, download.Version, modDirectory, collection);
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
        return collection is not null;
    }

    private bool ReconcileAlreadyImportedMapping(ModMapping mapping, string version, string modDirectory)
    {
        var collection = FindCollection(mapping.CollectionName);
        OrganizeModInPenumbra(mapping, modDirectory);

        if (collection is not null)
            EnableImportedMod(mapping, collection.Value, modDirectory);

        mapping.LastAppliedVersion = NormalizeVersionForComparison(version);
        mapping.LastStatus = BuildReconcileStatus(mapping, version, modDirectory, collection, alreadyApplied: true);
        if (!Config.FullAuto)
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

        mapping.LastAppliedVersion = NormalizeVersionForComparison(version);
        mapping.LastStatus = BuildReconcileStatus(mapping, version, modDirectory, collection, alreadyApplied: true);
        if (!Config.FullAuto)
            PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
        return collection is not null;
    }

    private void EnableImportedMod(ModMapping mapping, (Guid Id, string Name) collection, string modDirectory)
    {
        var enableCode = penumbra.TrySetMod(collection.Id, modDirectory, mapping.ModName, true);
        if (!IsSuccess(enableCode))
            PluginService.Log.Warning("Could not enable mod {ModDirectory} in {Collection}. Penumbra code {Code}.", modDirectory, collection.Name, enableCode);

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

    private static string BuildReconcileStatus(ModMapping mapping, string version, string? modDirectory, (Guid Id, string Name)? collection, bool alreadyApplied = false)
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

        return collection is not null
            ? $"{prefix} {folderText}; enabled in collection '{collection.Value.Name}'."
            : $"{prefix} {folderText}. The mod can be imported without the collection, but assignment requires a persistent Penumbra collection matching '{mapping.CollectionName}'.";
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
            mapping.LastStatus = $"Collection matching '{mapping.CollectionName}' does not exist.";
            cyberdeckWindow.InstallStatusItems.Add((false, $"Collection matching '{mapping.CollectionName}' not found"));
            PluginService.Chat.PrintError($"No Penumbra collection matching '{mapping.CollectionName}' exists. Names like TheGrid, The Grid, and 'the grid' are accepted.", "TheGrid");
            return;
        }

        var modDirectory = FindInstalledModDirectory(mapping, penumbra.GetModList());
        if (modDirectory is not null)
        {
            mapping.ModDirectory = modDirectory;
            OrganizeModInPenumbra(mapping, modDirectory);
            EnableImportedMod(mapping, collection.Value, modDirectory);
            cyberdeckWindow.InstallStatusItems.Add((true, $"Mod enabled in '{collection.Value.Name}'"));
        }
        else
        {
            cyberdeckWindow.InstallStatusItems.Add((false, "Mod not found in Penumbra"));
        }

        var targetCount = 0;
        var assigned = 0;
        var alreadyAssigned = 0;
        var failedAssignments = 0;
        var assignedObjectIndices = new List<int>();
        var targetObjects = FindTargetNpcObjects(mapping.NpcName, out var mannequinFallbackCandidates);
        if (targetObjects.Count > 0 && IsKnownWrongVenueAddress(out var mismatchReason))
            PluginService.Log.Debug("Venue address check reported a mismatch while assigning mannequin '{Npc}'; continuing with mannequin assignment: {Reason}", mapping.NpcName, mismatchReason);

        foreach (var targetObject in targetObjects)
        {
            var objectIndex = targetObject.ObjectIndex;
            targetCount++;
            var currentCollection = TryGetCollectionForObject(objectIndex);
            if (currentCollection.HasValue && currentCollection.Value.Individual && currentCollection.Value.Id == collection.Value.Id)
            {
                alreadyAssigned++;
                continue;
            }

            var (errorCode, _) = penumbra.SetCollectionForObject(objectIndex, collection.Value.Id);
            if (errorCode == PenumbraApiSuccess)
            {
                assigned++;
                assignedObjectIndices.Add(objectIndex);
            }
            else if (errorCode == PenumbraApiNothingChanged)
            {
                alreadyAssigned++;
            }
            else
            {
                failedAssignments++;
                PluginService.Log.Warning("Could not assign collection {Collection} to {Npc} at object index {Index}: {Code}", collection.Value.Name, mapping.NpcName, objectIndex, errorCode);
            }
        }


        if (assigned > 0)
        {
            foreach (var objectIndex in assignedObjectIndices)
            {
                try
                {
                    penumbra.RedrawObject(objectIndex);
                }
                catch (Exception ex)
                {
                    PluginService.Log.Warning(ex, "Could not redraw assigned NPC {Npc} at object index {Index}.", mapping.NpcName, objectIndex);
                }
            }

            cyberdeckWindow.InstallStatusItems.Add((true, $"Assigned to {assigned} '{mapping.NpcName}' object(s)"));
        }

        if (alreadyAssigned > 0)
            cyberdeckWindow.InstallStatusItems.Add((true, $"Already individually assigned to {alreadyAssigned} '{mapping.NpcName}' object(s); no redraw needed"));

        if (failedAssignments > 0)
            cyberdeckWindow.InstallStatusItems.Add((false, $"Could not assign {failedAssignments} '{mapping.NpcName}' object(s)"));

        if (targetCount == 0)
        {
            if (mannequinFallbackCandidates > 1)
                cyberdeckWindow.InstallStatusItems.Add((false, $"Found {mannequinFallbackCandidates} mannequins but could not identify '{mapping.NpcName}'"));
            else
                cyberdeckWindow.InstallStatusItems.Add((null, $"Mannequin '{mapping.NpcName}' not in range"));
        }

        mapping.LastStatus = string.Join(" ", cyberdeckWindow.InstallStatusItems.Select(s => s.Label + "."));
    }

    private (Guid Id, string Name)? FindCollection(string collectionName)
        => penumbra.FindCollectionByName(collectionName);

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

    private static IReadOnlyList<TargetObjectMatch> FindTargetNpcObjects(string npcName, out int mannequinFallbackCandidates)
    {
        mannequinFallbackCandidates = 0;
        var namedMatches = new List<TargetObjectMatch>();
        var mannequinMatches = new List<TargetObjectMatch>();

        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            var gameObject = PluginService.Objects[i];
            if (gameObject is null || !IsAssignableObject(gameObject))
                continue;

            var objectIndex = gameObject.ObjectIndex;
            if (IsTargetNpc(gameObject, npcName))
            {
                namedMatches.Add(new TargetObjectMatch(objectIndex, gameObject.Name.TextValue, gameObject.ObjectKind.ToString(), true));
                continue;
            }

            if (IsMannequinObject(gameObject))
                mannequinMatches.Add(new TargetObjectMatch(objectIndex, gameObject.Name.TextValue, gameObject.ObjectKind.ToString(), false));
        }

        if (namedMatches.Count > 0)
            return namedMatches;

        mannequinFallbackCandidates = mannequinMatches.Count;
        return mannequinMatches.Count == 1 ? mannequinMatches : [];
    }

    private static bool IsAssignableObject(IGameObject? gameObject)
    {
        if (gameObject is null)
            return false;

        if (PluginService.Objects.LocalPlayer?.GameObjectId == gameObject.GameObjectId)
            return false;

        return true;
    }

    private static bool IsTargetNpc(IGameObject gameObject, string npcName)
    {
        if (!IsAssignableObject(gameObject))
            return false;

        return NamesMatch(gameObject.Name.TextValue, npcName);
    }

    private static bool IsMannequinObject(IGameObject gameObject)
        => NamesMatch(gameObject.Name.TextValue, "Mannequin");

    private static bool NamesMatch(string objectName, string targetName)
    {
        var normalizedObject = NormalizeObjectName(objectName);
        var normalizedTarget = NormalizeObjectName(targetName);
        return normalizedObject.Length > 0 &&
               normalizedTarget.Length > 0 &&
               (string.Equals(normalizedObject, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedObject.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedTarget.Contains(normalizedObject, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeObjectName(string name)
        => Regex.Replace(name.Trim(), @"\s+", " ");

    private (Guid Id, string Name, bool Individual)? TryGetCollectionForObject(int objectIndex)
    {
        try
        {
            var result = penumbra.GetCollectionForObject(objectIndex);
            return result.Valid
                ? (result.Collection.Id, result.Collection.Name, result.Individual)
                : null;
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not read current Penumbra collection for object index {Index}.", objectIndex);
            return null;
        }
    }

    private bool IsKnownWrongVenueAddress(out string reason)
    {
        reason = string.Empty;
        if (!TryParseVenueAddress(Config.VenueAddress, out var expected))
            return false;

        var currentMaybe = TryGetCurrentHousingAddress();
        if (currentMaybe is null)
            return false;

        var current = currentMaybe.Value;
        var mismatches = new List<string>();
        if (!string.IsNullOrWhiteSpace(expected.WorldName)
            && !string.IsNullOrWhiteSpace(current.WorldName)
            && !string.Equals(expected.WorldName, current.WorldName, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"world {current.WorldName} != {expected.WorldName}");

        if (!string.IsNullOrWhiteSpace(expected.DistrictName)
            && !string.IsNullOrWhiteSpace(current.DistrictName)
            && !string.Equals(expected.DistrictName, current.DistrictName, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"district {current.DistrictName} != {expected.DistrictName}");

        if (expected.Ward is not null && current.Ward is not null && expected.Ward != current.Ward)
            mismatches.Add($"ward {current.Ward} != {expected.Ward}");

        if (expected.Plot is not null && current.Plot is not null && expected.Plot != current.Plot)
            mismatches.Add($"plot {current.Plot} != {expected.Plot}");

        if (mismatches.Count == 0)
            return false;

        reason = string.Join(", ", mismatches);
        return true;
    }

    private static bool TryParseVenueAddress(string address, out VenueAddressParts parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var district = TryGetHousingDistrict(address, out var districtIndex);
        var worldName = TryGetWorldName(address, districtIndex);
        var ward = TryGetAddressNumber(address, "W", "Ward");
        var plot = TryGetAddressNumber(address, "P", "Plot");

        if (district is null && worldName is null && ward is null && plot is null)
            return false;

        parts = new VenueAddressParts(worldName, district, ward, plot);
        return true;
    }

    private static string? TryGetHousingDistrict(string address, out int districtIndex)
    {
        foreach (var (canonical, aliases) in HousingDistrictAliases)
        {
            foreach (var alias in aliases.OrderByDescending(a => a.Length))
            {
                var match = Regex.Match(address, $@"\b{Regex.Escape(alias)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                    continue;

                districtIndex = match.Index;
                return canonical;
            }
        }

        districtIndex = -1;
        return null;
    }

    private static string? NormalizeHousingDistrictName(string? districtName)
    {
        if (string.IsNullOrWhiteSpace(districtName))
            return null;

        var trimmed = districtName.Trim();
        foreach (var (canonical, aliases) in HousingDistrictAliases)
        {
            if (string.Equals(trimmed, canonical, StringComparison.OrdinalIgnoreCase) ||
                aliases.Any(alias => string.Equals(trimmed, alias, StringComparison.OrdinalIgnoreCase)))
                return canonical;
        }

        return trimmed;
    }

    private static string? TryGetWorldName(string address, int districtIndex)
    {
        if (districtIndex <= 0)
            return null;

        var precedingTokens = address[..districtIndex]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (precedingTokens.Length == 0)
            return null;

        var candidate = precedingTokens[^1];
        return DataCenterNames.Contains(candidate, StringComparer.OrdinalIgnoreCase) ||
               string.Equals(candidate, "the", StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
    }

    private static int? TryGetAddressNumber(string address, string shortPrefix, string longPrefix)
    {
        var match = Regex.Match(address, $@"\b(?:{Regex.Escape(shortPrefix)}|{Regex.Escape(longPrefix)})\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static unsafe CurrentHousingAddress? TryGetCurrentHousingAddress()
    {
        try
        {
            var housing = NativeHousingManager.Instance();
            if (housing is null)
                return null;

            var houseId = housing->GetCurrentHouseId();
            if (houseId.Id == 0 && housing->IsInside())
                houseId = housing->GetCurrentIndoorHouseId();

            int? ward = houseId.Id != 0 ? houseId.WardIndex + 1 : null;
            int? plot = houseId.Id != 0 && !houseId.IsApartment ? houseId.PlotIndex + 1 : null;

            if (ward is null)
            {
                var currentWard = housing->GetCurrentWard();
                if (currentWard >= 0)
                    ward = currentWard + 1;
            }

            if (plot is null)
            {
                var currentPlot = housing->GetCurrentPlot();
                if (currentPlot >= 0)
                    plot = currentPlot + 1;
            }

            if (ward is null && plot is null)
                return null;

            var districtName = TryGetCurrentHousingDistrict();
            var worldName = PluginService.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText();
            return new CurrentHousingAddress(worldName, districtName, ward, plot);
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not read current housing address.");
            return null;
        }
    }

    private static string? TryGetCurrentHousingDistrict()
    {
        var territoryTypeId = (uint)NativeHousingManager.GetOriginalHouseTerritoryTypeId();
        if (territoryTypeId == 0)
            territoryTypeId = PluginService.ClientState.TerritoryType;

        if (territoryTypeId == 0)
            return null;

        var territory = PluginService.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territoryTypeId);
        var placeName = territory?.PlaceName.ValueNullable;
        if (placeName is null)
            return null;

        return NormalizeHousingDistrictName(placeName.Value.NameNoArticle.ExtractText()) ??
               NormalizeHousingDistrictName(placeName.Value.Name.ExtractText());
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

    private static void CleanupCacheDirectory(string cacheDirectory, string keepFilePath)
    {
        try
        {
            if (!Directory.Exists(cacheDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.pmp"))
            {
                if (!string.Equals(file, keepFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    PluginService.Log.Debug("Removing stale cached file: {File}", Path.GetFileName(file));
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Cache cleanup failed (non-fatal).");
        }
    }

    private static bool IsSuccess(int penumbraCode)
        => penumbraCode is 0 or 1;

    private const int PenumbraApiSuccess = 0;
    private const int PenumbraApiNothingChanged = 1;

    private static bool VersionsEqual(string? left, string? right)
        => string.Equals(NormalizeVersionForComparison(left), NormalizeVersionForComparison(right), StringComparison.OrdinalIgnoreCase);

    private static void RecoverMissingVersionFromConfigFile(IDalamudPluginInterface pluginInterface, ModMapping mapping)
    {
        if (!string.IsNullOrWhiteSpace(mapping.LastAppliedVersion))
            return;

        var storedVersion = TryReadStoredVersionFromConfigFile(pluginInterface);
        if (string.IsNullOrWhiteSpace(storedVersion))
            return;

        mapping.LastAppliedVersion = NormalizeVersionForComparison(storedVersion);
        PluginService.Log.Information(
            "Recovered stored mod version v{Version} from raw config file {ConfigFile}.",
            mapping.LastAppliedVersion,
            pluginInterface.ConfigFile.FullName);
    }

    private static string? TryReadStoredVersionFromConfigFile(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            var configFile = pluginInterface.ConfigFile;
            if (!File.Exists(configFile.FullName))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(configFile.FullName));
            if (TryReadFirstMappingVersion(document.RootElement, out var mappingVersion))
                return mappingVersion;

            return document.RootElement.TryGetProperty(nameof(ModMapping.LastAppliedVersion), out var topLevelVersion)
                ? topLevelVersion.GetString()
                : null;
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not read raw config file for stored mod version recovery.");
            return null;
        }
    }

    private static bool TryReadFirstMappingVersion(JsonElement root, out string? version)
    {
        version = null;
        if (!root.TryGetProperty(nameof(PluginConfig.Mappings), out var mappings) || mappings.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var mapping in mappings.EnumerateArray())
        {
            if (!mapping.TryGetProperty(nameof(ModMapping.LastAppliedVersion), out var versionElement))
                continue;

            version = versionElement.GetString();
            return !string.IsNullOrWhiteSpace(version);
        }

        return false;
    }

    private static string NormalizeVersionForComparison(string? version)
    {
        var normalized = version?.Trim() ?? string.Empty;
        if (normalized.StartsWith("mod-v", StringComparison.OrdinalIgnoreCase))
            return normalized[5..].Trim();
        if (normalized.StartsWith("mod-", StringComparison.OrdinalIgnoreCase))
            return normalized[4..].Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return normalized[1..].Trim();

        return normalized;
    }

    private void SetAllStatus(string status)
    {
        Config.GetPrimaryMapping().LastStatus = status;

        Config.Save();
        PluginService.Chat.PrintError(status, "TheGrid");
    }

    private readonly record struct VenueAddressParts(string? WorldName, string? DistrictName, int? Ward, int? Plot);

    private readonly record struct CurrentHousingAddress(string? WorldName, string? DistrictName, int? Ward, int? Plot);

    private readonly record struct TargetObjectMatch(int ObjectIndex, string Name, string ObjectKind, bool MatchedByName);
}
