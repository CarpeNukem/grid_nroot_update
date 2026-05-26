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
    private readonly CancellationTokenSource lifetime = new();
    private readonly GitHubReleaseClient github = new();
    private readonly PenumbraIpc penumbra;
    private bool reconcileQueued;
    private bool reconcileRunning;
    private bool mainUiOpen;
    private bool configUiOpen;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<PluginService>();
        Config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        if (Config.Mappings.Count == 0)
        {
            Config.Mappings.Add(ModMapping.CreateDefault());
            Config.Save();
        }

        penumbra = new PenumbraIpc(pluginInterface);

        PluginService.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Manage TheGrid Penumbra update and Chromiel assignment.",
            ShowInHelp = true,
        });

        PluginService.ClientState.Login += OnLogin;
        PluginService.ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginService.Framework.Update += OnFrameworkUpdate;
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
                QueueReconcile(forceDownload: true);
                PluginService.Chat.Print("Queued update reconciliation.", "TheGrid");
                break;
            case "assign":
                _ = AssignAllAsync(lifetime.Token);
                break;
            case "config":
                OpenConfigUi();
                break;
            default:
                PluginService.Chat.PrintError($"Unknown command '{args}'. Use status, asset, update, assign, or config.", "TheGrid");
                break;
        }
    }

    private void OpenMainUi()
        => mainUiOpen = true;

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

        foreach (var mapping in Config.Mappings)
        {
            ImGui.TextUnformatted("TheGrid");
            ImGui.TextUnformatted($"{ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}");
            ImGui.TextUnformatted($"Last applied: {DisplayValue(mapping.LastAppliedVersion)}");
            ImGui.TextWrapped(mapping.LastStatus);

            if (ImGui.Button("Update"))
                QueueReconcile(forceDownload: true);

            ImGui.SameLine();
            if (ImGui.Button("Assign"))
                _ = AssignAllAsync(lifetime.Token);

            ImGui.Spacing();
        }

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

        if (Config.Mappings.Count == 0)
            Config.Mappings.Add(ModMapping.CreateDefault());

        var mapping = Config.Mappings[0];
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
            QueueReconcile(forceDownload: true);
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
            foreach (var mapping in Config.Mappings)
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

            foreach (var mapping in Config.Mappings)
                await ReconcileMappingAsync(mapping, lifetime.Token).ConfigureAwait(false);

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
        var download = await github.DownloadLatestReleaseAssetAsync(mapping, cacheDirectory, cancellationToken).ConfigureAwait(false);
        if (!download.FromRepositoryFallback && string.Equals(download.Version, mapping.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
        {
            mapping.LastStatus = $"Latest release {download.Version} already applied.";
            return;
        }

        var installCode = penumbra.InstallMod(download.Path);
        if (!IsSuccess(installCode))
            throw new InvalidOperationException($"Penumbra rejected package '{Path.GetFileName(download.Path)}' with code {installCode}.");

        var collection = FindCollection(mapping.CollectionName)
            ?? throw new InvalidOperationException($"Collection '{mapping.CollectionName}' does not exist. Create it in Penumbra once, then run /thegrid update.");

        var modDirectory = ResolveModDirectory(mapping);
        var enableCode = penumbra.TrySetMod(collection.Id, modDirectory, mapping.ModName, true);
        if (!IsSuccess(enableCode))
            throw new InvalidOperationException($"Could not enable mod '{modDirectory}' in '{mapping.CollectionName}'. Penumbra code {enableCode}.");

        var priorityCode = penumbra.TrySetModPriority(collection.Id, modDirectory, mapping.ModName, mapping.Priority);
        if (!IsSuccess(priorityCode))
            throw new InvalidOperationException($"Could not set mod priority for '{modDirectory}'. Penumbra code {priorityCode}.");

        mapping.ModDirectory = modDirectory;
        mapping.LastAppliedVersion = download.Version;
        mapping.LastStatus = download.FromRepositoryFallback
            ? $"Applied fallback package from {ModMapping.FixedGitHubBranch}."
            : $"Applied latest release {download.Version}.";
        PluginService.Chat.Print($"{mapping.Name}: {mapping.LastStatus}", "TheGrid");
    }

    private async Task AssignAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PluginService.Framework.RunOnFrameworkThread(() =>
            {
                foreach (var mapping in Config.Mappings)
                    AssignMapping(mapping);

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
            return;
        }

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

        mapping.LastStatus = assigned == 0
            ? $"No loaded NPC named '{mapping.NpcName}' found. Assignment will retry on territory changes."
            : $"Assigned '{mapping.CollectionName}' to {assigned} loaded '{mapping.NpcName}' object(s).";
    }

    private (Guid Id, string Name)? FindCollection(string collectionName)
        => penumbra.GetCollectionsByIdentifier(collectionName)
            .FirstOrDefault(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase)) is var collection && collection.Id != Guid.Empty
                ? collection
                : null;

    private string ResolveModDirectory(ModMapping mapping)
    {
        var mods = penumbra.GetModList();
        if (mods.ContainsKey(mapping.ModDirectory))
            return mapping.ModDirectory;

        var byName = mods.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, mapping.ModName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Value, mapping.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kvp.Key, mapping.Name, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(byName.Key))
            return byName.Key;

        throw new InvalidOperationException($"Installed Penumbra mod '{mapping.ModDirectory}' was not found after import.");
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
        foreach (var mapping in Config.Mappings)
        {
            PluginService.Chat.Print(
                $"{mapping.Name}: latestRelease=auto, lastApplied={mapping.LastAppliedVersion}, collection={mapping.CollectionName}, npc={mapping.NpcName}, status={mapping.LastStatus}",
                "TheGrid");
        }
    }

    private void SetAssetPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            PluginService.Chat.PrintError("Usage: /thegrid asset <glob>", "TheGrid");
            return;
        }

        foreach (var mapping in Config.Mappings)
        {
            mapping.AssetPattern = pattern;
            mapping.LastStatus = $"Configured asset pattern {pattern}.";
        }

        Config.Save();
        PluginService.Chat.Print($"Configured asset pattern {pattern}.", "TheGrid");
    }

    private static void PrintConfigHelp()
        => PluginService.Chat.Print($"Updates are locked to the latest release from {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}. Use /thegrid asset <glob>, then /thegrid update. Edit config JSON for ModDirectory, ModName, CollectionName, NpcName, or additional mappings.", "TheGrid");

    private void SetAllStatus(string status)
    {
        foreach (var mapping in Config.Mappings)
            mapping.LastStatus = status;

        Config.Save();
        PluginService.Chat.PrintError(status, "TheGrid");
    }
}
