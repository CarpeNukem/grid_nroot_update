using System;
using Dalamud.Bindings.ImGui;

namespace GridNrootUpdate;

internal sealed class ConfigWindow
{
    private readonly PluginConfig config;
    private readonly Action queueReconcile;
    private readonly Action assignAll;

    public bool IsOpen;

    public ConfigWindow(PluginConfig config, Action queueReconcile, Action assignAll)
    {
        this.config = config;
        this.queueReconcile = queueReconcile;
        this.assignAll = assignAll;
    }

    public void Draw()
    {
        if (!IsOpen)
            return;

        if (!ImGui.Begin("TheGrid Updater Config", ref IsOpen))
        {
            ImGui.End();
            return;
        }

        var mapping = config.GetPrimaryMapping();
        var changed = false;
        var mappingChanged = false;

        changed |= InputText("Venue address", config.VenueAddress, value => config.VenueAddress = value);
        changed |= InputText("Discord URL", config.DiscordUrl, value => config.DiscordUrl = value);
        changed |= InputBool("Auto-open when venue mannequin is detected", config.AutoOpenOnVenueAddress, value => config.AutoOpenOnVenueAddress = value);
        ImGui.Separator();

        ImGui.TextUnformatted($"Repository: {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}");
        mappingChanged |= InputText("Asset pattern", mapping.AssetPattern, value => mapping.AssetPattern = value);
        mappingChanged |= InputText("Collection name", mapping.CollectionName, value => mapping.CollectionName = value);
        mappingChanged |= InputText("NPC name", mapping.NpcName, value => mapping.NpcName = value);
        mappingChanged |= InputText("Penumbra folder path", mapping.PenumbraFolderPath, value => mapping.PenumbraFolderPath = value);
        mappingChanged |= InputText("Penumbra mod directory", mapping.ModDirectory, value => mapping.ModDirectory = value);
        mappingChanged |= InputText("Penumbra mod name", mapping.ModName, value => mapping.ModName = value);

        mappingChanged |= InputInt("Priority", mapping.Priority, value => mapping.Priority = value);
        changed |= mappingChanged;

        if (mappingChanged)
        {
            mapping.LastAppliedVersion = string.Empty;
            mapping.LastStatus = "Config changed. Run Update to apply.";
        }

        if (changed)
            config.Save();

        if (ImGui.Button("Save and Update"))
        {
            config.Save();
            queueReconcile();
        }

        ImGui.SameLine();
        if (ImGui.Button("Install Now"))
            assignAll();

        ImGui.TextWrapped("TheGrid must already exist in Penumbra. Current public Penumbra IPC can assign existing collections but does not expose named collection creation.");

        ImGui.End();
    }

    private static bool InputBool(string label, bool currentValue, Action<bool> setValue)
    {
        var value = currentValue;
        if (!ImGui.Checkbox(label, ref value))
            return false;

        setValue(value);
        return true;
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
}
