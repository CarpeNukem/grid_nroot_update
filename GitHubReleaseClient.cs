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
        return new ReleaseAssetInfo(NormalizeVersion(release.TagName), asset.Name, asset.BrowserDownloadUrl, asset.Size);
    }

    /// <summary>
    /// The cached download for a release, when a complete one is there.
    ///
    /// Complete means the file is exactly as long as GitHub says the asset is.
    /// A download that was cut short never reaches this name, but a file with
    /// the right name and the wrong length is still not trusted, and neither is
    /// one GitHub reports no size for.
    /// </summary>
    public static DownloadedAsset? TryGetCachedAsset(ModMapping mapping, ReleaseAssetInfo asset, string cacheDirectory)
    {
        var targetPath = GetCachePath(mapping, asset, cacheDirectory);
        try
        {
            var file = new FileInfo(targetPath);
            if (!file.Exists || asset.Size <= 0 || file.Length != asset.Size)
                return null;
        }
        catch (Exception)
        {
            return null;
        }

        return new DownloadedAsset(targetPath, asset.Version);
    }

    /// <param name="reuseCached">
    /// Whether a complete download already in the cache is handed back instead
    /// of fetching again. A repair passes false: the point of a repair is a
    /// fresh copy.
    /// </param>
    public async Task<DownloadedAsset> DownloadReleaseAssetAsync(
        ModMapping mapping,
        ReleaseAssetInfo asset,
        string cacheDirectory,
        bool reuseCached,
        Action<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        if (reuseCached && TryGetCachedAsset(mapping, asset, cacheDirectory) is { } cached)
        {
            progress?.Invoke(new DownloadProgress(asset.Size, asset.Size));
            return cached;
        }

        var targetPath = GetCachePath(mapping, asset, cacheDirectory);
        var partialPath = $"{targetPath}.{Guid.NewGuid():N}.partial";

        try
        {
            using var assetResponse = await httpClient.GetAsync(
                asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            assetResponse.EnsureSuccessStatusCode();

            var totalBytes = assetResponse.Content.Headers.ContentLength;
            var bytesDownloaded = 0L;
            progress?.Invoke(new DownloadProgress(bytesDownloaded, totalBytes));

            await using var source = await assetResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                        break;

                    await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    bytesDownloaded += bytesRead;
                    progress?.Invoke(new DownloadProgress(bytesDownloaded, totalBytes));
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (totalBytes is { } expectedBytes && bytesDownloaded != expectedBytes)
                throw new IOException($"Downloaded {bytesDownloaded} bytes for '{asset.Name}', expected {expectedBytes}.");

            File.Move(partialPath, targetPath, overwrite: true);
            return new DownloadedAsset(targetPath, asset.Version);
        }
        catch
        {
            try
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch
            {
                // Preserve the original download failure if cleanup is also blocked.
            }
            throw;
        }
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

        // Highest release number, not first in the list. GitHub orders releases
        // by creation time, several tags published in one go share it, and a
        // re-published older tag would otherwise win over the current one.
        var release = releases
            .Where(r => !r.Draft && r.TagName.StartsWith("mod-v", StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Assets.Any(a => Glob.IsMatch(a.Name, mapping.AssetPattern)))
            .Select((r, index) => (Release: r, Index: index, Version: ParseTagVersion(r.TagName)))
            .OrderByDescending(entry => entry.Version.HasValue)
            .ThenByDescending(entry => entry.Version ?? default)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Release)
            .FirstOrDefault();

        return release
            ?? throw new InvalidOperationException($"No published GitHub release in {ModMapping.FixedGitHubOwner}/{ModMapping.FixedGitHubRepo} has a 'mod-v*' tag with an asset matching '{mapping.AssetPattern}'.");
    }

    public void Dispose()
        => httpClient.Dispose();

    private static string GetCachePath(ModMapping mapping, ReleaseAssetInfo asset, string cacheDirectory)
        => Path.Combine(cacheDirectory, $"{SanitizeFileName(mapping.Name)}-{asset.Version}-{asset.Name}");

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static ModVersion? ParseTagVersion(string tagName)
        => ModVersion.TryParse(NormalizeVersion(tagName), out var version) ? version : null;

    private static string NormalizeVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return "latest";

        var normalized = tagName.Trim();
        if (normalized.StartsWith("mod-v", StringComparison.OrdinalIgnoreCase))
            return normalized[5..].Trim();
        if (normalized.StartsWith("mod-", StringComparison.OrdinalIgnoreCase))
            return normalized[4..].Trim();

        return normalized.TrimStart('v', 'V').Trim();
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

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

internal readonly record struct DownloadedAsset(string Path, string Version);
internal readonly record struct ReleaseAssetInfo(string Version, string Name, string DownloadUrl, long Size);
internal readonly record struct DownloadProgress(long BytesDownloaded, long? TotalBytes);
