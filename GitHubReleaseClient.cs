using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public async Task<string> DownloadReleaseAssetAsync(ModMapping mapping, string cacheDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mapping.GitHubOwner) || string.IsNullOrWhiteSpace(mapping.GitHubRepo))
            throw new InvalidOperationException($"Mapping '{mapping.Name}' needs GitHubOwner and GitHubRepo configured.");

        var releaseUri = new Uri($"https://api.github.com/repos/{mapping.GitHubOwner}/{mapping.GitHubRepo}/releases/tags/{Uri.EscapeDataString(mapping.ReleaseTag)}");
        using var releaseResponse = await httpClient.GetAsync(releaseUri, cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub returned an empty release payload.");

        var asset = release.Assets.FirstOrDefault(a => Glob.IsMatch(a.Name, mapping.AssetPattern))
            ?? throw new InvalidOperationException($"Release '{mapping.ReleaseTag}' has no asset matching '{mapping.AssetPattern}'.");

        Directory.CreateDirectory(cacheDirectory);
        var targetPath = Path.Combine(cacheDirectory, $"{SanitizeFileName(mapping.Name)}-{mapping.DesiredVersion}-{asset.Name}");

        using var assetResponse = await httpClient.GetAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        assetResponse.EnsureSuccessStatusCode();
        await using var source = await assetResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

        return targetPath;
    }

    public void Dispose()
        => httpClient.Dispose();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private sealed class GitHubRelease
    {
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
