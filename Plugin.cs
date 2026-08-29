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
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeHousingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager;

namespace GridNrootUpdate;

public sealed class Plugin : IDalamudPlugin
{
    private const string PrimaryCommandName = "/grid";
    private const string TemporaryCollectionIdentity = "GridNrootUpdate";
    private const string TemporaryCollectionName = "The Grid Venue";
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
    private readonly NetworkStatsTracker networkStatsTracker;
    private readonly CatalogService catalogService;
    private readonly BroadcastAlert broadcastAlert;
    private readonly RemoteAssetCache remoteAssets;
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
    private bool penumbraStateSubscribed;
    private Guid? managedTemporaryCollectionId;
    private string? pendingReplacementModDirectory;

    /// <summary>
    /// What the venue mannequins were last redrawn for.
    ///
    /// A redraw is not about which collection is assigned — it is about the mod
    /// files having changed underneath one, which Penumbra will not re-render on
    /// its own. So it cannot be skipped just because the assignment already
    /// matched, or an update installed while someone stands in front of the
    /// mannequin would never show. Comparing the mod directory, the applied
    /// version and the collection catches every case that needs one, and lets a
    /// reconcile that changed nothing leave the mannequin alone.
    ///
    /// Session state on purpose: redrawing once after a restart is harmless.
    /// </summary>
    private string? lastRedrawSignature;
    private string? pendingReplacementModName;
    private CancellationTokenSource? zoneTickCts;
    private TaskCompletionSource<string>? pendingModAdded;
    private bool? cachedPenumbraAvailable;
    private long lastPenumbraAvailableCheckTick;
    private long nextNetworkStatsSampleAt;
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
        networkStatsTracker = new NetworkStatsTracker(Config);
        catalogService = new CatalogService(Config, pluginInterface.ConfigDirectory.FullName);
        remoteAssets = new RemoteAssetCache(pluginInterface.ConfigDirectory.FullName);

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
            () => UpdateStatus,
            () => networkStatsTracker.Snapshot,
            () => catalogService.Snapshot,
            catalogService.RequestRefresh,
            catalogService.RequestRefreshIfOlderThan,
            remoteAssets);

        remoteAssets.LoadExisting();
        broadcastAlert = new BroadcastAlert(Config, OpenMainUi);
        catalogService.Updated += broadcastAlert.OnCatalogUpdated;

        catalogService.Start();

        foreach (var commandName in CommandNames)
        {
            PluginService.Commands.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open The Grid Cyberdeck. Aliases: /thegrid, /cyberdeck. Subcommands: update, config, vault.",
                ShowInHelp = commandName == PrimaryCommandName,
            });
        }

        PluginService.ClientState.Login += OnLogin;
        PluginService.ClientState.Logout += OnLogout;
        PluginService.ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginService.Framework.Update += OnFrameworkUpdate;
        PluginService.Chat.ChatMessage += OnChatMessage;
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
        PluginService.ClientState.Logout -= OnLogout;
        networkStatsTracker.FinalizeSession(DateTimeOffset.UtcNow);
        PluginService.Chat.ChatMessage -= OnChatMessage;
        if (modAddedSubscribed)
            penumbra.UnsubscribeModAdded(OnPenumbraModAdded);
        if (penumbraStateSubscribed)
        {
            penumbra.UnsubscribeInitialized(OnPenumbraInitialized);
            penumbra.UnsubscribeDisposed(OnPenumbraDisposed);
        }
        ReleaseManagedTemporaryCollection();
        PluginService.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginService.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginService.PluginInterface.UiBuilder.Draw -= DrawUi;
        foreach (var commandName in CommandNames)
            PluginService.Commands.RemoveHandler(commandName);
        github.Dispose();
        catalogService.Updated -= broadcastAlert.OnCatalogUpdated;
        catalogService.Dispose();
        remoteAssets.Dispose();
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
            case "vault":
                cyberdeckWindow.OpenCipherVault();
                break;
#if DEBUG
            case "debug":
                HandleDebugCommand(split.Length > 1 ? split[1] : string.Empty);
                break;
#endif
            default:
                PluginService.Chat.PrintError($"Unknown command '{args}'. Use /thegrid, /grid, or /cyberdeck with optional update/config/vault.", "TheGrid");
                break;
        }
    }

