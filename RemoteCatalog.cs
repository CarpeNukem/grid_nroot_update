using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GridNrootUpdate;

internal enum NewsSource
{
    None,
    Remote,
    Cache,
}

internal sealed record NewsSnapshot(
    IReadOnlyList<NewsPost> Posts,
    NewsSource Source,
    DateTimeOffset? LastSync,
    string? LastError,
    bool IsRefreshing)
{
    public static readonly NewsSnapshot Empty = new([], NewsSource.None, null, null, false);

    public bool HasPosts => Posts.Count > 0;

    /// <summary>The post promoted to the home banner, if any.</summary>
    public NewsPost? Banner => Posts.FirstOrDefault(post => post.Pinned);

    public string SourceLabel => Source switch
    {
        NewsSource.Remote => "remote",
        NewsSource.Cache => "cached",
        _ => "none",
    };
}

/// <summary>
/// Keeps the venue announcement feed up to date in the background.
///
/// The Cyberdeck reads <see cref="Snapshot"/> during draw and never waits on
/// anything: fetching, parsing, and disk writes all happen on a background
/// task. A failed refresh leaves the previous snapshot in place, so losing the
/// backend degrades to stale announcements rather than an empty screen.
///
/// Announcements are the one collection with no bundled fallback — there is no
/// shipped announcements file the way there is for staff profiles — so "no
/// data" is a normal state the UI has to handle by showing nothing at all.
/// </summary>
internal sealed class NewsService : IDisposable
{
    private const string CacheFileName = "news_cache.json";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly PluginConfig config;
    private readonly BackendClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshSignal = new(0, 1);
    private readonly string cachePath;

    private volatile NewsSnapshot snapshot = NewsSnapshot.Empty;
    private string? etag;
    private Task? worker;

    public NewsService(PluginConfig config, string configDirectory)
    {
        this.config = config;
        cachePath = Path.Combine(configDirectory, CacheFileName);
    }

    public NewsSnapshot Snapshot => snapshot;

    public void Start()
    {
        LoadCache();
        worker = Task.Run(() => RunAsync(lifetime.Token));
    }

    /// <summary>Asks for an out-of-band refresh. Returns immediately.</summary>
    public void RequestRefresh()
    {
        if (refreshSignal.CurrentCount == 0)
        {
            try
            {
                refreshSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A refresh is already pending; nothing more to ask for.
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the game and the rest of the plugin settle before the first
            // request; nothing here is urgent.
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                await refreshSignal.WaitAsync(RefreshInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            PluginService.Log.Error(exception, "Announcement refresh loop stopped unexpectedly.");
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!config.BackendEnabled || string.IsNullOrWhiteSpace(config.BackendBaseUrl))
            return;

        snapshot = snapshot with { IsRefreshing = true };

        var result = await client
            .FetchNewsAsync(config.BackendBaseUrl, etag, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case NewsFetchOutcome.NotModified:
                snapshot = snapshot with
                {
                    LastSync = DateTimeOffset.UtcNow,
                    LastError = null,
                    IsRefreshing = false,
                };
                break;

            case NewsFetchOutcome.Updated when result.Feed is { } feed:
                etag = result.ETag;
                snapshot = new NewsSnapshot(feed.News, NewsSource.Remote, DateTimeOffset.UtcNow, null, false);
                SaveCache(feed, result.ETag);
                PluginService.Log.Debug("Announcements refreshed: {Count} post(s).", feed.News.Count);
                break;

            default:
                // Keep whatever is already on screen. Only the diagnostics change.
                snapshot = snapshot with { LastError = result.Error, IsRefreshing = false };
                PluginService.Log.Debug(
                    "Announcement refresh failed: {Error} ({Detail})",
                    result.Error ?? "unknown",
                    result.Detail ?? "no detail");
                break;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;

            var cached = JsonSerializer.Deserialize<CachedFeed>(File.ReadAllText(cachePath), JsonOptions);
            if (cached?.Feed is null || cached.Feed.SchemaVersion > BackendClient.SupportedSchemaVersion)
                return;

            cached.Feed.News ??= [];
            etag = cached.ETag;
            snapshot = new NewsSnapshot(cached.Feed.News, NewsSource.Cache, cached.FetchedAt, null, false);
        }
        catch (Exception exception)
        {
            // A corrupt cache is discarded, never allowed to break startup.
            PluginService.Log.Warning(exception, "Could not read the announcement cache; ignoring it.");
            etag = null;
        }
    }

    /// <summary>
    /// Replaces the cache atomically.
    ///
    /// The payload is written to a temporary file and moved into place, so an
    /// interrupted write cannot leave a half-written cache that would be
    /// discarded on the next start.
    /// </summary>
    private void SaveCache(NewsFeed feed, string? responseETag)
    {
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var payload = JsonSerializer.Serialize(
                new CachedFeed { ETag = responseETag, FetchedAt = DateTimeOffset.UtcNow, Feed = feed },
                JsonOptions);

            File.WriteAllText(temporaryPath, payload);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception exception)
        {
            PluginService.Log.Warning(exception, "Could not write the announcement cache.");

            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Nothing useful to do if cleanup also fails.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            lifetime.Cancel();
            worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Unloading; a stuck refresh must not block the plugin.
        }

        lifetime.Dispose();
        refreshSignal.Dispose();
        client.Dispose();
    }

    private sealed class CachedFeed
    {
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
        [JsonPropertyName("feed")] public NewsFeed? Feed { get; set; }
    }
}
