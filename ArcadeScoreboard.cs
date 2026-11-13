using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GridNrootUpdate;

/// <summary>
/// The arcade's shared boards as the relay last sent them, checked on the way in.
///
/// The relay is a trust boundary and these lines are drawn on screen, so a name
/// is cut down to what a cabinet can sign, six capitals, digits or dashes, and
/// anything that is not a score is dropped, whatever the relay says.
/// </summary>
internal sealed record ArcadeBoardSet(int Depth, IReadOnlyDictionary<string, IReadOnlyList<ArcadeBoardLine>> Boards)
{
    /// <summary>The relay's own depth, used when a catalogue does not say.</summary>
    private const int DefaultDepth = 10;

    private const int MaxDepth = 50;
    private const int MaxBoards = 64;
    private const int NameLength = 6;

    public static ArcadeBoardSet? From(RemoteArcade? remote)
    {
        if (remote is null)
            return null;

        var depth = remote.Depth is > 0 and <= MaxDepth ? remote.Depth : DefaultDepth;
        var boards = new Dictionary<string, IReadOnlyList<ArcadeBoardLine>>(StringComparer.Ordinal);
        foreach (var (board, lines) in remote.Boards ?? [])
        {
            if (boards.Count >= MaxBoards || string.IsNullOrEmpty(board) || lines is null)
                continue;

            boards[board] = lines
                .Where(line => line is { Score: > 0 })
                .Select(line => new ArcadeBoardLine(CleanName(line.Initials), line.Score))
                .Where(line => line.Initials.Length > 0)
                // The relay sends a board best first; sorted again here because
                // the module reads the last line as the bar a score has to clear.
                .OrderByDescending(line => line.Score)
                .Take(depth)
                .ToArray();
        }

        return new ArcadeBoardSet(depth, boards);
    }

    private static string CleanName(string? name)
        => new((name ?? string.Empty)
            .Where(letter => letter is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
            .Take(NameLength)
            .ToArray());
}

/// <summary>
/// The Grid's shared high-score boards, for the arcade module.
///
/// Built to ask as little of the relay as it can. There is no request for
/// reading: the boards arrive inside the catalogue, which the deck fetches
/// anyway, so this only ever looks at the snapshot in hand. Sitting down at a
/// cabinet asks the catalogue to refresh if it is more than ten minutes old,
/// and that is the same single request the deck's hourly check would have made,
/// not one on top of it. The only request that is the arcade's own is the post,
/// and the module makes it only for a score that would be on a board.
/// </summary>
internal sealed class ArcadeScoreboard : IArcadeScoreboard, IDisposable
{
    /// <summary>
    /// How old the boards may be when someone sits down to play.
    ///
    /// Long enough that walking down the row of cabinets is one request at
    /// most, short enough that the table on screen is the one being played
    /// against.
    /// </summary>
    private static readonly TimeSpan FreshEnough = TimeSpan.FromMinutes(10);

    /// <summary>The relay's own limit on one submission.</summary>
    private const int RelayMaxBatch = 25;

    private readonly PluginConfig config;
    private readonly CatalogService catalog;
    private readonly BackendClient client = new();
    private readonly CancellationTokenSource lifetime = new();

    public ArcadeScoreboard(PluginConfig config, CatalogService catalog)
    {
        this.config = config;
        this.catalog = catalog;
    }

    public bool Enabled => config.BackendEnabled && config.ArcadeScoresShared;

    public int Depth => catalog.Snapshot.Arcade?.Depth ?? 0;

    public int MaxBatch => RelayMaxBatch;

    public IReadOnlyList<ArcadeBoardLine>? Board(string board)
    {
        // A relay that sends no boards does not keep any, so there is nowhere
        // to post to either: null, and the module keeps to its own table.
        if (!Enabled || catalog.Snapshot.Arcade is not { } arcade)
            return null;

        return arcade.Boards.TryGetValue(board, out var lines) ? lines : [];
    }

    public void CabinetOpened()
    {
        if (Enabled)
            catalog.RequestRefreshIfOlderThan(FreshEnough);
    }

    public void Submit(IReadOnlyList<ArcadeSubmission> scores, Action<bool> answered)
    {
        if (!Enabled || scores.Count == 0 || scores.Count > RelayMaxBatch)
        {
            answered(false);
            return;
        }

        var token = lifetime.Token;
        _ = Task.Run(
            async () =>
            {
                var result = new ArcadeSubmitResult(false, 0, null);
                try
                {
                    result = await client.SubmitArcadeScoresAsync(config.BackendBaseUrl, scores, token).ConfigureAwait(false);
                    PluginService.Log.Debug(
                        "Arcade scores posted: {Count} sent, {Placed} placed, answered {Answered} ({Error}).",
                        scores.Count,
                        result.Placed,
                        result.Answered,
                        result.Error ?? "no error");
                }
                catch (OperationCanceledException)
                {
                    // Unloading. The scores are still marked as waiting, so
                    // the next session offers them again.
                    return;
                }
                catch (Exception exception)
                {
                    PluginService.Log.Debug(exception, "Could not post arcade scores.");
                }

                try
                {
                    answered(result.Answered);
                }
                catch (Exception exception)
                {
                    PluginService.Log.Debug(exception, "The arcade module could not record what the relay answered.");
                }
            },
            token);
    }

    public void Dispose()
    {
        try
        {
            lifetime.Cancel();
        }
        catch
        {
            // Unloading; nothing to do about a callback that threw.
        }

        lifetime.Dispose();
        client.Dispose();
    }
}
