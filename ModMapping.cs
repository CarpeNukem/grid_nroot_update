using System;

namespace GridNrootUpdate;

public sealed class ModMapping
{
    public const string FixedGitHubOwner = "CarpeNukem";
    public const string FixedGitHubRepo = "grid_nroot_update";

    public string Name { get; set; } = "TheGrid";
    public string LastAppliedVersion { get; set; } = string.Empty;
    public const string LegacyAssetPattern = "n_root_the_grid_beta.pmp";
    public const string DefaultAssetPattern = "*.pmp";

    public string AssetPattern { get; set; } = DefaultAssetPattern;
    public string CollectionName { get; set; } = "TheGrid";
    public string NpcName { get; set; } = "Chromiel";
    public string PenumbraFolderPath { get; set; } = "TheGrid";
    public string ModDirectory { get; set; } = "n_root_the_grid";
    public string ModName { get; set; } = "n_root_the_grid";
    /// <summary>
    /// Where the venue mod sits when something else redirects the same file.
    ///
    /// Above 100 on purpose. The mod is applied to the player's Base collection
    /// while they are at the venue — that is the only place its furniture and the
    /// effects it plays resolve — and Base is shared with everything else they run.
    /// At the old 0 the venue's furniture lost coin flips against any other mod
    /// touching the same paths, which reads as the venue simply being broken.
    /// </summary>
    public const int DefaultPriority = 101;

    public int Priority { get; set; } = DefaultPriority;
    public string LastStatus { get; set; } = "Not applied yet.";

    /// <summary>
    /// The product family the venue mod ships under.
    ///
    /// The pack is not one fixed name: it is "Grid CityScape" plus an edition —
    /// "Grid CityScape(tm) - Summer Party Edition" — and the venue can rename or
    /// reskin an edition without the plugin being rebuilt. So identity lives in
    /// the family, and everything after it is free text.
    /// </summary>
    public string ModFamily { get; set; } = DefaultModFamily;

    public const string DefaultModFamily = "Grid CityScape";

    /// <summary>
    /// Names earlier packs shipped under.
    ///
    /// Kept so an install from before the rename is still recognised rather than
    /// reported missing and reinstalled alongside itself.
    /// </summary>
    private static readonly string[] LegacyModNames = ["n_root_the_grid", "TheGrid"];

    /// <summary>
    /// Whether a mod Penumbra is holding is this mapping's, whatever it is called.
    ///
    /// Compared with case and punctuation stripped out, so the trademark sign, the
    /// dash and the spacing are all decoration, and Penumbra's " (2)" suffix on a
    /// re-import does not stop a pack being recognised.
    ///
    /// A prefix and deliberately not a substring search. "grid" on its own also
    /// matches mods like GridWeave and CYBER-TP (GRIDLESS), and being wrong here
    /// is not cosmetic: when the update path decides which mod is the old one, it
    /// deletes it.
    /// </summary>
    public bool MatchesMod(string? modDirectory, string? modName)
    {
        if (!string.IsNullOrEmpty(modDirectory) &&
            string.Equals(modDirectory, ModDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(modName) &&
            string.Equals(modName, ModName, StringComparison.OrdinalIgnoreCase))
            return true;

        return HasKnownPrefix(modDirectory) || HasKnownPrefix(modName);
    }

    private bool HasKnownPrefix(string? candidate)
    {
        var normalized = CollectionNameMatcher.Normalize(candidate);
        if (normalized.Length == 0)
            return false;

        if (StartsWithName(normalized, ModFamily))
            return true;

        foreach (var legacy in LegacyModNames)
        {
            if (StartsWithName(normalized, legacy))
                return true;
        }

        return false;
    }

    private static bool StartsWithName(string normalizedCandidate, string? name)
    {
        var normalizedName = CollectionNameMatcher.Normalize(name);

        return normalizedName.Length > 0 && normalizedCandidate.StartsWith(normalizedName, StringComparison.Ordinal);
    }

    public static ModMapping CreateDefault()
        => new();
}
