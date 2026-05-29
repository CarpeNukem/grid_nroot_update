using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GridNrootUpdate;

internal sealed class GitHubReleaseClient : IDisposable
{
    private readonly HttpClient httpClient = new();

    public GitHubReleaseClient()
    {
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GridNrootUpdate", "0.1"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ReleaseAssetInfo> GetLatestReleaseAssetInfoAsync(ModMapping mapping, CancellationToken cancellationToken)
    {
        var release = await GetLatestUsableReleaseAsync(mapping, cancellationToken).ConfigureAwait(false);
        var asset = release.Assets.First(a => Glob.IsMatch(a.Name, mapping.AssetPattern));
        return new ReleaseAssetInfo(NormalizeVersion(release.TagName), asset.Name, asset.BrowserDownloadUrl);
    }

    public async Task<DownloadedAsset> DownloadReleaseAssetAsync(ModMapping mapping, ReleaseAssetInfo asset, string cacheDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var targetPath = Path.Combine(cacheDirectory, $"{SanitizeFileName(mapping.Name)}-{asset.Version}-{asset.Name}");

        using var assetResponse = await httpClient.GetAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        assetResponse.EnsureSuccessStatusCode();
        await using var source = await assetResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

        return new DownloadedAsset(targetPath, asset.Version);
    }

    private async Task<GitHubRelease> GetLatestUsableReleaseAsync(ModMapping mapping, CancellationToken cancellationToken)
    {
        var releasesUri = new Uri($"https://api.github.com/repos/{ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}/releases?per_page=30");
        using var releasesResponse = await httpClient.GetAsync(releasesUri, cancellationToken).ConfigureAwait(false);
        if (releasesResponse.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"GitHub API cannot access {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}. Make the repository public, or GitHub returns 404 for unauthenticated plugin update checks.");

        releasesResponse.EnsureSuccessStatusCode();

        await using var releasesStream = await releasesResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(releasesStream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub returned an empty releases payload.");

        var release = releases
            .Where(r => !r.Draft && r.TagName.StartsWith("mod-v", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(r => r.Assets.Any(a => Glob.IsMatch(a.Name, mapping.AssetPattern)));

        return release
            ?? throw new InvalidOperationException($"No published GitHub release in {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo} has a 'mod-v*' tag with an asset matching '{mapping.AssetPattern}'.");
    }

    public void Dispose()
        => httpClient.Dispose();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static string NormalizeVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return "latest";

        if (tagName.StartsWith("mod-v", StringComparison.OrdinalIgnoreCase))
            return tagName[5..];
        if (tagName.StartsWith("mod-", StringComparison.OrdinalIgnoreCase))
            return tagName[4..];

        return tagName.TrimStart('v', 'V');
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}

internal readonly record struct DownloadedAsset(string Path, string Version);
internal readonly record struct ReleaseAssetInfo(string Version, string Name, string DownloadUrl);
