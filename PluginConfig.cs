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
}
