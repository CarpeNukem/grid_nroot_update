namespace GridNrootUpdate;

public sealed class ModMapping
{
    public string Name { get; set; } = "TheGrid";
    public string DesiredVersion { get; set; } = "0.0.0";
    public string LastAppliedVersion { get; set; } = string.Empty;
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string ReleaseTagPattern { get; set; } = "v{version}";
    public string AssetPattern { get; set; } = "*.pmp";
    public string CollectionName { get; set; } = "TheGrid";
    public string NpcName { get; set; } = "Chromiel";
    public string ModDirectory { get; set; } = "TheGrid";
    public string ModName { get; set; } = "";
    public int Priority { get; set; } = 0;
    public string LastStatus { get; set; } = "Not applied yet.";

    public string ReleaseTag
        => ReleaseTagPattern.Replace("{version}", DesiredVersion);

    public static ModMapping CreateDefault()
        => new();
}
