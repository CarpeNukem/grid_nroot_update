using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GridNrootUpdate;

internal sealed class NewsPost
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;
    [JsonPropertyName("eventAt")] public string EventAt { get; set; } = string.Empty;
    [JsonPropertyName("eventDiscord")] public string EventDiscord { get; set; } = string.Empty;
    [JsonPropertyName("link")] public string Link { get; set; } = string.Empty;
    [JsonPropertyName("linkLabel")] public string LinkLabel { get; set; } = string.Empty;
    [JsonPropertyName("flyerUrl")] public string FlyerUrl { get; set; } = string.Empty;
    [JsonPropertyName("flyerImage")] public string FlyerImage { get; set; } = string.Empty;

    public DateTimeOffset? EventAtUtc
        => DateTimeOffset.TryParse(EventAt, out var parsed) ? parsed.ToUniversalTime() : null;

    public DateTimeOffset PublishedAtUtc
        => DateTimeOffset.TryParse(PublishedAt, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.MinValue;

    // Only https links are accepted server-side, but the plugin re-checks before
    // handing anything to the shell: a compromised or spoofed endpoint must not
    // be able to make the client open an arbitrary URI.
    public bool HasSafeLink
        => Uri.TryCreate(Link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>Anything the relay returns carries the wire schema version.</summary>
internal interface ISchemaVersioned
{
    int SchemaVersion { get; }
}

internal sealed class NewsFeed : ISchemaVersioned
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = string.Empty;
    [JsonPropertyName("news")] public List<NewsPost> News { get; set; } = [];
}

internal sealed class RemoteProfileOptional
{
    [JsonPropertyName("pronunciation")] public string Pronunciation { get; set; } = string.Empty;
    [JsonPropertyName("pronouns")] public string Pronouns { get; set; } = string.Empty;
    [JsonPropertyName("race")] public string Race { get; set; } = string.Empty;
    [JsonPropertyName("availability")] public string Availability { get; set; } = string.Empty;
    [JsonPropertyName("quote")] public string Quote { get; set; } = string.Empty;
}

internal sealed class RemoteProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("characterName")] public string CharacterName { get; set; } = string.Empty;
    [JsonPropertyName("age")] public string Age { get; set; } = string.Empty;
    [JsonPropertyName("affiliation")] public string Affiliation { get; set; } = string.Empty;
    [JsonPropertyName("occupation")] public string Occupation { get; set; } = string.Empty;
    [JsonPropertyName("bio")] public string Bio { get; set; } = string.Empty;
    [JsonPropertyName("optional")] public RemoteProfileOptional? Optional { get; set; }
    [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; } = string.Empty;
    [JsonPropertyName("bundledImage")] public string BundledImage { get; set; } = string.Empty;
    [JsonPropertyName("logoUrl")] public string LogoUrl { get; set; } = string.Empty;
    [JsonPropertyName("logoImage")] public string LogoImage { get; set; } = string.Empty;
    [JsonPropertyName("requestLabel")] public string RequestLabel { get; set; } = string.Empty;
    [JsonPropertyName("requestMessage")] public string RequestMessage { get; set; } = string.Empty;
}

internal sealed class RemoteMenuItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("priceGil")] public int PriceGil { get; set; }
    [JsonPropertyName("priceLabel")] public string PriceLabel { get; set; } = string.Empty;
    [JsonPropertyName("ingredients")] public string Ingredients { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("taste")] public string Taste { get; set; } = string.Empty;
    [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; } = string.Empty;
    [JsonPropertyName("bundledImage")] public string BundledImage { get; set; } = string.Empty;
}

internal sealed class RemotePage
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    /// <summary>Markdown. Rendered as a subset; never as HTML.</summary>
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
}

/// <summary>The whole published catalogue in one response.</summary>
internal sealed class RemoteCatalogFeed : ISchemaVersioned
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = string.Empty;
    [JsonPropertyName("mediaRevision")] public string MediaRevision { get; set; } = string.Empty;
    [JsonPropertyName("profiles")] public List<RemoteProfile> Profiles { get; set; } = [];
    [JsonPropertyName("menu")] public List<RemoteMenuItem> Menu { get; set; } = [];
    [JsonPropertyName("news")] public List<NewsPost> News { get; set; } = [];
    [JsonPropertyName("pages")] public List<RemotePage> Pages { get; set; } = [];
}

internal enum NewsFetchOutcome
{
    Updated,
    NotModified,
    Failed,
}

internal readonly record struct NewsFetchResult(
    NewsFetchOutcome Outcome,
    NewsFeed? Feed,
    string? ETag,
    string? Error,
    string? Detail = null);

internal readonly record struct CatalogFetchResult(
    NewsFetchOutcome Outcome,
    RemoteCatalogFeed? Feed,
    string? ETag,
    string? Error,
    string? Detail = null);

