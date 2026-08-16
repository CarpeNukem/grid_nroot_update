using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace GridNrootUpdate;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string VenueAddress { get; set; } = "Light Raiden Mist W2 P6";
    public string DiscordUrl { get; set; } = "https://discord.gg/kxZMbP3C5B";
    public bool AutoOpenOnVenueAddress { get; set; } = true;
    public bool NetworkAlertBadge { get; set; } = true;
    public bool TarotHost { get; set; } = false;
    public bool ReduceMotion { get; set; } = false;
    public bool FullAuto { get; set; } = true;
    public bool FirstRunCompleted { get; set; } = false;
    public float UiScale { get; set; } = 0;
    public CyberdeckThemeId Theme { get; set; } = CyberdeckThemeId.Grid;
    public Vector4 CustomThemeBackground { get; set; } = new(0x07 / 255f, 0x10 / 255f, 0x17 / 255f, 1f);
    public Vector4 CustomThemePrimary { get; set; } = new(0x31 / 255f, 0xE6 / 255f, 0xDF / 255f, 1f);
    public Vector4 CustomThemeSecondary { get; set; } = new(0xFF / 255f, 0x3E / 255f, 0xB5 / 255f, 1f);
    public Vector4 CustomThemeText { get; set; } = new(0xD5 / 255f, 0xFB / 255f, 0xF7 / 255f, 1f);
    public int IntrusionDifficulty { get; set; } = 1;
    public int IntrusionBestCasualScore { get; set; }
    public int IntrusionBestStandardScore { get; set; }
    public int IntrusionBestBlackIceScore { get; set; }
    public int IntrusionSuccessfulBreaches { get; set; }
    public List<string> CipherSolvedIntercepts { get; set; } = [];
    public Dictionary<string, int> CipherHintLevels { get; set; } = [];
    public int CipherVaultVersion { get; set; }
    public bool CipherDecoyTriggered { get; set; }
    public int CipherTracePenalty { get; set; }
    public int CipherRunSeed { get; set; }
    public bool CipherRunActive { get; set; }
    public bool CipherRunCompleted { get; set; }
    public bool CipherRunCompromised { get; set; }
    public int CipherTraceLevel { get; set; }
    public int CipherBestScore { get; set; }
    public string CipherBestGrade { get; set; } = string.Empty;
    public bool CipherPrizeUnlocked { get; set; }
    public int CipherAbortedRuns { get; set; }
    public int CipherAuthFailedAttempts { get; set; }
    public long CipherLockoutUntilUnixMs { get; set; }
    public long CipherRunStartedUnixMs { get; set; }
    public NetworkSessionCheckpoint? ActiveNetworkSession { get; set; }
    public List<NetworkSessionSummary> NetworkSessionHistory { get; set; } = [];

    public List<ModMapping> Mappings { get; set; } =
    [
        ModMapping.CreateDefault(),
    ];

    public void Save()
        => PluginService.PluginInterface.SavePluginConfig(this);

    public ModMapping GetPrimaryMapping()
    {
        NetworkSessionHistory ??= [];
        ActiveNetworkSession?.OccupancyBuckets ??= [];
        foreach (var summary in NetworkSessionHistory)
            summary.OccupancyBuckets ??= [];

        if (Mappings.Count == 0)
            Mappings.Add(ModMapping.CreateDefault());

        if (Mappings.Count > 1)
            Mappings.RemoveRange(1, Mappings.Count - 1);

        if (Mappings[0].ModDirectory == "TheGrid")
            Mappings[0].ModDirectory = "n_root_the_grid";

        if (string.IsNullOrWhiteSpace(Mappings[0].ModName))
            Mappings[0].ModName = "n_root_the_grid";

        if (string.IsNullOrWhiteSpace(Mappings[0].PenumbraFolderPath))
            Mappings[0].PenumbraFolderPath = "TheGrid";

        // Migrate the old beta-specific asset pattern to "any .pmp".
        if (string.IsNullOrWhiteSpace(Mappings[0].AssetPattern)
            || string.Equals(Mappings[0].AssetPattern, ModMapping.LegacyAssetPattern, System.StringComparison.OrdinalIgnoreCase))
            Mappings[0].AssetPattern = ModMapping.DefaultAssetPattern;

        return Mappings[0];
    }
}
