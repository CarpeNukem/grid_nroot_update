using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GridNrootUpdate;

/// <summary>
/// The deck's WebSockets to the venue relay, for the arcade's games that are
/// played between decks (see <see cref="IArcadeLink"/>).
///
/// The same trust boundary as <see cref="BackendClient"/>: a socket only ever
/// goes to the venue relay over TLS, or to a loopback relay while developing.
/// The module names a route, never a host.
///
/// Keep-alives are WebSocket protocol pings. The relay's Durable Objects are
/// billed for incoming messages, and protocol pings are answered by the
/// runtime without being counted or waking the object, so an idle table costs
/// nothing to hold open. Without them Cloudflare's edge closes a socket that
/// has been quiet for about a hundred seconds.
///
/// Like BackendClient, this keeps the logger out, so it runs the same in the
/// game, in the arcade runner and in a test. What went wrong is reported
/// through <see cref="IArcadeSocket.Error"/> for the module to show.
/// </summary>
internal sealed class ArcadeRelayLink : IArcadeLink, IDisposable
{
    private static readonly string VenueRelayHost = new Uri(PluginConfig.VenueRelayUrl).Host;

    private readonly Func<string> baseUrl;
    private readonly Func<bool> enabled;
    private readonly object gate = new();
    private readonly List<RelaySocket> sockets = [];
    private bool disposed;

    public ArcadeRelayLink(PluginConfig config)
        : this(() => config.BackendBaseUrl, () => config.BackendEnabled)
    {
    }

    /// <param name="baseUrl">The relay, as <see cref="PluginConfig.BackendBaseUrl"/> holds it.</param>
    /// <param name="enabled">Whether the deck talks to the relay at all, asked on every connect.</param>
    public ArcadeRelayLink(Func<string> baseUrl, Func<bool> enabled)
    {
        this.baseUrl = baseUrl;
        this.enabled = enabled;
    }

    public bool Enabled => enabled();

    public IArcadeSocket Connect(string path, IReadOnlyDictionary<string, string> query)
    {
        if (!Enabled)
            return RelaySocket.Refused("Online play is off: the deck is not using the venue relay.");

        if (!TryBuildUri(baseUrl(), path, query, out var uri))
            return RelaySocket.Refused("That is not a relay the deck connects to.");

        lock (gate)
        {
            if (disposed)
                return RelaySocket.Refused("The deck is shutting down.");

            sockets.RemoveAll(socket => socket.State == ArcadeSocketState.Closed);
            var socket = new RelaySocket(uri);
            sockets.Add(socket);
            socket.Start();
            return socket;
        }
    }