/// <summary>
/// Reads the venue announcement feed from the Cyberdeck backend.
///
/// Everything here is network I/O and must never be called from a draw method.
/// The client is deliberately strict: a slow, malformed, oversized, or
/// newer-schema response is treated as a failure so the caller keeps whatever
/// good data it already had.
/// </summary>
internal sealed class BackendClient : IDisposable
{
    /// <summary>Highest wire schema this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    private const int MaxResponseBytes = 512 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;

    public BackendClient()
    {
        httpClient = new HttpClient
        {
            Timeout = RequestTimeout,
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GridNrootUpdate", "0.1"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Fetches the announcement feed, sending <paramref name="etag"/> so an
    /// unchanged feed costs a 304 and no re-parse.
    /// </summary>
    /// <summary>
    /// Fetches the whole published catalogue — profiles, menu, and news — in one
    /// request, which is what the Cyberdeck uses. One round trip that can answer
    /// 304 is cheaper than three that each might not.
    /// </summary>
    public async Task<CatalogFetchResult> FetchCatalogAsync(string baseUrl, string? etag, CancellationToken cancellationToken)
    {
        var result = await FetchAsync<RemoteCatalogFeed>(baseUrl, "/v1/catalog", etag, cancellationToken)
            .ConfigureAwait(false);

        if (result.Feed is { } feed)
        {
            feed.Profiles ??= [];
            feed.Menu ??= [];
            feed.News ??= [];
            feed.Pages ??= [];
        }

        return new CatalogFetchResult(result.Outcome, result.Feed, result.ETag, result.Error, result.Detail);
    }

    public async Task<NewsFetchResult> FetchNewsAsync(string baseUrl, string? etag, CancellationToken cancellationToken)
    {
        var result = await FetchAsync<NewsFeed>(baseUrl, "/v1/news", etag, cancellationToken).ConfigureAwait(false);
        if (result.Feed is { } feed)
            feed.News ??= [];

        return new NewsFetchResult(result.Outcome, result.Feed, result.ETag, result.Error, result.Detail);
    }

    private readonly record struct FetchResult<T>(
        NewsFetchOutcome Outcome,
        T? Feed,
        string? ETag,
        string? Error,
        string? Detail) where T : class;

    private async Task<FetchResult<T>> FetchAsync<T>(
        string baseUrl,
        string path,
        string? etag,
        CancellationToken cancellationToken) where T : class, ISchemaVersioned
    {
        if (!TryBuildUri(baseUrl, path, out var uri))
            return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, "Backend address is not a valid URL.", null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new FetchResult<T>(NewsFetchOutcome.NotModified, null, etag, null, null);

            if (!response.IsSuccessStatusCode)
                return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, $"The relay returned {(int)response.StatusCode}.", null);

            if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
                return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, "That response is too large.", null);

            var payload = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            if (payload is null)
                return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, "That response is too large.", null);

            var feed = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            if (feed is null)
                return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, "The relay sent an empty response.", null);

            // A newer schema may have changed the meaning of fields this build
            // reads, so it is refused rather than half-understood.
            if (feed.SchemaVersion > SupportedSchemaVersion)
                return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, $"The relay uses unsupported format v{feed.SchemaVersion}.", null);

            return new FetchResult<T>(NewsFetchOutcome.Updated, feed, response.Headers.ETag?.ToString(), null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, "The relay did not respond in time.", null);
        }
        catch (Exception exception)
        {
            // `Error` is a sentence for the Cyberdeck; `Detail` carries the raw
            // failure for the caller to log. Keeping the logger out of this class
            // is what lets it be exercised outside the game.
            return new FetchResult<T>(NewsFetchOutcome.Failed, null, null, Describe(exception), exception.Message);
        }
    }

    /// <summary>
    /// Turns a transport failure into readable text.
    ///
    /// "No connection could be made because the target machine actively refused
    /// it" is the operating system talking to a developer. Someone looking at
    /// the Cyberdeck wants to know the relay is not answering.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var socketError = exception as System.Net.Sockets.SocketException
                          ?? exception.InnerException as System.Net.Sockets.SocketException;

        if (socketError is not null)
        {
            return socketError.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => "The relay is not running at that address.",
                System.Net.Sockets.SocketError.HostNotFound => "That relay address could not be found.",
                System.Net.Sockets.SocketError.TimedOut => "The relay did not respond in time.",
                _ => "Could not reach the relay.",
            };
        }

        return exception is HttpRequestException
            ? "Could not reach the relay."
            : "The broadcast feed could not be read.";
    }

    /// <summary>Reads at most <see cref="MaxResponseBytes"/>, so a lying or absent Content-Length cannot exhaust memory.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new System.IO.MemoryStream();

        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > MaxResponseBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryBuildUri(string baseUrl, string path, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (!Uri.TryCreate($"{baseUrl.TrimEnd('/')}{path}", UriKind.Absolute, out var parsed))
            return false;

        // http is tolerated only for a loopback development server.
        var isLoopback = parsed.IsLoopback;
        if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && isLoopback))
            return false;

        uri = parsed;
        return true;
    }

    public void Dispose()
        => httpClient.Dispose();
}
