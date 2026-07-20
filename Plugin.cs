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
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly UpdateUiStateStore updateUiState = new();
    private readonly PenumbraIpc penumbra;
    private readonly CyberdeckWindow cyberdeckWindow;
    private readonly object modAddedLock = new();
    private readonly object reconcileQueueLock = new();
    private bool reconcileQueued;
    private bool reconcileForceDownload;
    private bool reconcileRunning;
    private int updateCheckPending;
    private int assignmentPending;
    private bool venueUpdateCheckDoneThisZone;
    private bool startupActionDone;
    private uint lastAutoOpenedTerritory;
    private bool modAddedSubscribed;
    private CancellationTokenSource? zoneTickCts;
    private TaskCompletionSource<string>? pendingModAdded;
    private bool? cachedPenumbraAvailable;
    private long lastPenumbraAvailableCheckTick;
    private volatile bool disposed;

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
        updateUiState.Initialize(primaryMapping.LastAppliedVersion);

        penumbra = new PenumbraIpc(pluginInterface);
        var (textures, textureLoadSource) = LoadTextures();

        cyberdeckWindow = new CyberdeckWindow(
            Config,
            penumbra,
            textures,
            textureLoadSource,
            () => QueueReconcile(),
            () => QueueReconcile(forceDownload: true),
            () => _ = AssignAllAsync(lifetime.Token),
            () => RunUpdateCheck(silent: false),
            OnAutoOpenSettingChanged,
            IsPenumbraAvailable,
            () => UpdateStatus);

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

    /// <summary>Returns the latest immutable updater snapshot and is safe to sample from the UI thread.</summary>
    public UpdateUiSnapshot UpdateStatus => updateUiState.Snapshot;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
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
        => cyberdeckWindow.OpenSettings();

    private void DrawUi()
        => cyberdeckWindow.Draw();

    private void OnLogin()
    {
        startupActionDone = false;
        lastAutoOpenedTerritory = 0;
        cyberdeckWindow.InstallStatusItems.Clear();
        cyberdeckWindow.InstallStatusTimestamp = 0;
        QueueStartupAction();
        QueueVenueUpdateCheck();
        QueueVenueAutoOpenCheck();
    }

    private void OnTerritoryChanged(uint _)
    {
        venueUpdateCheckDoneThisZone = false;
        lastAutoOpenedTerritory = 0;
        cyberdeckWindow.InstallStatusItems.Clear();
        cyberdeckWindow.InstallStatusTimestamp = 0;
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
        if (disposed)
            return;

        var startReconcile = false;
        var forceDownload = false;
        lock (reconcileQueueLock)
        {
            if (reconcileQueued && !reconcileRunning)
            {
                reconcileQueued = false;
                reconcileRunning = true;
                forceDownload = reconcileForceDownload;
                reconcileForceDownload = false;
                startReconcile = true;
            }
        }

        if (startReconcile)
            _ = Task.Run(() => ReconcileAsync(forceDownload));
    }

    private void RunUpdateCheck(bool silent)
    {
        if (disposed)
            return;

        if (Interlocked.CompareExchange(ref updateCheckPending, 1, 0) != 0)
            return;

        updateUiState.Queue(
            UpdateOperationKind.UpdateCheck,
            "CHECK QUEUED",
            "Waiting for the updater channel.");
        if (!silent)
        {
            PluginService.Log.Information("Check for updates requested.");
            PluginService.Chat.Print("Checking for The Grid mod updates...", "TheGrid");
        }

        _ = Task.Run(async () =>
        {
            var enteredGate = false;
            try
            {
                await operationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                enteredGate = true;
                updateUiState.Begin(
                    UpdateOperationKind.UpdateCheck,
                    UpdateOperationPhase.Checking,
                    "CHECKING RELEASES",
                    "Querying the latest published GitHub release.");

                var mapping = Config.GetPrimaryMapping();
                var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, lifetime.Token).ConfigureAwait(false);
                var hasVersion = !string.IsNullOrWhiteSpace(mapping.LastAppliedVersion);
                var installedModDirectory = FindInstalledModDirectory(mapping, penumbra.GetModList());
                var hasManagedMod = installedModDirectory is not null;
                var upToDate = hasVersion && hasManagedMod && VersionsEqual(latestAsset.Version, mapping.LastAppliedVersion);

                if (upToDate)
                {
                    PluginService.Log.Information("No update: already on latest v{Version}.", latestAsset.Version);
                    updateUiState.SetRelease(
                        UpdateReleaseAvailability.UpToDate,
                        latestAsset.Version,
                        hasManagedMod ? mapping.LastAppliedVersion : null);
                    updateUiState.Complete(
                        "UP TO DATE",
                        $"Installed release v{latestAsset.Version} is current.");
                    if (!silent)
                        PluginService.Chat.Print($"You're on the latest release: v{latestAsset.Version}.", "TheGrid");
                }
                else
                {
                    PluginService.Log.Information(
                        "Update available: v{Latest} (stored: {Stored}, managed mod: {ManagedMod}).",
                        latestAsset.Version,
                        hasVersion ? mapping.LastAppliedVersion : "none",
                        installedModDirectory ?? "not found");
                    updateUiState.SetRelease(
                        UpdateReleaseAvailability.UpdateAvailable,
                        latestAsset.Version,
                        hasManagedMod ? mapping.LastAppliedVersion : null);
                    var message = hasVersion && hasManagedMod
                        ? $"Update available: v{latestAsset.Version} (installed v{mapping.LastAppliedVersion}). Press Update to install."
                        : hasVersion
                            ? $"The Grid mod v{latestAsset.Version} is available; the managed Penumbra mod is missing. Press Update to restore it."
                            : $"The Grid mod v{latestAsset.Version} is available (not installed). Press Update to install.";
                    updateUiState.Complete("UPDATE AVAILABLE", message);
                    PluginService.Chat.Print(message, "TheGrid");
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception) when (disposed)
            {
            }
            catch (Exception ex)
            {
                updateUiState.Fail(
                    "CHECK FAILED",
                    ex,
                    "The release check failed. The last known availability has been retained.");
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
                if (enteredGate)
                    operationGate.Release();
                Volatile.Write(ref updateCheckPending, 0);
            }
        });
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
        if (disposed)
            return;

        var publishQueued = false;
        lock (reconcileQueueLock)
        {
            if (forceDownload)
                reconcileForceDownload = true;

            if (!reconcileQueued)
            {
                reconcileQueued = true;
                publishQueued = !reconcileRunning;
            }
        }

        if (publishQueued)
        {
            updateUiState.Queue(
                forceDownload ? UpdateOperationKind.Repair : UpdateOperationKind.Reconcile,
                forceDownload ? "REINSTALL QUEUED" : "UPDATE QUEUED",
                "Waiting for the updater channel.");
        }
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

    private async Task ReconcileAsync(bool forceDownload)
    {
        var enteredGate = false;
        try
        {
            await operationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            enteredGate = true;
            updateUiState.Begin(
                forceDownload ? UpdateOperationKind.Repair : UpdateOperationKind.Reconcile,
                UpdateOperationPhase.Checking,
                "CHECKING RELEASES",
                "Validating Penumbra and querying the latest release.");

            if (!IsPenumbraAvailable())
            {
                const string unavailableMessage = "Penumbra IPC is not available. Install and enable Penumbra, then run /grid update.";
                await PluginService.Framework.RunOnFrameworkThread(() => SetAllStatus(unavailableMessage));
                updateUiState.Fail("PENUMBRA OFFLINE", unavailableMessage);
                return;
            }

            TrySubscribePenumbraEvents();

            var mapping = Config.GetPrimaryMapping();
            var canAssign = await ReconcileMappingAsync(mapping, lifetime.Token, forceDownload).ConfigureAwait(false);

            await PluginService.Framework.RunOnFrameworkThread(Config.Save);
            updateUiState.SetRelease(
                UpdateReleaseAvailability.UpToDate,
                mapping.LastAppliedVersion,
                mapping.LastAppliedVersion);

            if (!canAssign)
            {
                updateUiState.SetOperation(UpdateOperationKind.Assignment);
                updateUiState.NeedsAttention(
                    "SYNC COMPLETE // ACTION REQUIRED",
                    mapping.LastStatus);
                return;
            }

            updateUiState.Transition(
                UpdateOperationPhase.Assigning,
                "ASSIGNING COLLECTION",
                $"Waiting for Penumbra to settle before assigning '{mapping.CollectionName}'.");
            await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token).ConfigureAwait(false);
            AssignmentResult assignmentResult;
            try
            {
                assignmentResult = await AssignAllCoreAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportAssignmentFailure(ex);
                updateUiState.SetOperation(UpdateOperationKind.Assignment);
                updateUiState.Fail(
                    "ASSIGNMENT FAILED",
                    ex,
                    "The release is installed, but collection assignment did not complete.");
                return;
            }

            if (TryPublishAssignmentIssue(assignmentResult, afterSync: true))
                return;

            updateUiState.Complete(
                forceDownload ? "REINSTALL COMPLETE" : "SYNC COMPLETE",
                assignmentResult.Detail);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (disposed)
        {
        }
        catch (Exception ex)
        {
            updateUiState.Fail(
                "UPDATE FAILED",
                ex,
                updateUiState.Snapshot.ReleaseAvailability == UpdateReleaseAvailability.UpdateAvailable
                    ? "Installation failed. The available release remains ready to retry."
                    : null);
            PluginService.Log.Error(ex, "TheGrid reconciliation failed.");
            PluginService.Chat.PrintError($"Update failed: {ex.Message}", "TheGrid");
        }
        finally
        {
            if (enteredGate)
                operationGate.Release();
            lock (reconcileQueueLock)
                reconcileRunning = false;
        }
    }

    private async Task<bool> ReconcileMappingAsync(ModMapping mapping, CancellationToken cancellationToken, bool forceDownload)
    {
        var cacheDirectory = Path.Combine(PluginService.PluginInterface.ConfigDirectory.FullName, "cache");
        PluginService.Log.Information("Checking for updates; matching asset pattern '{Pattern}'.", mapping.AssetPattern);
        var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, cancellationToken).ConfigureAwait(false);
        var installedMods = penumbra.GetModList();
        var installedModDirectory = FindInstalledModDirectory(mapping, installedMods);
        var hasInstalledVersion = !string.IsNullOrWhiteSpace(mapping.LastAppliedVersion);
        var releaseIsApplied = hasInstalledVersion &&
                               installedModDirectory is not null &&
                               VersionsEqual(latestAsset.Version, mapping.LastAppliedVersion);
        updateUiState.SetRelease(
            releaseIsApplied
                ? UpdateReleaseAvailability.UpToDate
                : UpdateReleaseAvailability.UpdateAvailable,
            latestAsset.Version,
            installedModDirectory is not null ? mapping.LastAppliedVersion : null);

        if (!forceDownload)
        {
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
                updateUiState.Transition(
                    UpdateOperationPhase.Configuring,
                    "CONFIGURING PENUMBRA",
                    $"Release v{latestAsset.Version} is already imported; validating its collection settings.");
                mapping.ModDirectory = installedModDirectory;
                mapping.ModName = installedMods[installedModDirectory];
                return ReconcileAlreadyImportedMapping(mapping, latestAsset.Version, installedModDirectory);
            }

            if (installedModDirectory is not null && missingVersionRecord)
                PluginService.Log.Information("Stored version is missing; installed Penumbra mod {ModDirectory} exists but its release version is unknown, refreshing from GitHub.", installedModDirectory);
        }

        PluginService.Log.Information(
            "{Reason}: v{Version} (stored v{Installed}); {Mode} - downloading '{Asset}'.",
            forceDownload ? "Forced reinstall requested" : "New update found",
            NormalizeVersionForComparison(latestAsset.Version),
            string.IsNullOrWhiteSpace(mapping.LastAppliedVersion) ? "none" : NormalizeVersionForComparison(mapping.LastAppliedVersion),
            Config.FullAuto ? "automatic mode" : "manual mode",
            latestAsset.Name);
        updateUiState.Transition(
            UpdateOperationPhase.Downloading,
            "PREPARING DOWNLOAD",
            $"{latestAsset.Name} // release v{latestAsset.Version}");
        var download = await github.DownloadReleaseAssetAsync(
            mapping,
            latestAsset,
            cacheDirectory,
            progress => updateUiState.ReportDownloadProgress(
                progress.BytesDownloaded,
                progress.TotalBytes,
                $"{latestAsset.Name} // release v{latestAsset.Version}"),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        updateUiState.Transition(
            UpdateOperationPhase.Importing,
            "IMPORTING PACKAGE",
            $"Sending {Path.GetFileName(download.Path)} to Penumbra.");

        var destructiveInstallStarted = false;
        try
        {
            var previousModDirectory = NormalizeManagedModDirectory(mapping.ModDirectory);
            mapping.ModDirectory = previousModDirectory;
            var previousModName = mapping.ModName;
            var modsBeforeInstall = penumbra.GetModList();
            cancellationToken.ThrowIfCancellationRequested();
            destructiveInstallStarted = true;
            DeleteExistingManagedModBeforeInstall(previousModDirectory, previousModName, modsBeforeInstall);
            modsBeforeInstall = penumbra.GetModList();

            cancellationToken.ThrowIfCancellationRequested();
            var modAddedWaiter = PrepareForModAdded();
            string? addedDirectory;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installCode = penumbra.InstallMod(download.Path);
                if (!IsSuccess(installCode))
                    throw new InvalidOperationException($"Penumbra rejected package '{Path.GetFileName(download.Path)}' with code {installCode}.");

                updateUiState.Transition(
                    UpdateOperationPhase.WaitingForPenumbra,
                    "WAITING FOR PENUMBRA",
                    "The package was accepted; waiting for the imported mod to appear in IPC.");
                addedDirectory = await WaitForModAddedAsync(modAddedWaiter, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ClearPendingModAdded(modAddedWaiter);
            }

            cancellationToken.ThrowIfCancellationRequested();
            updateUiState.Transition(
                UpdateOperationPhase.Configuring,
                "CONFIGURING PENUMBRA",
                "Resolving the imported mod and applying folder, collection, and priority settings.");
            var modsAfterInstall = penumbra.GetModList();

            var modDirectory = TryResolveModDirectory(mapping, modsAfterInstall, modsBeforeInstall, addedDirectory);
            var collection = FindCollection(mapping.CollectionName);
            if (modDirectory is null)
                throw new InvalidOperationException($"Penumbra accepted '{Path.GetFileName(download.Path)}', but the imported mod did not appear in IPC within 30 seconds.");

            mapping.ModDirectory = modDirectory;
            var folderConfigured = OrganizeModInPenumbra(mapping, modDirectory);
            var collectionConfigured = collection is null || EnableImportedMod(mapping, collection.Value, modDirectory);
            CleanupCacheDirectory(cacheDirectory, download.Path);

            mapping.LastAppliedVersion = NormalizeVersionForComparison(download.Version);
            mapping.LastStatus = BuildReconcileStatus(mapping, download.Version, modDirectory, collection, folderConfigured);
            if (!folderConfigured)
                mapping.LastStatus += $" Penumbra did not confirm placement under '{mapping.PenumbraFolderPath}'.";
            if (!collectionConfigured)
                mapping.LastStatus += " Penumbra did not confirm the collection enable/priority settings.";
            PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
            return collection is not null && collectionConfigured && folderConfigured;
        }
        catch
        {
            if (destructiveInstallStarted)
                CorrectReleaseHealthAfterInstallFailure(mapping, latestAsset.Version);
            throw;
        }
    }

    private bool ReconcileAlreadyImportedMapping(ModMapping mapping, string version, string modDirectory)
    {
        var collection = FindCollection(mapping.CollectionName);
        var folderConfigured = OrganizeModInPenumbra(mapping, modDirectory);

        var collectionConfigured = collection is null || EnableImportedMod(mapping, collection.Value, modDirectory);

        mapping.LastAppliedVersion = NormalizeVersionForComparison(version);
        mapping.LastStatus = BuildReconcileStatus(mapping, version, modDirectory, collection, folderConfigured, alreadyApplied: true);
        if (!folderConfigured)
            mapping.LastStatus += $" Penumbra did not confirm placement under '{mapping.PenumbraFolderPath}'.";
        if (!collectionConfigured)
            mapping.LastStatus += " Penumbra did not confirm the collection enable/priority settings.";
        if (!Config.FullAuto)
            PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
        return collection is not null && collectionConfigured && folderConfigured;
    }

    private bool EnableImportedMod(ModMapping mapping, (Guid Id, string Name) collection, string modDirectory)
    {
        var succeeded = true;
        var enableCode = penumbra.TrySetMod(collection.Id, modDirectory, mapping.ModName, true);
        if (!IsSuccess(enableCode))
        {
            succeeded = false;
            PluginService.Log.Warning("Could not enable mod {ModDirectory} in {Collection}. Penumbra code {Code}.", modDirectory, collection.Name, enableCode);
        }

        var priorityCode = penumbra.TrySetModPriority(collection.Id, modDirectory, mapping.ModName, mapping.Priority);
        if (!IsSuccess(priorityCode))
        {
            succeeded = false;
            PluginService.Log.Warning("Could not set mod priority for {ModDirectory}. Penumbra code {Code}.", modDirectory, priorityCode);
        }

        return succeeded;
    }

    private bool OrganizeModInPenumbra(ModMapping mapping, string modDirectory)
    {
        if (string.IsNullOrWhiteSpace(mapping.PenumbraFolderPath))
            return true;

        var folder = mapping.PenumbraFolderPath.Trim().Trim('/', '\\');
        if (string.IsNullOrWhiteSpace(folder))
            return true;

        try
        {
            var targetPath = $"{folder}/{mapping.ModName}";
            var pathCode = penumbra.SetModPath(modDirectory, mapping.ModName, targetPath);
            if (!IsSuccess(pathCode))
            {
                PluginService.Log.Warning("Could not move mod {ModDirectory} to Penumbra path {Path}. Penumbra code {Code}.", modDirectory, targetPath, pathCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            PluginService.Log.Warning(ex, "Could not move mod {ModDirectory} into the configured Penumbra folder.", modDirectory);
            return false;
        }
    }

    private static string BuildReconcileStatus(
        ModMapping mapping,
        string version,
        string? modDirectory,
        (Guid Id, string Name)? collection,
        bool folderConfigured,
        bool alreadyApplied = false)
    {
        if (modDirectory is null)
            return alreadyApplied
                ? $"Latest release {version} already applied, but the imported mod is not visible in Penumbra IPC."
                : $"Imported latest release {version}; Penumbra did not expose the mod in IPC immediately.";

        var prefix = alreadyApplied
            ? $"Latest release {version} already imported"
            : $"Imported latest release {version}";
        var requestedFolder = mapping.PenumbraFolderPath.Trim().Trim('/', '\\');
        var folderText = string.IsNullOrWhiteSpace(requestedFolder)
            ? "in Penumbra"
            : folderConfigured
                ? $"under {requestedFolder}"
                : "in Penumbra";

        return collection is not null
            ? $"{prefix} {folderText}; enabled in collection '{collection.Value.Name}'."
            : $"{prefix} {folderText}. The mod can be imported without the collection, but assignment requires a persistent Penumbra collection matching '{mapping.CollectionName}'.";
    }

    private TaskCompletionSource<string> PrepareForModAdded()
    {
        lock (modAddedLock)
        {
            var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingModAdded = waiter;
            return waiter;
        }
    }

    private async Task<string?> WaitForModAddedAsync(
        TaskCompletionSource<string> waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != waiter.Task)
                return null;

            var result = await waiter.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            ClearPendingModAdded(waiter);
        }
    }

    private void ClearPendingModAdded(TaskCompletionSource<string> waiter)
    {
        lock (modAddedLock)
        {
            if (ReferenceEquals(pendingModAdded, waiter))
                pendingModAdded = null;
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

    private void CorrectReleaseHealthAfterInstallFailure(ModMapping mapping, string latestVersion)
    {
        try
        {
            var installedModDirectory = FindInstalledModDirectory(mapping, penumbra.GetModList());
            if (installedModDirectory is null)
            {
                updateUiState.SetRelease(
                    UpdateReleaseAvailability.UpdateAvailable,
                    latestVersion,
                    installedVersion: null);
                PluginService.Log.Warning(
                    "The managed Penumbra mod is no longer present after the failed install; updater health was corrected to not installed.");
                return;
            }

            var storedVersion = string.IsNullOrWhiteSpace(mapping.LastAppliedVersion)
                ? null
                : mapping.LastAppliedVersion;
            updateUiState.SetRelease(
                storedVersion is not null && VersionsEqual(latestVersion, storedVersion)
                    ? UpdateReleaseAvailability.UpToDate
                    : UpdateReleaseAvailability.UpdateAvailable,
                latestVersion,
                storedVersion);
        }
        catch (Exception ex)
        {
            PluginService.Log.Warning(ex, "Could not re-query Penumbra after the failed destructive install.");
        }
    }

    private static string NormalizeManagedModDirectory(string modDirectory)
        => IsManagedDuplicateDirectory(modDirectory) ? "n_root_the_grid" : modDirectory;

    private static bool IsManagedDuplicateDirectory(string modDirectory)
        => modDirectory.StartsWith("n_root_the_grid (", StringComparison.OrdinalIgnoreCase);

    private async Task AssignAllAsync(CancellationToken cancellationToken)
    {
        if (disposed)
            return;

        if (Interlocked.CompareExchange(ref assignmentPending, 1, 0) != 0)
            return;

        updateUiState.Queue(
            UpdateOperationKind.Assignment,
            "ASSIGNMENT QUEUED",
            "Waiting for the updater channel.");

        var enteredGate = false;
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            var mapping = Config.GetPrimaryMapping();
            updateUiState.Begin(
                UpdateOperationKind.Assignment,
                UpdateOperationPhase.Assigning,
                "ASSIGNING COLLECTION",
                $"Applying '{mapping.CollectionName}' to nearby '{mapping.NpcName}' objects.");
            var assignmentResult = await AssignAllCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!TryPublishAssignmentIssue(assignmentResult, afterSync: false))
                updateUiState.Complete("ASSIGNMENT COMPLETE", assignmentResult.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (disposed)
        {
        }
        catch (Exception ex)
        {
            ReportAssignmentFailure(ex);
            updateUiState.Fail("ASSIGNMENT FAILED", ex);
        }
        finally
        {
            if (enteredGate)
                operationGate.Release();
            Volatile.Write(ref assignmentPending, 0);
        }
    }

    private async Task<AssignmentResult> AssignAllCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssignmentResult? assignmentResult = null;
        await PluginService.Framework.RunOnFrameworkThread(() =>
        {
            assignmentResult = AssignMapping(Config.GetPrimaryMapping());
            Config.Save();
        });
        cancellationToken.ThrowIfCancellationRequested();
        return assignmentResult
               ?? throw new InvalidOperationException("The assignment did not return a result from the framework thread.");
    }

    private bool TryPublishAssignmentIssue(AssignmentResult result, bool afterSync)
    {
        if (afterSync && (result.HasFailures || result.NeedsAttention || result.HasPending))
            updateUiState.SetOperation(UpdateOperationKind.Assignment);

        if (result.HasFailures)
        {
            updateUiState.Fail(
                afterSync ? "SYNC COMPLETE // ASSIGNMENT FAILED" : "ASSIGNMENT FAILED",
                result.Detail,
                afterSync
                    ? $"The release is installed, but assignment needs repair. {result.Detail}"
                    : result.Detail);
            return true;
        }

        if (result.NeedsAttention)
        {
            updateUiState.NeedsAttention(
                afterSync ? "SYNC COMPLETE // ACTION REQUIRED" : "ASSIGNMENT NEEDS ATTENTION",
                result.Detail);
            return true;
        }

        if (result.HasPending)
        {
            if (afterSync && Config.FullAuto)
            {
                updateUiState.Complete(
                    "SYNC COMPLETE // ASSIGNMENT DEFERRED",
                    $"{result.Detail} Automatic mode will try again when the venue is detected.");
            }
            else
            {
                updateUiState.NeedsAttention(
                    afterSync ? "SYNC COMPLETE // ASSIGNMENT PENDING" : "ASSIGNMENT PENDING",
                    result.Detail);
            }
            return true;
        }

        return false;
    }

    private static void ReportAssignmentFailure(Exception ex)
    {
        PluginService.Log.Error(ex, "TheGrid assignment failed.");
        PluginService.Chat.PrintError($"Assignment failed: {ex.Message}", "TheGrid");
    }

    private AssignmentResult AssignMapping(ModMapping mapping)
    {
        var statusItems = new List<(bool? Ok, string Label)>();
        var needsAttention = false;
        cyberdeckWindow.InstallStatusTimestamp = Environment.TickCount64;

        if (!IsPenumbraAvailable())
        {
            mapping.LastStatus = "Penumbra IPC is not available.";
            statusItems.Add((false, "Penumbra not available"));
            return PublishAssignmentResult(statusItems, mapping);
        }

        var collection = FindCollection(mapping.CollectionName);
        if (collection is null)
        {
            mapping.LastStatus = $"Collection matching '{mapping.CollectionName}' does not exist.";
            statusItems.Add((false, $"Collection matching '{mapping.CollectionName}' not found"));
            PluginService.Chat.PrintError($"No Penumbra collection matching '{mapping.CollectionName}' exists. Names like TheGrid, The Grid, and 'the grid' are accepted.", "TheGrid");
            return PublishAssignmentResult(statusItems, mapping);
        }

        var modDirectory = FindInstalledModDirectory(mapping, penumbra.GetModList());
        if (modDirectory is not null)
        {
            mapping.ModDirectory = modDirectory;
            var organized = OrganizeModInPenumbra(mapping, modDirectory);
            if (!organized)
            {
                needsAttention = true;
                statusItems.Add((null, $"Could not place mod under '{mapping.PenumbraFolderPath}'"));
            }
            var configured = EnableImportedMod(mapping, collection.Value, modDirectory);
            statusItems.Add((
                configured,
                configured
                    ? $"Mod enabled in '{collection.Value.Name}'"
                    : $"Could not confirm mod enable/priority in '{collection.Value.Name}'"));
        }
        else
        {
            statusItems.Add((false, "Mod not found in Penumbra"));
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

            statusItems.Add((true, $"Assigned to {assigned} '{mapping.NpcName}' object(s)"));
        }

        if (alreadyAssigned > 0)
            statusItems.Add((true, $"Already individually assigned to {alreadyAssigned} '{mapping.NpcName}' object(s); no redraw needed"));

        if (failedAssignments > 0)
            statusItems.Add((false, $"Could not assign {failedAssignments} '{mapping.NpcName}' object(s)"));

        if (targetCount == 0)
        {
            if (mannequinFallbackCandidates > 1)
                statusItems.Add((false, $"Found {mannequinFallbackCandidates} mannequins but could not identify '{mapping.NpcName}'"));
            else
                statusItems.Add((null, $"Mannequin '{mapping.NpcName}' not in range"));
        }

        return PublishAssignmentResult(statusItems, mapping, needsAttention);
    }

    private AssignmentResult PublishAssignmentResult(
        List<(bool? Ok, string Label)> statusItems,
        ModMapping mapping,
        bool needsAttention = false)
    {
        mapping.LastStatus = string.Join(" ", statusItems.Select(item => item.Label + "."));
        cyberdeckWindow.InstallStatusItems.Clear();
        cyberdeckWindow.InstallStatusItems.AddRange(statusItems);
        return new AssignmentResult(
            statusItems.Any(item => item.Ok == false),
            statusItems.Any(item => item.Ok is null),
            needsAttention,
            mapping.LastStatus);
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

    private readonly record struct AssignmentResult(bool HasFailures, bool HasPending, bool NeedsAttention, string Detail);
}
