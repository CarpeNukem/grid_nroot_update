using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GridNrootUpdate;

internal enum CatalogSource
{
    None,
    Remote,
    Cache,
}

internal sealed record CatalogSnapshot(
    IReadOnlyList<RemoteProfile> Profiles,
    IReadOnlyList<RemoteMenuItem> Menu,
    IReadOnlyList<NewsPost> News,
    IReadOnlyList<RemotePage> Pages,
    CatalogSource Source,
    DateTimeOffset? LastSync,
    string? LastError,
    bool IsRefreshing)
{
    public static readonly CatalogSnapshot Empty =
        new([], [], [], [], CatalogSource.None, null, null, false);

    /// <summary>Announcements, under the name the broadcast UI reads them by.</summary>
    public IReadOnlyList<NewsPost> Posts => News;

    public bool HasPosts => News.Count > 0;

    /// <summary>
    /// True when the relay has actually delivered a catalogue.
    ///
    /// Only then do the remote profile and menu lists replace the bundled ones;
    /// an empty list from a reachable relay is a real answer ("nothing is
    /// published"), while never having reached it is not.
    /// </summary>
    public bool IsLoaded => Source != CatalogSource.None;

    /// <summary>The post promoted to the home banner, if any.</summary>
    public NewsPost? Banner => News.FirstOrDefault(post => post.Pinned);

    public string SourceLabel => Source switch
    {
        CatalogSource.Remote => "remote",
        CatalogSource.Cache => "cached",
        _ => "none",
    };
}

/// <summary>
/// Keeps the published catalogue — profiles, drinks, and announcements — up to
/// date in the background.
///
/// The Cyberdeck reads <see cref="Snapshot"/> during draw and never waits on
/// anything: fetching, parsing, and disk writes all happen on a background
/// task. A failed refresh leaves the previous snapshot in place, so losing the
/// relay degrades to stale content rather than an empty screen.
///
/// When a catalogue has been loaded it *replaces* the bundled profiles and
/// drinks rather than merging with them, so what an editor sees in the admin
/// tool is exactly what the deck shows. Bundled data is the fallback for having
/// never reached the relay at all — not a floor that remote edits sit on top of.
/// </summary>
internal sealed class CatalogService : IDisposable
{
    private const string CacheFileName = "catalog_cache.json";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often the catalogue is rechecked.
    ///
    /// A minute rather than something longer because an unchanged check is
    /// almost free: the request carries an ETag, so the relay answers 304 with
    /// no body and never touches the database. Measured at roughly 900 bytes of
    /// headers against 6 KB for a full fetch. Sixty checks an hour is one
    /// request per minute, comfortably inside the relay's own 60/minute limit
    /// even with a busy venue, and it means an edit made in the admin tool
    /// reaches the deck within a minute rather than fifteen.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

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

    private volatile CatalogSnapshot snapshot = CatalogSnapshot.Empty;
    private string? etag;
    private Task? worker;

    public CatalogService(PluginConfig config, string configDirectory)
    {
        this.config = config;
        cachePath = Path.Combine(configDirectory, CacheFileName);
    }

    public CatalogSnapshot Snapshot => snapshot;

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
            PluginService.Log.Error(exception, "Catalogue refresh loop stopped unexpectedly.");
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!config.BackendEnabled || string.IsNullOrWhiteSpace(config.BackendBaseUrl))
            return;

        snapshot = snapshot with { IsRefreshing = true };

        var result = await client
            .FetchCatalogAsync(config.BackendBaseUrl, etag, cancellationToken)
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
                snapshot = new CatalogSnapshot(
                    feed.Profiles,
                    feed.Menu,
                    feed.News,
                    feed.Pages,
                    CatalogSource.Remote,
                    DateTimeOffset.UtcNow,
                    null,
                    false);
                SaveCache(feed, result.ETag);
                PluginService.Log.Debug(
                    "Catalogue refreshed: {Profiles} profile(s), {Menu} drink(s), {News} post(s).",
                    feed.Profiles.Count,
                    feed.Menu.Count,
                    feed.News.Count);
                break;

            default:
                // Keep whatever is already on screen. Only the diagnostics change.
                snapshot = snapshot with { LastError = result.Error, IsRefreshing = false };
                PluginService.Log.Debug(
                    "Catalogue refresh failed: {Error} ({Detail})",
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

            var cached = JsonSerializer.Deserialize<CachedCatalog>(File.ReadAllText(cachePath), JsonOptions);
            if (cached?.Feed is null || cached.Feed.SchemaVersion > BackendClient.SupportedSchemaVersion)
                return;

            cached.Feed.Profiles ??= [];
            cached.Feed.Menu ??= [];
            cached.Feed.News ??= [];
            cached.Feed.Pages ??= [];
            etag = cached.ETag;
            snapshot = new CatalogSnapshot(
                cached.Feed.Profiles,
                cached.Feed.Menu,
                cached.Feed.News,
                cached.Feed.Pages,
                CatalogSource.Cache,
                cached.FetchedAt,
                null,
                false);
        }
        catch (Exception exception)
        {
            // A corrupt cache is discarded, never allowed to break startup.
            PluginService.Log.Warning(exception, "Could not read the catalogue cache; ignoring it.");
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
    private void SaveCache(RemoteCatalogFeed feed, string? responseETag)
    {
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var payload = JsonSerializer.Serialize(
                new CachedCatalog { ETag = responseETag, FetchedAt = DateTimeOffset.UtcNow, Feed = feed },
                JsonOptions);

            File.WriteAllText(temporaryPath, payload);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception exception)
        {
            PluginService.Log.Warning(exception, "Could not write the catalogue cache.");

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

    private sealed class CachedCatalog
    {
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
        [JsonPropertyName("feed")] public RemoteCatalogFeed? Feed { get; set; }
    }
}
