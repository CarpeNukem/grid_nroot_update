namespace GridNrootUpdate;

public sealed class ModMapping
{
    public const string FixedGitHubOwner = "CarpeNukem";
    public const string FixedGitHubRepo = "grid_nroot_update";

    public string Name { get; set; } = "TheGrid";
    public string LastAppliedVersion { get; set; } = string.Empty;
    public string AssetPattern { get; set; } = "n_root_the_grid_beta.pmp";
    public string CollectionName { get; set; } = "TheGrid";
    public string NpcName { get; set; } = "Chromiel";
    public string PenumbraFolderPath { get; set; } = "TheGrid";
    public string ModDirectory { get; set; } = "n_root_the_grid";
    public string ModName { get; set; } = "n_root_the_grid";
    public int Priority { get; set; } = 0;
    public string LastStatus { get; set; } = "Not applied yet.";

    public static ModMapping CreateDefault()
        => new();
}