#if DEBUG
    private void HandleDebugCommand(string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "blackice-clear":
                cyberdeckWindow.DebugClearBlackIce();
                PluginService.Chat.Print("DEBUG: Black ICE clear state injected.", "TheGrid");
                break;
            case "vault-s-clear":
                cyberdeckWindow.DebugClearCipherVault('S');
                PluginService.Chat.Print("DEBUG: Vault S-clear queued. Authenticate if the archive is sealed.", "TheGrid");
                break;
            case "vault-a-clear":
                cyberdeckWindow.DebugClearCipherVault('A');
                PluginService.Chat.Print("DEBUG: Vault A-clear queued. Authenticate if the archive is sealed.", "TheGrid");
                break;
            case "vault-b-clear":
                cyberdeckWindow.DebugClearCipherVault('B');
                PluginService.Chat.Print("DEBUG: Vault B-clear queued. Authenticate if the archive is sealed.", "TheGrid");
                break;
            case "vault-c-clear":
                cyberdeckWindow.DebugClearCipherVault('C');
                PluginService.Chat.Print("DEBUG: Vault C-clear queued. Authenticate if the archive is sealed.", "TheGrid");
                break;
            case "tarot":
                cyberdeckWindow.OpenTarotDebug();
                PluginService.Chat.Print("DEBUG: Tarot link window opened.", "TheGrid");
                break;
            case "tarot-invite":
                cyberdeckWindow.StartTarotCustomerLoopback();
                PluginService.Chat.Print("DEBUG: Local Tarot invitation injected. Use YES or NO in the Cyberdeck.", "TheGrid");
                break;
            case "tarot-host":
                cyberdeckWindow.StartTarotHostLoopback();
                PluginService.Chat.Print("DEBUG: Local Tarot host session started. No second player or tells required.", "TheGrid");
                break;
            case "tarot-next":
                PluginService.Chat.Print($"DEBUG: {cyberdeckWindow.AdvanceTarotCustomerLoopback()}", "TheGrid");
                break;
            case "tarot-reset":
                cyberdeckWindow.ResetTarotLoopback();
                PluginService.Chat.Print("DEBUG: Local Tarot session reset.", "TheGrid");
                break;
            default:
                PluginService.Chat.PrintError(
                    "Debug usage: /grid debug tarot-invite | tarot-next | tarot-host | tarot-reset | tarot | blackice-clear | vault-s-clear | vault-a-clear | vault-b-clear | vault-c-clear",
                    "TheGrid");
                break;
        }
    }
