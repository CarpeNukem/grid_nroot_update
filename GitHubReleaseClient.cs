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

    public async Task<DownloadedAsset> DownloadLatestReleaseAssetAsync(ModMapping mapping, string cacheDirectory, CancellationToken cancellationToken)
    {
        var releaseUri = new Uri($"https://api.github.com/repos/{ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}/releases/latest");
        using var releaseResponse = await httpClient.GetAsync(releaseUri, cancellationToken).ConfigureAwait(false);
        if (releaseResponse.StatusCode == HttpStatusCode.NotFound)
            return await DownloadRepositoryAssetAsync(mapping, cacheDirectory, cancellationToken).ConfigureAwait(false);

        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub returned an empty release payload.");

        var asset = release.Assets.FirstOrDefault(a => Glob.IsMatch(a.Name, mapping.AssetPattern))
            ?? throw new InvalidOperationException($"Latest release '{release.TagName}' has no asset matching '{mapping.AssetPattern}'.");

        Directory.CreateDirectory(cacheDirectory);
        var version = NormalizeVersion(release.TagName);
        var targetPath = Path.Combine(cacheDirectory, $"{SanitizeFileName(mapping.Name)}-{version}-{asset.Name}");

        using var assetResponse = await httpClient.GetAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        assetResponse.EnsureSuccessStatusCode();
        await using var source = await assetResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

        return new DownloadedAsset(targetPath, version, false);
    }

    private async Task<DownloadedAsset> DownloadRepositoryAssetAsync(ModMapping mapping, string cacheDirectory, CancellationToken cancellationToken)
    {
        var contentsUri = new Uri($"https://api.github.com/repos/{ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo}/contents/{ModMapping.FixedAssetFolder}?ref={ModMapping.FixedGitHubBranch}");
        using var contentsResponse = await httpClient.GetAsync(contentsUri, cancellationToken).ConfigureAwait(false);
        contentsResponse.EnsureSuccessStatusCode();

        await using var contentsStream = await contentsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var contents = await JsonSerializer.DeserializeAsync<List<GitHubContent>>(contentsStream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub returned an empty contents payload.");

        var asset = contents.FirstOrDefault(a => a.Type == "file" && Glob.IsMatch(a.Name, mapping.AssetPattern))
            ?? throw new InvalidOperationException($"No file in '{ModMapping.FixedAssetFolder}' on '{ModMapping.FixedGitHubBranch}' matches '{mapping.AssetPattern}'.");

        if (string.IsNullOrWhiteSpace(asset.DownloadUrl))
            throw new InvalidOperationException($"GitHub did not provide a download URL for '{asset.Name}'.");

        Directory.CreateDirectory(cacheDirectory);
        var targetPath = Path.Combine(cacheDirectory, $"{SanitizeFileName(mapping.Name)}-{ModMapping.FixedGitHubBranch}-{asset.Name}");

        using var assetResponse = await httpClient.GetAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        assetResponse.EnsureSuccessStatusCode();
        await using var source = await assetResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

        return new DownloadedAsset(targetPath, ModMapping.FixedGitHubBranch, true);
    }

    public void Dispose()
        => httpClient.Dispose();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static string NormalizeVersion(string tagName)
        => string.IsNullOrWhiteSpace(tagName) ? "latest" : tagName.TrimStart('v', 'V');

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

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

    private sealed class GitHubContent
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;
    }
}

internal readonly record struct DownloadedAsset(string Path, string Version, bool FromRepositoryFallback);
