using System;
using System.Text.RegularExpressions;

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
    ///
    /// Matched whole, never as a prefix. Normalised, "n_root_the_grid" is
    /// "nrootthegrid", which is also the start of every "[//n_root] The Grid's …"
    /// mod — dotes, throws, props the venue crew publish and players keep — and
    /// as a prefix it had the update path delete four of them as "older editions".
    /// </summary>
    private static readonly string[] LegacyModNames = ["n_root_the_grid", "TheGrid"];

    /// <summary>
    /// Whether a mod Penumbra is holding is this mapping's, whatever it is called.
    ///
    /// Compared with case and punctuation stripped out, so the trademark sign, the
    /// dash and the spacing are all decoration, and Penumbra's " (2)" suffix on a
    /// re-import does not stop a pack being recognised.
    ///
    /// Being wrong here is not cosmetic: when the update path decides which mod is
    /// the old one, it switches it off in the venue collection.
    /// </summary>
    public bool MatchesMod(string? modDirectory, string? modName)
    {
        if (!string.IsNullOrEmpty(modDirectory) &&
            string.Equals(modDirectory, ModDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(modName) &&
            string.Equals(modName, ModName, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsVenuePackName(modDirectory, modName);
    }

    /// <summary>
    /// Whether a mod is named like a venue pack, going by name alone.
    ///
    /// Unlike <see cref="MatchesMod"/> this ignores what the mapping currently
    /// points at, which can be stale or wrong. It is what decides which mods an
    /// update switches off as older editions.
    /// </summary>
    public bool IsVenuePackName(string? modDirectory, string? modName)
        => IsVenuePackName(modDirectory) || IsVenuePackName(modName);

    /// <summary>
    /// The family as a prefix, the legacy names whole.
    ///
    /// The family prefix is deliberately not a substring search. "grid" on its own
    /// also matches mods like GridWeave and CYBER-TP (GRIDLESS).
    /// </summary>
    private bool IsVenuePackName(string? candidate)
    {
        var normalized = CollectionNameMatcher.Normalize(StripDuplicateSuffix(candidate));
        if (normalized.Length == 0)
            return false;

        var family = CollectionNameMatcher.Normalize(ModFamily);
        if (family.Length > 0 && normalized.StartsWith(family, StringComparison.Ordinal))
            return true;

        foreach (var legacy in LegacyModNames)
        {
            if (string.Equals(normalized, CollectionNameMatcher.Normalize(legacy), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Drops the " (2)" Penumbra appends to a directory on a repeat import.</summary>
    private static string? StripDuplicateSuffix(string? candidate)
        => candidate is null ? null : DuplicateSuffix.Replace(candidate.Trim(), string.Empty);

    private static readonly Regex DuplicateSuffix = new(@"\s\(\d+\)$", RegexOptions.CultureInvariant);

    public static ModMapping CreateDefault()
        => new();
}