#endif

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (message.LogKind != Dalamud.Game.Text.XivChatType.TellIncoming)
            return;

        var text = message.Message.TextValue;

        // Deck traffic announces itself two ways: plain messages carry the venue
        // prefix, tarot carries its own packet marker. Both chime — the prefix
        // is not added to packets, so checking for it alone would miss them.
        var isVenueTraffic =
            text.Contains(TarotTellSender.MessagePrefix, StringComparison.Ordinal) ||
            text.Contains(TarotPacket.Marker, StringComparison.Ordinal);

        if (Config.MessageToneEnabled && isVenueTraffic)
            VenueSounds.PlayMessageTone();

        if (!text.Contains(TarotPacket.Marker, StringComparison.Ordinal))
            return;

        var sender = GetTarotTellSender(message.Sender);
        if (cyberdeckWindow.TryReceiveTarotTell(sender, text))
            PluginService.Log.Debug("Received GRID-TAROT packet from {Sender}.", sender);
    }

    private static string GetTarotTellSender(SeString sender)
    {
        try
        {
            var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
            if (player is not null)
            {
                var playerName = player.PlayerName.Trim();
                var world = player.World.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(world))
                    return $"{playerName}@{world.Trim()}";
                if (!string.IsNullOrWhiteSpace(playerName))
                    return playerName;
            }
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not extract the structured sender from a GRID-TAROT tell.");
        }

        var rendered = sender.TextValue.Trim();
        var nameStart = 0;
        while (nameStart < rendered.Length && !char.IsLetter(rendered[nameStart]))
            nameStart++;
        return nameStart < rendered.Length ? rendered[nameStart..].Trim() : rendered;
    }

    private void OpenMainUi()
        => cyberdeckWindow.IsOpen = true;

    private void TrySubscribePenumbraEvents()
    {
        if (!modAddedSubscribed)
        {
            try
            {
                penumbra.SubscribeModAdded(OnPenumbraModAdded);
                modAddedSubscribed = true;
            }
            catch (Exception ex)
            {
                PluginService.Log.Warning(ex, "Could not subscribe to Penumbra ModAdded event; mod install detection will rely on IPC list polling.");
            }
        }

        if (!penumbraStateSubscribed)
        {
            try
            {
                penumbra.SubscribeInitialized(OnPenumbraInitialized);
                try
                {
                    penumbra.SubscribeDisposed(OnPenumbraDisposed);
                }
                catch
                {
                    penumbra.UnsubscribeInitialized(OnPenumbraInitialized);
                    throw;
                }

                penumbraStateSubscribed = true;
            }
            catch (Exception ex)
            {
                PluginService.Log.Warning(ex, "Could not subscribe to Penumbra lifecycle events.");
            }
        }
    }

    private void OnPenumbraInitialized()
    {
        cachedPenumbraAvailable = true;
        lastPenumbraAvailableCheckTick = Environment.TickCount64;
        managedTemporaryCollectionId = null;
        startupActionDone = false;
        venueUpdateCheckDoneThisZone = false;
        QueueStartupAction();
        QueueVenueUpdateCheck();
    }

    private void OnPenumbraDisposed()
    {
        cachedPenumbraAvailable = false;
        lastPenumbraAvailableCheckTick = Environment.TickCount64;
        managedTemporaryCollectionId = null;
        startupActionDone = false;
        venueUpdateCheckDoneThisZone = false;
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

    private void OnLogout(int _, int __)
        => networkStatsTracker.FinalizeSession(DateTimeOffset.UtcNow);

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

        var nowTick = Environment.TickCount64;
        if (nowTick >= nextNetworkStatsSampleAt)
        {
            nextNetworkStatsSampleAt = nowTick + 1000;
            try
            {
                var presence = GetVenuePresence();
                var people = presence == VenuePresence.Confirmed
                    ? NetworkGuestScanner.Capture()
                    : [];
                if (presence == VenuePresence.Confirmed && NetworkGuestScanner.CaptureLocal() is { } localPlayer)
                    people.Insert(0, localPlayer);
                networkStatsTracker.Update(presence, people, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                PluginService.Log.Debug(ex, "Could not sample Grid venue network statistics.");
            }
        }
    }

    private VenuePresence GetVenuePresence()
    {
        if (!PluginService.ClientState.IsLoggedIn ||
            !TryParseVenueAddress(Config.VenueAddress, out var expected))
            return VenuePresence.Unknown;

        // Keep venue telemetry aligned with the existing arrival/update workflow.
        // Indoor housing data can be incomplete, while a recognized venue mannequin
        // in the object table is direct evidence that the player reached The Grid.
        if (IsTargetNpcPresent(Config.GetPrimaryMapping().NpcName))
            return VenuePresence.Confirmed;

        var currentMaybe = TryGetCurrentHousingAddress();
        if (currentMaybe is null)
        {
            var currentDistrict = TryGetCurrentHousingDistrict();
            if (!string.IsNullOrWhiteSpace(expected.DistrictName) &&
                !string.IsNullOrWhiteSpace(currentDistrict) &&
                !string.Equals(expected.DistrictName, currentDistrict, StringComparison.OrdinalIgnoreCase))
                return VenuePresence.Elsewhere;

            return VenuePresence.Unknown;
        }

        var current = currentMaybe.Value;
        if (!AddressFieldMatches(expected.WorldName, current.WorldName))
            return VenuePresence.Elsewhere;
        if (!AddressFieldMatches(expected.DistrictName, current.DistrictName))
            return VenuePresence.Elsewhere;
        if (expected.Ward is not null && current.Ward is not null && expected.Ward != current.Ward)
            return VenuePresence.Elsewhere;
        if (expected.Plot is not null && current.Plot is not null && expected.Plot != current.Plot)
            return VenuePresence.Elsewhere;

        var addressComplete = (string.IsNullOrWhiteSpace(expected.WorldName) || !string.IsNullOrWhiteSpace(current.WorldName)) &&
                              (string.IsNullOrWhiteSpace(expected.DistrictName) || !string.IsNullOrWhiteSpace(current.DistrictName)) &&
                              (expected.Ward is null || current.Ward is not null) &&
                              (expected.Plot is null || current.Plot is not null);
        return addressComplete
            ? VenuePresence.Confirmed
            : VenuePresence.Unknown;
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
            var queueAutomaticInstall = false;
            try
            {
                await operationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                enteredGate = true;
                updateUiState.Begin(
                    UpdateOperationKind.UpdateCheck,
                    UpdateOperationPhase.Checking,
                    "CHECKING FOR UPDATES",
                    "Checking the latest available venue mod.");

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
                    var installAutomatically = Config.FullAuto;
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
                        ? installAutomatically
                            ? $"Update available: v{latestAsset.Version} (installed v{mapping.LastAppliedVersion}). Automatic update queued."
                            : $"Update available: v{latestAsset.Version} (installed v{mapping.LastAppliedVersion}). Press Update to install."
                        : hasVersion
                            ? installAutomatically
                                ? $"The Grid mod v{latestAsset.Version} is available; the managed Penumbra mod is missing. Automatic restore queued."
                                : $"The Grid mod v{latestAsset.Version} is available; the managed Penumbra mod is missing. Press Update to restore it."
                            : installAutomatically
                                ? $"The Grid mod v{latestAsset.Version} is available (not installed). Automatic installation queued."
                                : $"The Grid mod v{latestAsset.Version} is available (not installed). Press Update to install.";
                    updateUiState.Complete("UPDATE AVAILABLE", message);
                    queueAutomaticInstall = installAutomatically;
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
                if (queueAutomaticInstall && !disposed && !lifetime.IsCancellationRequested)
                    QueueReconcile();
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
                forceDownload ? "REPAIR QUEUED" : "INSTALLATION QUEUED",
                "Waiting for the current installation task to finish.");
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
        if (!IsVenueMannequinPresent(mapping))
            return;

        venueUpdateCheckDoneThisZone = true;
        if (IsKnownWrongVenueAddress(out var mismatchReason))
        {
            PluginService.Log.Information(
                "Found '{Npc}' but the address does not match the venue ({Reason}); skipping the update check.",
                mapping.NpcName,
                mismatchReason);

            return;
        }

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
        => FindTargetNpcObjects(npcName, out _, out _).Count > 0;

    /// <summary>
    /// The venue mannequin, however it can be identified.
    ///
    /// A housing mannequin cannot be matched by name — see
    /// <see cref="IsConfirmedVenueAddress"/> — so at a confirmed venue address
    /// the single mannequin present is the venue's. Every caller has to agree on
    /// that: when only the assignment path knew it, arriving at the venue
    /// stopped triggering the update check and stopped opening the deck, because
    /// both were still asking a question that can never be answered yes.
    /// </summary>
    private IReadOnlyList<TargetObjectMatch> FindVenueMannequins(
        ModMapping mapping,
        out IReadOnlyList<string> mannequinDescriptions,
        out bool addressConfirmed)
    {
        var named = FindTargetNpcObjects(mapping.NpcName, out var mannequins, out mannequinDescriptions);
        addressConfirmed = IsConfirmedVenueAddress(out _);

        if (named.Count == 0 && mannequins.Count == 1 && addressConfirmed)
            return mannequins;

        return named;
    }

    /// <summary>Whether the venue mannequin is in reach, by any of those routes.</summary>
    private bool IsVenueMannequinPresent(ModMapping mapping)
        => FindVenueMannequins(mapping, out _, out _).Count > 0;

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
        foreach (var _ in FindVenueMannequins(mapping, out _, out _))
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

            foreach (var path in Directory.EnumerateFiles(imageDirectory, "*.png", SearchOption.AllDirectories))
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
                "CHECKING FOR UPDATES",
                "Checking Penumbra and the latest available venue mod.");

            if (!IsPenumbraAvailable())
            {
                const string unavailableMessage = "Penumbra is unavailable. Install or enable Penumbra, then try again.";
                await PluginService.Framework.RunOnFrameworkThread(() => SetAllStatus(unavailableMessage));
                updateUiState.Fail("PENUMBRA UNAVAILABLE", unavailableMessage);
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
                    "SETUP NEEDS ATTENTION",
                    mapping.LastStatus);
                return;
            }

            updateUiState.Transition(
                UpdateOperationPhase.Assigning,
                "FINISHING SETUP",
                "Waiting for Penumbra to finish installing the venue mod.");
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
                    "SETUP FAILED",
                    ex,
                    "The venue mod is installed, but Penumbra setup could not be completed.");
                return;
            }

            if (TryPublishAssignmentIssue(assignmentResult, afterSync: true))
                return;

            updateUiState.Complete(
                forceDownload ? "REPAIR COMPLETE" : "INSTALLATION COMPLETE",
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
                "INSTALLATION FAILED",
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
                    "FINISHING SETUP",
                    $"Venue mod v{latestAsset.Version} is already installed; completing its Penumbra setup.");
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
            "DOWNLOADING",
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
            "INSTALLING IN PENUMBRA",
            $"Installing {Path.GetFileName(download.Path)}.");

        try
        {
            var modsBeforeInstall = penumbra.GetModList();
            var previousModDirectory = FindInstalledModDirectory(mapping, modsBeforeInstall);
            var previousModName = previousModDirectory is not null &&
                                  modsBeforeInstall.TryGetValue(previousModDirectory, out var installedModName)
                ? installedModName
                : mapping.ModName;

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
                    "INSTALLING IN PENUMBRA",
                    "Penumbra accepted the download and is finishing the installation.");
                addedDirectory = await WaitForModAddedAsync(modAddedWaiter, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ClearPendingModAdded(modAddedWaiter);
            }

            cancellationToken.ThrowIfCancellationRequested();
            updateUiState.Transition(
                UpdateOperationPhase.Configuring,
                "FINISHING SETUP",
                "Completing the Penumbra setup.");
            var modsAfterInstall = penumbra.GetModList();

            var modDirectory = TryResolveModDirectory(mapping, modsAfterInstall, modsBeforeInstall, addedDirectory);
            var collection = FindCollection(mapping.CollectionName);
            if (modDirectory is null)
                throw new InvalidOperationException($"Penumbra accepted '{Path.GetFileName(download.Path)}', but the imported mod did not appear in IPC within 30 seconds.");

            mapping.ModDirectory = modDirectory;
            mapping.ModName = modsAfterInstall.TryGetValue(modDirectory, out var importedModName)
                ? importedModName
                : mapping.ModName;
            if (previousModDirectory is not null &&
                !string.Equals(previousModDirectory, modDirectory, StringComparison.OrdinalIgnoreCase))
            {
                pendingReplacementModDirectory = previousModDirectory;
                pendingReplacementModName = previousModName;
            }

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
            return collectionConfigured;
        }
        catch
        {
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
        return collectionConfigured;
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
                ? $"Venue mod v{version} is recorded as installed, but Penumbra cannot currently find it."
                : $"Venue mod v{version} was installed, but Penumbra has not finished loading it.";

        var prefix = alreadyApplied
            ? $"Venue mod v{version} is installed"
            : $"Installed venue mod v{version}";
        var requestedFolder = mapping.PenumbraFolderPath.Trim().Trim('/', '\\');
        var folderText = string.IsNullOrWhiteSpace(requestedFolder)
            ? "in Penumbra"
            : folderConfigured
                ? $"under {requestedFolder}"
                : "in Penumbra";

        return collection is not null
            ? $"{prefix} {folderText} and is enabled in '{collection.Value.Name}'."
            : $"{prefix} {folderText}. The remaining Penumbra setup will be completed automatically.";
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

    private bool DeleteManagedMod(string modDirectory, string modName)
    {
        var deleteCode = penumbra.DeleteMod(modDirectory, modName);
        if (IsSuccess(deleteCode))
        {
            PluginService.Log.Information("Removed replaced Penumbra mod {ModDirectory}.", modDirectory);
            return true;
        }

        PluginService.Log.Warning("Could not remove replaced Penumbra mod {ModDirectory}. Penumbra code {Code}.", modDirectory, deleteCode);
        return false;
    }

    private void FinalizePendingModReplacement(string activeModDirectory)
    {
        if (pendingReplacementModDirectory is not { } previousDirectory ||
            string.Equals(previousDirectory, activeModDirectory, StringComparison.OrdinalIgnoreCase))
        {
            pendingReplacementModDirectory = null;
            pendingReplacementModName = null;
            return;
        }

        var previousName = string.IsNullOrWhiteSpace(pendingReplacementModName)
            ? Config.GetPrimaryMapping().ModName
            : pendingReplacementModName;
        if (DeleteManagedMod(previousDirectory, previousName))
        {
            pendingReplacementModDirectory = null;
            pendingReplacementModName = null;
        }
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
            PluginService.Log.Warning(ex, "Could not re-query Penumbra after the failed installation.");
        }
    }

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
            "SETUP QUEUED",
            "Waiting for the current installation task to finish.");

        var enteredGate = false;
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            var mapping = Config.GetPrimaryMapping();
            updateUiState.Begin(
                UpdateOperationKind.Assignment,
                UpdateOperationPhase.Assigning,
                "FINISHING SETUP",
                "Applying the venue mod to nearby mannequins.");
            var assignmentResult = await AssignAllCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!TryPublishAssignmentIssue(assignmentResult, afterSync: false))
                updateUiState.Complete("SETUP COMPLETE", assignmentResult.Detail);
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
            updateUiState.Fail("SETUP FAILED", ex);
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
                afterSync ? "INSTALLATION NEEDS ATTENTION" : "SETUP FAILED",
                result.Detail,
                afterSync
                    ? $"The venue mod is installed, but setup needs attention. {result.Detail}"
                    : result.Detail);
            return true;
        }

        if (result.NeedsAttention)
        {
            updateUiState.NeedsAttention(
                afterSync ? "INSTALLATION NEEDS ATTENTION" : "SETUP NEEDS ATTENTION",
                result.Detail);
            return true;
        }

        if (result.HasPending)
        {
            updateUiState.Complete(
                afterSync ? "INSTALLATION COMPLETE" : "READY AT VENUE",
                "The venue mod is installed and will activate automatically when you enter The Grid.");
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
            mapping.LastStatus = "Penumbra is unavailable.";
            statusItems.Add((false, "Penumbra not available"));
            return PublishAssignmentResult(statusItems, mapping);
        }

        var modDirectory = FindInstalledModDirectory(mapping, penumbra.GetModList());
        if (modDirectory is null)
        {
            statusItems.Add((false, "Mod not found in Penumbra"));
            return PublishAssignmentResult(statusItems, mapping);
        }

        mapping.ModDirectory = modDirectory;
        _ = OrganizeModInPenumbra(mapping, modDirectory);
        statusItems.Add((true, "Venue mod installed in Penumbra"));

        var assignmentCollection = PrepareAssignmentCollection(mapping, modDirectory);
        if (assignmentCollection is null)
        {
            statusItems.Add((false, "Penumbra setup could not be completed automatically"));
            PluginService.Chat.PrintError(
                "Automatic Penumbra setup failed. Create a permanent collection named Grid or TheGrid, then try again.",
                "TheGrid");
            return PublishAssignmentResult(statusItems, mapping);
        }

        statusItems.Add((true, assignmentCollection.Value.IsTemporary
            ? "Automatic Penumbra setup ready"
            : $"Using Penumbra collection '{assignmentCollection.Value.Name}'"));
        FinalizePendingModReplacement(modDirectory);
        _ = OrganizeModInPenumbra(mapping, modDirectory);

        var redrawSignature =
            $"{modDirectory}|{mapping.LastAppliedVersion}|{assignmentCollection.Value.Id}";
        var contentChanged = !string.Equals(redrawSignature, lastRedrawSignature, StringComparison.Ordinal);

        var targetCount = 0;
        var assigned = 0;
        var alreadyAssigned = 0;
        var failedAssignments = 0;
        var redrawObjectIndices = new HashSet<int>();
        var targetObjects = FindVenueMannequins(mapping, out var mannequinDescriptions, out var addressConfirmed);
        var nearbyMannequins = mannequinDescriptions;
        IsConfirmedVenueAddress(out var addressDescription);
        if (targetObjects.Count > 0 && IsKnownWrongVenueAddress(out var mismatchReason))
        {
            // Refusing outright rather than logging and carrying on. Assignment
            // through a permanent collection is persistent, so getting this
            // wrong edits somebody else's Penumbra rather than flickering.
            PluginService.Log.Information(
                "Refusing to assign '{Npc}': the address does not match the venue ({Reason}).",
                mapping.NpcName,
                mismatchReason);
            statusItems.Add((false, $"Not at the venue address ({mismatchReason}); nothing was assigned"));

            return PublishAssignmentResult(statusItems, mapping, needsAttention);
        }

        foreach (var targetObject in targetObjects)
        {
            var objectIndex = targetObject.ObjectIndex;
            targetCount++;
            var currentCollection = TryGetCollectionForObject(objectIndex);
            if (currentCollection.HasValue && currentCollection.Value.Id == assignmentCollection.Value.Id)
            {
                alreadyAssigned++;
                // Already pointing at the right collection: only worth a redraw
                // if what that collection contains has changed since the last one.
                if (contentChanged)
                    redrawObjectIndices.Add(objectIndex);
                continue;
            }

            var errorCode = assignmentCollection.Value.IsTemporary
                ? penumbra.AssignTemporaryCollection(assignmentCollection.Value.Id, objectIndex)
                : penumbra.SetCollectionForObject(objectIndex, assignmentCollection.Value.Id).ErrorCode;
            if (errorCode == PenumbraApiSuccess)
            {
                assigned++;
                redrawObjectIndices.Add(objectIndex);
            }
            else if (errorCode == PenumbraApiNothingChanged)
            {
                alreadyAssigned++;
                if (!contentChanged)
                    continue;

                redrawObjectIndices.Add(objectIndex);
            }
            else
            {
                failedAssignments++;
                PluginService.Log.Warning("Could not apply Penumbra setup {Collection} to {Npc} at object index {Index}: {Code}", assignmentCollection.Value.Name, mapping.NpcName, objectIndex, errorCode);
            }
        }


        var redrawFailures = 0;
        foreach (var objectIndex in redrawObjectIndices)
        {
            try
            {
                penumbra.RedrawObject(objectIndex);
            }
            catch (Exception ex)
            {
                redrawFailures++;
                PluginService.Log.Warning(ex, "Could not redraw assigned NPC {Npc} at object index {Index}.", mapping.NpcName, objectIndex);
            }
        }

        if (targetCount > 0)
            lastRedrawSignature = redrawSignature;

        if (assigned > 0)
            statusItems.Add((true, $"Activated for {assigned} venue mannequin(s)"));

        if (alreadyAssigned > 0)
            statusItems.Add((true, $"Already active for {alreadyAssigned} venue mannequin(s)"));

        if (redrawObjectIndices.Count > 0)
            statusItems.Add((redrawFailures == 0, $"Refreshed {redrawObjectIndices.Count - redrawFailures} of {redrawObjectIndices.Count} venue mannequin(s)"));

        if (failedAssignments > 0)
            statusItems.Add((false, $"Could not activate {failedAssignments} venue mannequin(s)"));

        if (targetCount == 0)
        {
            if (nearbyMannequins.Count > 1)
                statusItems.Add((false, $"Found {nearbyMannequins.Count} mannequins here, so none was assumed to be the venue's: {string.Join(", ", mannequinDescriptions)}"));
            else if (nearbyMannequins.Count == 1 && !addressConfirmed)
                statusItems.Add((false, $"A mannequin is here but this is not confirmed as the venue address, so nothing was assigned ({addressDescription})"));
            else
                statusItems.Add((null, "Venue mannequin is not currently nearby"));
        }

        return PublishAssignmentResult(statusItems, mapping, needsAttention);
    }

    private AssignmentCollection? PrepareAssignmentCollection(ModMapping mapping, string modDirectory)
    {
        var permanentCollection = FindCollection(mapping.CollectionName);
        if (permanentCollection is not null)
        {
            if (!ReleaseManagedTemporaryCollection())
                return null;

            return EnableImportedMod(mapping, permanentCollection.Value, modDirectory)
                ? new AssignmentCollection(permanentCollection.Value.Id, permanentCollection.Value.Name, false)
                : null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (managedTemporaryCollectionId is null)
                {
                    var (errorCode, collectionId) = penumbra.CreateTemporaryCollection(
                        TemporaryCollectionIdentity,
                        TemporaryCollectionName);
                    if (!IsSuccess(errorCode) || collectionId == Guid.Empty)
                    {
                        PluginService.Log.Warning(
                            "Penumbra could not create the managed temporary collection. Code {Code}.",
                            errorCode);
                        return null;
                    }

                    managedTemporaryCollectionId = collectionId;
                }

                var configureCode = penumbra.EnableModInTemporaryCollection(
                    managedTemporaryCollectionId.Value,
                    modDirectory,
                    mapping.ModName,
                    mapping.Priority);
                if (IsSuccess(configureCode))
                {
                    return new AssignmentCollection(
                        managedTemporaryCollectionId.Value,
                        TemporaryCollectionName,
                        true);
                }

                PluginService.Log.Warning(
                    "Penumbra could not configure the managed temporary collection. Code {Code}.",
                    configureCode);
            }
            catch (Exception ex)
            {
                PluginService.Log.Warning(
                    ex,
                    "Automatic Penumbra collection setup is unavailable; a permanent Grid collection can be used instead.");
                return null;
            }

            _ = ReleaseManagedTemporaryCollection();
        }

        return null;
    }

    private bool ReleaseManagedTemporaryCollection()
    {
        if (managedTemporaryCollectionId is not { } collectionId)
            return true;

        try
        {
            var errorCode = penumbra.DeleteTemporaryCollection(collectionId);
            if (!IsSuccess(errorCode))
            {
                PluginService.Log.Warning(
                    "Penumbra could not remove the managed temporary collection {CollectionId}. Code {Code}.",
                    collectionId,
                    errorCode);
                return false;
            }

            managedTemporaryCollectionId = null;
            return true;
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Could not remove the managed temporary Penumbra collection.");
            return false;
        }
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

    /// <summary>
    /// Finds the venue mannequin, and only the venue mannequin.
    ///
    /// Matching is by the configured name. There is deliberately no fallback to
    /// "the only mannequin nearby": that fallback assigned the venue collection
    /// to whatever single mannequin happened to be around, which in someone
    /// else's house is a persistent individual assignment written into their
    /// Penumbra setup. Nearby mannequins are still counted, so the deck can say
    /// why it did nothing.
    /// </summary>
    private static IReadOnlyList<TargetObjectMatch> FindTargetNpcObjects(
        string npcName,
        out IReadOnlyList<TargetObjectMatch> nearbyMannequins,
        out IReadOnlyList<string> mannequinDescriptions)
    {
        var mannequins = new List<TargetObjectMatch>();
        var descriptions = new List<string>();
        nearbyMannequins = mannequins;
        mannequinDescriptions = descriptions;
        var namedMatches = new List<TargetObjectMatch>();

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

            if (!IsMannequinObject(gameObject))
                continue;

            // Record what it actually is, tag included. A mannequin's own name
            // is always "Mannequin", so without the tag this message says
            // nothing a reader can act on.
            var tag = TryGetObjectTag(gameObject);
            mannequins.Add(new TargetObjectMatch(objectIndex, gameObject.Name.TextValue, gameObject.ObjectKind.ToString(), false));
            descriptions.Add(tag.Length > 0
                ? $"{gameObject.Name.TextValue} «{tag}»"
                : $"{gameObject.Name.TextValue} [{gameObject.ObjectKind}]");
        }

        return namedMatches;
    }

    private static bool IsAssignableObject(IGameObject? gameObject)
    {
        if (gameObject is null)
            return false;

        if (PluginService.Objects.LocalPlayer?.GameObjectId == gameObject.GameObjectId)
            return false;

        return true;
    }

    /// <summary>
    /// Whether this object is the venue mannequin.
    ///
    /// A housing mannequin's object name is always "Mannequin". The name its
    /// owner gave it — Chromiel, here — is the title, shown in guillemets under
    /// the nameplate. Matching only on the name therefore never matched at all,
    /// and the deck was reaching the venue mannequin purely through a fallback
    /// that took the only mannequin nearby. That fallback was removed because
    /// it also assigned to other people's furniture, so the title has to be
    /// read for the venue's own setup to work.
    /// </summary>
    private static bool IsTargetNpc(IGameObject gameObject, string npcName)
    {
        if (!IsAssignableObject(gameObject))
            return false;

        if (NamesMatch(gameObject.Name.TextValue, npcName))
            return true;

        return NamesMatch(TryGetObjectTag(gameObject), npcName);
    }

    /// <summary>
    /// The name in guillemets under an object's nameplate.
    ///
    /// It shares a slot with a player's Free Company tag, which is why it is
    /// read from that field. Dalamud's object model does not expose it, so this
    /// goes through the native character struct — guarded by the managed type
    /// check, so the pointer is only followed for something that really is a
    /// character.
    /// </summary>
    private static unsafe string TryGetObjectTag(IGameObject gameObject)
    {
        // The pointer is only followed for kinds that really are laid out as a
        // character. A mannequin is not a player, so `is ICharacter` alone was
        // too narrow — it read nothing and reported no tag at all.
        var kind = gameObject.ObjectKind;
        var characterLike = gameObject is ICharacter
            || kind == ObjectKind.EventNpc
            || kind == ObjectKind.BattleNpc
            || kind == ObjectKind.Retainer;

        if (!characterLike)
            return string.Empty;

        try
        {
            var native = (NativeCharacter*)gameObject.Address;

            return native is null ? string.Empty : native->FreeCompanyTagString;
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(
                exception,
                "Could not read the nameplate tag for object index {Index}.",
                gameObject.ObjectIndex);

            return string.Empty;
        }
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

    /// <summary>
    /// Whether the game positively confirms this is the venue's address.
    ///
    /// Deliberately stricter than <see cref="IsKnownWrongVenueAddress"/>, which
    /// answers "can I prove you are elsewhere" and so says no whenever it cannot
    /// tell. This says yes only when every field the configured address
    /// specifies was actually readable here and matched.
    ///
    /// It exists because the venue mannequin cannot be identified by name: a
    /// housing mannequin is called "Mannequin", and the name its owner gave it
    /// is not on the object. So being at the venue is the evidence that the one
    /// mannequin in front of you is the venue's.
    /// </summary>
    /// <summary>
    /// Compares one field of a housing address.
    ///
    /// Tolerant on purpose. The game names the zone "Private House - Mist"
    /// where the venue address says "Mist", so strict equality reports a
    /// mismatch standing in the right place. Either side containing the other
    /// counts as a match, which is the same rule object names use.
    /// </summary>
    private static bool AddressFieldMatches(string? expected, string? current)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(current))
            return true;

        return NamesMatch(current, expected);
    }

    private bool IsConfirmedVenueAddress(out string description)
    {
        if (!TryParseVenueAddress(Config.VenueAddress, out var expected))
        {
            description = "the configured venue address could not be read";
            return false;
        }

        var currentMaybe = TryGetCurrentHousingAddress();
        if (currentMaybe is null)
        {
            description = "this location reports no housing address";
            return false;
        }

        var current = currentMaybe.Value;
        description =
            $"here: {current.WorldName ?? "?"} / {current.DistrictName ?? "?"} / " +
            $"W{current.Ward?.ToString() ?? "?"} P{current.Plot?.ToString() ?? "?"}";

        if (!AddressFieldMatches(expected.WorldName, current.WorldName))
            return false;
        if (!AddressFieldMatches(expected.DistrictName, current.DistrictName))
            return false;
        if (expected.Ward is not null && expected.Ward != current.Ward)
            return false;
        if (expected.Plot is not null && expected.Plot != current.Plot)
            return false;

        return true;
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
        if (!AddressFieldMatches(expected.WorldName, current.WorldName))
            mismatches.Add($"world {current.WorldName} != {expected.WorldName}");

        if (!AddressFieldMatches(expected.DistrictName, current.DistrictName))
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

    private readonly record struct AssignmentCollection(Guid Id, string Name, bool IsTemporary);

    private readonly record struct AssignmentResult(bool HasFailures, bool HasPending, bool NeedsAttention, string Detail);
}
