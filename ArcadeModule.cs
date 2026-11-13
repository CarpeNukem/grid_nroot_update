using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// The arcade module implements the internal contract below and reuses the
// deck's theme, so it is let in as a friend rather than widening the
// plugin's public surface.
[assembly: InternalsVisibleTo("GridArcade")]

namespace GridNrootUpdate;

/// <summary>
/// The arcade games, as the deck sees them.
///
/// The games are a separate assembly, GridArcade.dll, built from a private
/// repository and shipped in the release package beside this plugin. This
/// repository holds only this contract and the loader, so it builds and runs
/// without the module: the arcade cabinets then simply have no zones.
/// </summary>
internal interface IGridArcade : IDisposable
{
    /// <summary>The name shown on a cabinet's marker, or null when that cabinet runs nothing.</summary>
    string? CabinetTitle(int cabinet);

    bool IsOpen { get; }

    void Open(int cabinet);

    void Close();

    /// <summary>Called every frame from the deck's own draw, for the arcade's windows.</summary>
    void Draw();
}

/// <summary>A drink on the venue's menu, as the arcade sees it.</summary>
/// <param name="Price">The price as the menu prints it, such as "10 000".</param>
/// <param name="Ingredients">The menu's ingredient line, comma separated, exactly as guests read it.</param>
internal sealed record ArcadeDrink(string Name, string Price, string Ingredients);

/// <summary>A line on one of The Grid's shared high-score boards: the name signed at a cabinet, and the score.</summary>
internal readonly record struct ArcadeBoardLine(string Initials, int Score);

/// <summary>A score the module wants on a shared board.</summary>
/// <param name="Id">The module's own id for this score, 32 hex characters, so the relay recognises one sent twice.</param>
/// <param name="Board">The board it belongs on: the game's id.</param>
internal sealed record ArcadeSubmission(string Id, string Board, string Initials, int Score);

/// <summary>
/// The Grid's shared high-score boards, as the arcade module sees them.
///
/// The deck owns the network, so the module asks here rather than calling the
/// relay itself. Two rules keep this cheap for the relay, and the module is
/// expected to keep to them: the boards arrive with the catalogue the deck
/// already fetches, so reading one never causes a request; and a score is
/// submitted only when it would make its board, several at once if several
/// are waiting.
/// </summary>
internal interface IArcadeScoreboard
{
    /// <summary>Whether this deck shares scores at all. When false, nothing is shown from the relay and nothing may be submitted.</summary>
    bool Enabled { get; }

    /// <summary>How many scores a board keeps. A score makes a board that holds fewer, or whose last line it beats.</summary>
    int Depth { get; }

    /// <summary>The most scores one <see cref="Submit"/> may carry.</summary>
    int MaxBatch { get; }

    /// <summary>
    /// A board, best first, as the deck last received it. Empty when nobody is
    /// on it yet; null when the deck has no boards at all, because sharing is
    /// off or the relay has never been reached, in which case the module shows
    /// its own table and submits nothing.
    /// </summary>
    IReadOnlyList<ArcadeBoardLine>? Board(string board);

    /// <summary>Tells the deck someone sat down at a cabinet, so it can bring the boards up to date if its copy is old. Returns at once.</summary>
    void CabinetOpened();

    /// <summary>
    /// Sends scores to the relay in one request. Returns at once.
    /// <paramref name="answered"/> is called later, off the draw thread: true
    /// when the relay took a decision on them, placed or not, and they must
    /// not be sent again; false when it could not be reached and they should
    /// be offered again another time.
    /// </summary>
    void Submit(IReadOnlyList<ArcadeSubmission> scores, Action<bool> answered);
}

/// <summary>What the deck hands the arcade module when it loads it.</summary>
/// <param name="DataDirectory">The deck's own config folder, for anything the module keeps between sessions, such as high scores.</param>
/// <param name="Menu">
/// The drinks card the deck is showing right now: the relay's when it has
/// loaded, the bundled one otherwise. A function rather than a list, so a game
/// asks when it starts and gets tonight's menu.
/// </param>
/// <param name="Scoreboard">The Grid's shared high-score boards. Null on a deck that has none, where scores stay on this machine.</param>
internal sealed record ArcadeHost(
    PluginConfig Config,
    string DataDirectory,
    Func<IReadOnlyList<ArcadeDrink>>? Menu = null,
    IArcadeScoreboard? Scoreboard = null);

internal static class ArcadeModule
{
    internal const string FileName = "GridArcade.dll";

    /// <summary>
    /// Loads the module from beside the plugin, if it is there.
    ///
    /// The file is read into memory and loaded into the plugin's own load
    /// context. That context is where the module finds this assembly, so the
    /// contract it implements is the same type the deck asks for; and loading
    /// from bytes leaves the file unlocked, so it can be rebuilt while the
    /// game runs, the same way Dalamud loads dev plugins.
    /// </summary>
    public static IGridArcade? TryLoad(string? directory, ArcadeHost host, out string status)
    {
        if (string.IsNullOrEmpty(directory))
        {
            status = "not installed (plugin folder unknown)";
            return null;
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            status = "not installed";
            return null;
        }

        try
        {
            var context = AssemblyLoadContext.GetLoadContext(typeof(ArcadeModule).Assembly) ?? AssemblyLoadContext.Default;
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var assembly = context.LoadFromStream(stream);
            var type = assembly.GetTypes().FirstOrDefault(candidate =>
                typeof(IGridArcade).IsAssignableFrom(candidate) && candidate is { IsAbstract: false, IsInterface: false });
            if (type is null)
            {
                status = $"{FileName} has no arcade in it";
                return null;
            }

            var arcade = (IGridArcade)Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [host],
                culture: null)!;
            status = $"loaded {assembly.GetName().Version}";
            return arcade;
        }
        catch (Exception exception)
        {
            // A module built against an older deck can fail here. The deck
            // carries on without the arcade rather than failing to load.
            PluginService.Log.Warning(exception, "Could not load the arcade module from {Path}.", path);
            status = $"failed to load: {exception.GetBaseException().Message}";
            return null;
        }
    }
}
