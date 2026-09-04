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

    /// <summary>
    /// Whether the deck reads venue content from the relay.
    ///
    /// On by default now that the relay is live and populated. It stays a
    /// setting because the deck has to keep working without it: turning this
    /// off falls back to the profiles and drinks bundled in the plugin, which
    /// is also what happens whenever the relay cannot be reached.
    /// </summary>
    public bool BackendEnabled { get; set; } = true;

    /// <summary>
    /// Where venue content is read from.
    ///
    /// Not editable in the interface. The deck trusts whatever this returns —
    /// profile text, links, and media URLs are all rendered — so a player who
    /// could be talked into pointing it somewhere else ("paste this for the
    /// beta menu") would be handing that trust to a stranger. Only the venue
    /// relay and a loopback address are accepted; see BackendClient.
    /// </summary>
    public string BackendBaseUrl { get; set; } = VenueRelayUrl;

    /// <summary>The venue's own relay. Changing this is a code change, deliberately.</summary>
    public const string VenueRelayUrl = "https://api.nroot.io";

    /// <summary>Chime when a tell arrives carrying the venue prefix.</summary>
    public bool MessageToneEnabled { get; set; } = true;

    // Remembers whether the home banner is folded away, per install.
    public bool NewsBannerCollapsed { get; set; } = false;

    // Publication time of the newest announcement the player has actually seen,
    // used for the unread badge. A timestamp rather than an id, so a pinned post
    // sitting at the top of the feed does not distort the count.
    public long LastSeenNewsUnixMs { get; set; }

    /// <summary>Rings when a broadcast the deck has not announced before arrives.</summary>
    public bool BroadcastToneEnabled { get; set; } = true;

    /// <summary>Opens the deck on a new broadcast, unless the moment is a bad one.</summary>
    public bool AutoOpenOnBroadcast { get; set; } = true;

    /// <summary>
    /// Whether the venue's furniture is applied while you are there.
    ///
    /// The mannequin assignment cannot carry these: housing VFX, the skybox and
    /// the room textures resolve through Penumbra's Base collection, which is the
    /// player's own. So the deck borrows Base rather than editing it — temporary
    /// settings that are never written to their config and are taken off again on
    /// the way out. On by default because a venue whose lights do not come up is
    /// the thing everyone reports; the setting exists because borrowing someone's
    /// Base collection at all should be refusable.
    /// </summary>
    public bool VenueFurnitureEnabled { get; set; } = true;

    /// <summary>
    /// Newest broadcast this deck has already announced.
    ///
    /// Deliberately not <see cref="LastSeenNewsUnixMs"/>, which records that the
    /// reader opened the Broadcast screen. Sharing one marker would mean a deck
    /// whose owner never opens that screen announces the same post on every
    /// refresh, forever.
    /// </summary>
    public long LastAnnouncedNewsUnixMs { get; set; }
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

        // A stored relay address from an older build wins over the default,
        // which means an install that once pointed at a developer's machine
        // would stay there forever — and since the address is no longer shown
        // as a field, there is no way to correct it from inside the game.
        // Anything that is not the venue relay is reset here.
        if (!string.Equals(BackendBaseUrl, VenueRelayUrl, System.StringComparison.OrdinalIgnoreCase))
            BackendBaseUrl = VenueRelayUrl;

        // Raise the priority stored by builds that defaulted it to zero. Not a
        // user setting — it is not editable anywhere — so there is no choice here
        // to overwrite, and leaving it at zero would mean existing installs never
        // get the fix that the new default exists for.
        if (Mappings[0].Priority <= 0)
            Mappings[0].Priority = ModMapping.DefaultPriority;

        // Migrate the old beta-specific asset pattern to "any .pmp".
        if (string.IsNullOrWhiteSpace(Mappings[0].AssetPattern)
            || string.Equals(Mappings[0].AssetPattern, ModMapping.LegacyAssetPattern, System.StringComparison.OrdinalIgnoreCase))
            Mappings[0].AssetPattern = ModMapping.DefaultAssetPattern;

        return Mappings[0];
    }
}
