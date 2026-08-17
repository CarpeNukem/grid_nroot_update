using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GridNrootUpdate;

/// <summary>
/// How a remote asset can be presented.
/// </summary>
internal enum RemoteAssetKind
{
    /// <summary>A still image the deck can draw itself.</summary>
    Still,

    /// <summary>GIF or MP4. Downloaded and verified, but opened externally to view.</summary>
    Animated,
}

internal sealed record RemoteAsset(string Url, string LocalPath, RemoteAssetKind Kind);

/// <summary>
/// Downloads and stores remote flyer art.
///
/// Assets are content-addressed: the backend names every object after the
/// SHA-256 of its bytes, so the digest is right there in the URL. That is what
/// makes verification cheap and worthwhile — a download whose hash does not
/// match the name it was fetched under is discarded rather than shown.
///
/// Everything here runs off the draw thread. <see cref="TryGet"/> is what the
/// UI calls: it never blocks, never starts a download during a frame beyond
/// queueing one, and returns null until the file is on disk and verified.
/// </summary>
internal sealed class RemoteAssetCache : IDisposable
{
    private const long MaxAssetBytes = 12 * 1024 * 1024;
    private const long MaxCacheBytes = 96 * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> StillExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    private static readonly HashSet<string> AnimatedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".gif", ".mp4" };

    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string cacheDirectory;

    /// <summary>Verified assets, keyed by URL.</summary>
    private readonly ConcurrentDictionary<string, RemoteAsset> ready = new(StringComparer.Ordinal);

    /// <summary>URLs currently downloading or known bad, so neither is retried in a loop.</summary>
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> failed = new(StringComparer.Ordinal);

    public RemoteAssetCache(string configDirectory)
    {
        cacheDirectory = Path.Combine(configDirectory, "media");
        httpClient = new HttpClient { Timeout = DownloadTimeout };
    }

    /// <summary>
    /// Returns a verified asset, or null while it is missing.
    ///
    /// Safe to call every frame: a miss queues one background download and
    /// returns immediately.
    /// </summary>
    public RemoteAsset? TryGet(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (ready.TryGetValue(url, out var asset))
            return asset;

        if (failed.ContainsKey(url) || !inFlight.TryAdd(url, 0))
            return null;

        _ = Task.Run(() => DownloadAsync(url, lifetime.Token));
        return null;
    }

    /// <summary>True when the URL points at something the deck cannot draw inline.</summary>
    public static bool IsAnimated(string url)
        => AnimatedExtensions.Contains(ExtensionOf(url));

    /// <summary>Loads whatever is already cached, so a restart does not re-download.</summary>
    public void LoadExisting()
    {
        try
        {
            if (!Directory.Exists(cacheDirectory))
                return;

            // Files are named by digest, so the URL cannot be recovered from disk.
            // They are left in place and adopted as each URL is requested again.
            var count = Directory.EnumerateFiles(cacheDirectory).Count();
            PluginService.Log.Debug("Remote media cache holds {Count} file(s).", count);
        }
        catch (Exception exception)
        {
            PluginService.Log.Warning(exception, "Could not inspect the remote media cache.");
        }
    }

    private async Task DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var expectedDigest = DigestFromUrl(url);
            if (expectedDigest is null)
            {
                // Without a digest in the name there is nothing to verify against,
                // so the file is not trusted at all.
                MarkFailed(url, "asset URL carries no content hash");
                return;
            }

            var extension = ExtensionOf(url);
            var kind = StillExtensions.Contains(extension)
                ? RemoteAssetKind.Still
                : AnimatedExtensions.Contains(extension)
                    ? RemoteAssetKind.Animated
                    : (RemoteAssetKind?)null ?? RemoteAssetKind.Still;

            if (!StillExtensions.Contains(extension) && !AnimatedExtensions.Contains(extension))
            {
                MarkFailed(url, $"unsupported asset type '{extension}'");
                return;
            }

            Directory.CreateDirectory(cacheDirectory);
            var targetPath = Path.Combine(cacheDirectory, $"{expectedDigest}{extension}");

            if (File.Exists(targetPath) && await VerifyFileAsync(targetPath, expectedDigest, cancellationToken).ConfigureAwait(false))
            {
                Publish(url, targetPath, kind);
                return;
            }

            var bytes = await FetchBoundedAsync(url, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                MarkFailed(url, "asset was unavailable or too large");
                return;
            }

            var actualDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            {
                // The bytes are not what the name promised. Something rewrote them
                // in transit, or the relay is not what it claims to be.
                MarkFailed(url, "asset content did not match its hash");
                return;
            }

            WriteAtomically(targetPath, bytes);
            Publish(url, targetPath, kind);
            PruneCache();
        }
        catch (OperationCanceledException)
        {
            inFlight.TryRemove(url, out _);
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Remote asset download failed for {Url}.", url);
            MarkFailed(url, exception.Message);
        }
    }

    private async Task<byte[]?> FetchBoundedAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxAssetBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > MaxAssetBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<bool> VerifyFileAsync(string path, string expectedDigest, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > MaxAssetBytes)
                return false;

            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(digest).ToLowerInvariant(), expectedDigest, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomically(string targetPath, byte[] bytes)
    {
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Nothing useful to do if cleanup also fails.
            }

            throw;
        }
    }

    private void Publish(string url, string path, RemoteAssetKind kind)
    {
        ready[url] = new RemoteAsset(url, path, kind);
        inFlight.TryRemove(url, out _);
    }

    private void MarkFailed(string url, string reason)
    {
        failed[url] = 0;
        inFlight.TryRemove(url, out _);
        PluginService.Log.Debug("Remote asset rejected ({Reason}): {Url}", reason, url);
    }

    /// <summary>Drops the oldest files once the cache grows past its budget.</summary>
    private void PruneCache()
    {
        try
        {
            var files = new DirectoryInfo(cacheDirectory)
                .EnumerateFiles()
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            var total = 0L;
            foreach (var file in files)
            {
                total += file.Length;
                if (total <= MaxCacheBytes)
                    continue;

                var stale = ready.FirstOrDefault(entry =>
                    string.Equals(entry.Value.LocalPath, file.FullName, StringComparison.OrdinalIgnoreCase));
                if (stale.Key is not null)
                    ready.TryRemove(stale.Key, out _);

                file.Delete();
            }
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not prune the remote media cache.");
        }
    }

    /// <summary>The digest the backend put in the object name, if it is there.</summary>
    private static string? DigestFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (name.Length != 64 || !name.All(Uri.IsHexDigit))
            return null;

        return name.ToLowerInvariant();
    }

    private static string ExtensionOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath)
            : string.Empty;

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        httpClient.Dispose();
    }
}
