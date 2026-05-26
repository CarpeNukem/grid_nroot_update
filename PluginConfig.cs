using System.Collections.Generic;
using Dalamud.Configuration;

namespace GridNrootUpdate;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<ModMapping> Mappings { get; set; } =
    [
        ModMapping.CreateDefault(),
    ];

    public void Save()
        => PluginService.PluginInterface.SavePluginConfig(this);

    public ModMapping GetPrimaryMapping()
    {
        if (Mappings.Count == 0)
            Mappings.Add(ModMapping.CreateDefault());

        if (Mappings.Count > 1)
            Mappings.RemoveRange(1, Mappings.Count - 1);

        if (Mappings[0].ModDirectory == "TheGrid")
            Mappings[0].ModDirectory = "n_root_the_grid";

        if (string.IsNullOrWhiteSpace(Mappings[0].ModName))
            Mappings[0].ModName = "n_root_the_grid";

        return Mappings[0];
    }
}