    /// <summary>
    /// The socket address for a route: wss on the venue relay, ws only on a
    /// loopback one. Anything else, including a path that is not one of the
    /// relay's versioned routes, is refused.
    /// </summary>
    internal static bool TryBuildUri(string baseUrl, string path, IReadOnlyDictionary<string, string> query, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !path.StartsWith("/v1/", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.IndexOfAny(['?', '#', '\\', ' ']) >= 0)
            return false;

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var root) || root.AbsolutePath != "/")
            return false;

        string scheme;
        if (root.Scheme == Uri.UriSchemeHttps && (root.IsLoopback || string.Equals(root.Host, VenueRelayHost, StringComparison.OrdinalIgnoreCase)))
            scheme = "wss";
        else if (root.Scheme == Uri.UriSchemeHttp && root.IsLoopback)
            scheme = "ws";
        else
            return false;

        var builder = new UriBuilder(root)
        {
            Scheme = scheme,
            Port = root.IsDefaultPort ? -1 : root.Port,
            Path = path,
            Query = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        uri = builder.Uri;
        return true;
    }

    public void Dispose()
    {
        RelaySocket[] open;
        lock (gate)
        {
            disposed = true;
            open = [.. sockets];
            sockets.Clear();
        }

        foreach (var socket in open)
            socket.Dispose();
    }

    private sealed class RelaySocket : IArcadeSocket
    {
        /// <summary>The largest message taken from the relay. The pool hall's biggest is a few kilobytes.</summary>
        private const int MaxMessageBytes = 64 * 1024;

        /// <summary>Messages waiting either way. The module drains its inbox every frame, so reaching this means something has gone wrong.</summary>
        private const int MaxWaiting = 512;

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

        private readonly Uri? uri;
        private readonly ClientWebSocket? socket;
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentQueue<string> inbox = new();

        /// <summary>Text to send, in order, written by the draw thread and read by one send loop. Null asks the loop to close.</summary>
        private readonly Channel<string?> outbox = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });
        private int waitingToSend;

        private volatile ArcadeSocketState state;
        private volatile string? error;
        private volatile bool closing;

        private RelaySocket(string refusal)
        {
            state = ArcadeSocketState.Closed;
            error = refusal;
        }

        public RelaySocket(Uri uri)
        {
            this.uri = uri;
            state = ArcadeSocketState.Connecting;
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("User-Agent", "GridNrootUpdate");
        }

        public static RelaySocket Refused(string refusal) => new(refusal);

        public ArcadeSocketState State => state;

        public string? Error => error;

        public void Start()
            => _ = Task.Run(RunAsync);

        public bool Send(string text)
        {
            if (state != ArcadeSocketState.Open || closing)
                return false;

            if (Interlocked.Increment(ref waitingToSend) > MaxWaiting)
            {
                Interlocked.Decrement(ref waitingToSend);
                return false;
            }

            return outbox.Writer.TryWrite(text);
        }

        public bool TryReceive(out string text)
        {
            if (inbox.TryDequeue(out var next))
            {
                text = next;
                return true;
            }

            text = string.Empty;
            return false;
        }

        public void Close()
        {
            if (closing)
                return;

            closing = true;
            if (socket is null)
                return;

            outbox.Writer.TryWrite(null);
            outbox.Writer.TryComplete();
            try
            {
                lifetime.CancelAfter(CloseTimeout);
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        public void Dispose()
            => Close();

        private async Task RunAsync()
        {
            var token = lifetime.Token;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(ConnectTimeout);
                    try
                    {
                        await socket!.ConnectAsync(uri!, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        Fail("The relay did not answer in time.");
                        return;
                    }
                }

                if (closing)
                {
                    await CloseOutputAsync().ConfigureAwait(false);
                    return;
                }

                state = ArcadeSocketState.Open;
                var sending = SendLoopAsync(token);
                await ReceiveLoopAsync(token).ConfigureAwait(false);

                // The receive side has ended, by a close from either end or a
                // failure. The send loop answers a close the relay started.
                outbox.Writer.TryWrite(null);
                outbox.Writer.TryComplete();
                await Task.WhenAny(sending, Task.Delay(CloseTimeout, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!closing)
                    Fail("The connection to the relay was lost.");
            }
            catch (WebSocketException exception)
            {
                Fail(DescribeFailure(exception));
            }
            catch (Exception)
            {
                Fail("Could not reach the relay.");
            }
            finally
            {
                state = ArcadeSocketState.Closed;
                closing = true;
                outbox.Writer.TryComplete();
                socket?.Dispose();
                lifetime.Dispose();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[8 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket!.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (!closing)
                        Fail(DescribeClose(socket.CloseStatus, socket.CloseStatusDescription));
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    Fail("The relay sent more than the deck reads.");
                    return;
                }

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (inbox.Count >= MaxWaiting)
                    {
                        Fail("Messages from the relay were not being read.");
                        return;
                    }

                    inbox.Enqueue(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }

                message.SetLength(0);
            }
        }

        /// <summary>
        /// The only writer to the socket: a WebSocket takes one send at a time,
        /// and the closing frame is a send, so it comes through here too.
        /// </summary>
        private async Task SendLoopAsync(CancellationToken token)
        {
            try
            {
                await foreach (var text in outbox.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (text is null)
                    {
                        await CloseOutputAsync().ConfigureAwait(false);
                        return;
                    }

                    Interlocked.Decrement(ref waitingToSend);
                    if (socket!.State != WebSocketState.Open)
                        continue;

                    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing.
            }
            catch (Exception)
            {
                if (!closing)
                    Fail("The connection to the relay was lost.");
                CancelQuietly();
            }
        }

        private async Task CloseOutputAsync()
        {
            if (socket is not { State: WebSocketState.Open or WebSocketState.CloseReceived })
                return;

            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed by the deck.", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The relay may already be gone. Nothing is waiting on this.
            }
        }

        private void Fail(string reason)
        {
            error ??= reason;
            state = ArcadeSocketState.Closed;
            closing = true;
        }

        private void CancelQuietly()
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        private string DescribeFailure(WebSocketException exception)
        {
            var status = socket?.HttpStatusCode ?? 0;
            return status switch
            {
                HttpStatusCode.TooManyRequests => "Too many connections from here. Try again in a minute.",
                HttpStatusCode.ServiceUnavailable => "The relay is full. Try again shortly.",
                HttpStatusCode.NotFound or HttpStatusCode.UpgradeRequired => "The relay has no room for this game yet.",
                HttpStatusCode.BadRequest => "The relay turned the connection away.",
                0 when exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely => "The connection to the relay was lost.",
                0 => "Could not reach the relay.",
                _ => $"The relay answered {(int)status}.",
            };
        }

        private static string DescribeClose(WebSocketCloseStatus? status, string? description)
            => status switch
            {
                WebSocketCloseStatus.NormalClosure => "The relay closed the connection.",
                WebSocketCloseStatus.EndpointUnavailable => "The relay restarted. Connect again to carry on.",
                WebSocketCloseStatus.PolicyViolation => "The relay closed the connection: too many messages.",
                WebSocketCloseStatus.MessageTooBig => "The relay closed the connection: a message was too large.",
                (WebSocketCloseStatus)4000 => "This table was opened again somewhere else.",
                _ when !string.IsNullOrWhiteSpace(description) => $"The relay closed the connection: {description}",
                _ => "The relay closed the connection.",
            };
    }
}
