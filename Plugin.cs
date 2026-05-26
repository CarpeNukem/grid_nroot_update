using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/thegrid";
    private const byte MaleNonPlayerCharacterCollectionType = 3;
    private readonly CancellationTokenSource lifetime = new();
    private readonly GitHubReleaseClient github = new();
    private readonly PenumbraIpc penumbra;
    private readonly object modAddedLock = new();
    private bool reconcileQueued;
    private bool reconcileRunning;
    private bool mainUiOpen;
    private bool configUiOpen;
    private bool modAddedSubscribed;
    private TaskCompletionSource<string>? pendingModAdded;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<PluginService>();
        Config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        Config.GetPrimaryMapping();
        Config.Save();

        penumbra = new PenumbraIpc(pluginInterface);

        PluginService.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Manage TheGrid Penumbra update and Chromiel assignment.",
            ShowInHelp = true,
        });

        PluginService.ClientState.Login += OnLogin;
        PluginService.ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginService.Framework.Update += OnFrameworkUpdate;
        TrySubscribePenumbraEvents();
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        QueueReconcile();
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
        lifetime.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var split = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var subCommand = split.Length == 0 ? "status" : split[0].ToLowerInvariant();
        var value = split.Length > 1 ? split[1] : string.Empty;

        switch (subCommand)
        {
            case "":
            case "status":
                PrintStatus();
                break;
            case "asset":
                SetAssetPattern(value);
                break;
            case "update":
                QueueReconcile();
                PluginService.Chat.Print("Queued update check.", "TheGrid");
                break;
            case "force":
            case "reinstall":
                QueueReconcile(forceDownload: true);
                PluginService.Chat.Print("Queued forced reinstall.", "TheGrid");
                break;
            case "assign":
                _ = AssignAllAsync(lifetime.Token);
                break;
            case "config":
                OpenConfigUi();
                break;
            default:
                PluginService.Chat.PrintError($"Unknown command '{args}'. Use status, asset, update, reinstall, assign, or config.", "TheGrid");
                break;
        }
    }

    private void OpenMainUi()
        => mainUiOpen = true;

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
        => configUiOpen = true;

    private void DrawUi()
    {
        DrawMainWindow();
        DrawConfigWindow();
    }

    private void DrawMainWindow()
    {
        if (!mainUiOpen)
            return;

        if (!ImGui.Begin("TheGrid Updater", ref mainUiOpen))
        {
            ImGui.End();
            return;
        }

        var mapping = Config.GetPrimaryMapping();
        ImGui.TextUnformatted("TheGrid");
        ImGui.TextUnformatted($"{ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}");
        ImGui.TextUnformatted($"Last applied: {DisplayValue(mapping.LastAppliedVersion)}");
        ImGui.TextWrapped(mapping.LastStatus);

        if (ImGui.Button("Update"))
            QueueReconcile();

        ImGui.SameLine();
        if (ImGui.Button("Assign"))
            _ = AssignAllAsync(lifetime.Token);

        ImGui.SameLine();
        if (ImGui.Button("Force Reinstall"))
            QueueReconcile(forceDownload: true);

        ImGui.Spacing();

        if (ImGui.Button("Open Config"))
            OpenConfigUi();

        ImGui.End();
    }

    private void DrawConfigWindow()
    {
        if (!configUiOpen)
            return;

        if (!ImGui.Begin("TheGrid Updater Config", ref configUiOpen))
        {
            ImGui.End();
            return;
        }

        var mapping = Config.GetPrimaryMapping();
        var changed = false;

        ImGui.TextUnformatted($"Repository: {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}");
        changed |= InputText("Asset pattern", mapping.AssetPattern, value => mapping.AssetPattern = value);
        changed |= InputText("Collection name", mapping.CollectionName, value => mapping.CollectionName = value);
        changed |= InputText("NPC name", mapping.NpcName, value => mapping.NpcName = value);
        changed |= InputText("Penumbra mod directory", mapping.ModDirectory, value => mapping.ModDirectory = value);
        changed |= InputText("Penumbra mod name", mapping.ModName, value => mapping.ModName = value);

        changed |= InputInt("Priority", mapping.Priority, value => mapping.Priority = value);

        if (changed)
        {
            mapping.LastAppliedVersion = string.Empty;
            mapping.LastStatus = "Config changed. Run Update to apply.";
            Config.Save();
        }

        if (ImGui.Button("Save and Update"))
        {
            Config.Save();
            QueueReconcile();
        }

        ImGui.SameLine();
        if (ImGui.Button("Assign Now"))
            _ = AssignAllAsync(lifetime.Token);

        ImGui.TextWrapped("TheGrid must already exist in Penumbra. Current public Penumbra IPC can assign existing collections but does not expose named collection creation.");

        ImGui.End();
    }

    private static bool InputText(string label, string currentValue, Action<string> setValue)
    {
        var value = currentValue;
        if (!ImGui.InputText(label, ref value, 512))
            return false;

        setValue(value);
        return true;
    }

    private static bool InputInt(string label, int currentValue, Action<int> setValue)
    {
        var value = currentValue;
        if (!ImGui.InputInt(label, ref value))
            return false;

        setValue(value);
        return true;
    }

    private static string DisplayValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "(unset)" : value;

    private void OnLogin()
        => QueueReconcile();

    private void OnTerritoryChanged(uint _)
        => QueueAssignment();

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!reconcileQueued || reconcileRunning)
            return;

        reconcileQueued = false;
        reconcileRunning = true;
        Task.Run(ReconcileAsync, lifetime.Token);
    }

    private void QueueReconcile(bool forceDownload = false)
    {
        if (forceDownload)
        {
            var mapping = Config.GetPrimaryMapping();
            mapping.LastAppliedVersion = string.Empty;
            Config.Save();
        }

        reconcileQueued = true;
    }

    private void QueueAssignment()
        => PluginService.Framework.RunOnTick(() => _ = AssignAllAsync(lifetime.Token), delay: TimeSpan.FromSeconds(2), cancellationToken: lifetime.Token);

    private async Task ReconcileAsync()
    {
        try
        {
            if (!penumbra.IsAvailable())
            {
                SetAllStatus("Penumbra IPC is not available. Install and enable Penumbra, then run /thegrid update.");
                return;
            }

            TrySubscribePenumbraEvents();

            await ReconcileMappingAsync(Config.GetPrimaryMapping(), lifetime.Token).ConfigureAwait(false);

            Config.Save();
            await AssignAllAsync(lifetime.Token).ConfigureAwait(false);
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
        }
    }

    private async Task ReconcileMappingAsync(ModMapping mapping, CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(PluginService.PluginInterface.ConfigDirectory.FullName, "cache");
        var latestAsset = await github.GetLatestReleaseAssetInfoAsync(mapping, cancellationToken).ConfigureAwait(false);
        if (string.Equals(latestAsset.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
        {
            mapping.LastStatus = $"Latest release {latestAsset.Version} already applied.";
            return;
        }

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

        var collection = FindCollection(mapping.CollectionName)
            ?? throw new InvalidOperationException($"Collection '{mapping.CollectionName}' does not exist. Create it in Penumbra once, then run /thegrid update.");

        var modDirectory = TryResolveModDirectory(mapping, modsAfterInstall, modsBeforeInstall, addedDirectory);
        if (modDirectory is not null)
        {
            var enableCode = penumbra.TrySetMod(collection.Id, modDirectory, mapping.ModName, true);
            if (!IsSuccess(enableCode))
                PluginService.Log.Warning("Could not enable mod {ModDirectory} in {Collection}. Penumbra code {Code}.", modDirectory, mapping.CollectionName, enableCode);

            var priorityCode = penumbra.TrySetModPriority(collection.Id, modDirectory, mapping.ModName, mapping.Priority);
            if (!IsSuccess(priorityCode))
                PluginService.Log.Warning("Could not set mod priority for {ModDirectory}. Penumbra code {Code}.", modDirectory, priorityCode);

            mapping.ModDirectory = modDirectory;
        }
        else
        {
            PluginService.Log.Warning("Penumbra accepted package {Package}, but the imported mod was not visible in the IPC mod list immediately after import.", Path.GetFileName(download.Path));
        }

        mapping.LastAppliedVersion = download.Version;
        mapping.LastStatus = modDirectory is null
            ? $"Imported latest release {download.Version}; Penumbra did not expose the mod in IPC immediately."
            : $"Applied latest release {download.Version}.";
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
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

    private void DeleteExistingManagedModBeforeInstall(string previousModDirectory, string previousModName, System.Collections.Generic.Dictionary<string, string> installedMods)
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
        if (!penumbra.IsAvailable())
        {
            mapping.LastStatus = "Penumbra IPC is not available.";
            return;
        }

        var collection = FindCollection(mapping.CollectionName);
        if (collection is null)
        {
            mapping.LastStatus = $"Collection '{mapping.CollectionName}' does not exist.";
            PluginService.Chat.PrintError($"Collection '{mapping.CollectionName}' does not exist. Create it in Penumbra first, then run /thegrid assign.", "TheGrid");
            return;
        }

        var simpleAssignmentStatus = AssignMaleNonPlayerCharacters(mapping, collection.Value);
        var assigned = 0;
        for (var i = 0; i < PluginService.Objects.Length; i++)
        {
            var gameObject = PluginService.Objects[i];
            if (!IsTargetNpc(gameObject, mapping.NpcName))
                continue;

            var (errorCode, _) = penumbra.SetCollectionForObject(i, collection.Value.Id);
            if (IsSuccess(errorCode))
                assigned++;
            else
                PluginService.Log.Warning("Could not assign collection {Collection} to {Npc} at object index {Index}: {Code}", mapping.CollectionName, mapping.NpcName, i, errorCode);
        }

        var objectAssignmentStatus = assigned == 0
            ? $"No loaded NPC named '{mapping.NpcName}' found. Assignment will retry on territory changes."
            : $"Assigned '{mapping.CollectionName}' to {assigned} loaded '{mapping.NpcName}' object(s).";

        mapping.LastStatus = $"{objectAssignmentStatus} {simpleAssignmentStatus}";
    }

    private string AssignMaleNonPlayerCharacters(ModMapping mapping, (Guid Id, string Name) collection)
    {
        var (errorCode, _) = penumbra.SetCollection(MaleNonPlayerCharacterCollectionType, collection.Id);
        if (IsSuccess(errorCode))
            return $"Assigned '{mapping.CollectionName}' to Male Non-Player Characters.";

        PluginService.Log.Warning("Could not assign collection {Collection} to Male Non-Player Characters: {Code}", mapping.CollectionName, errorCode);
        return $"Could not assign '{mapping.CollectionName}' to Male Non-Player Characters. Penumbra code {errorCode}.";
    }

    private (Guid Id, string Name)? FindCollection(string collectionName)
        => penumbra.GetCollectionsByIdentifier(collectionName)
            .FirstOrDefault(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase)) is var collection && collection.Id != Guid.Empty
                ? collection
                : null;

    private static string? TryResolveModDirectory(ModMapping mapping, System.Collections.Generic.Dictionary<string, string> mods, System.Collections.Generic.Dictionary<string, string> modsBeforeInstall, string? addedDirectory)
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

        if (gameObject.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc))
            return false;

        return string.Equals(gameObject.Name.TextValue, npcName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuccess(int penumbraCode)
        => penumbraCode is 0 or 1;

    private void PrintStatus()
    {
        var mapping = Config.GetPrimaryMapping();
        PluginService.Chat.Print(
            $"{mapping.Name}: latestRelease=auto, lastApplied={mapping.LastAppliedVersion}, collection={mapping.CollectionName}, npc={mapping.NpcName}, status={mapping.LastStatus}",
            "TheGrid");
    }

    private void SetAssetPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            PluginService.Chat.PrintError("Usage: /thegrid asset <glob>", "TheGrid");
            return;
        }

        var mapping = Config.GetPrimaryMapping();
        mapping.AssetPattern = pattern;
        mapping.LastStatus = $"Configured asset pattern {pattern}.";

        Config.Save();
        PluginService.Chat.Print($"Configured asset pattern {pattern}.", "TheGrid");
    }

    private static void PrintConfigHelp()
        => PluginService.Chat.Print($"Updates are locked to the latest release from {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}. Use /thegrid asset <glob>, then /thegrid update. Edit config JSON for ModDirectory, ModName, CollectionName, NpcName, or additional mappings.", "TheGrid");

    private void SetAllStatus(string status)
    {
        Config.GetPrimaryMapping().LastStatus = status;

        Config.Save();
        PluginService.Chat.PrintError(status, "TheGrid");
    }
}
