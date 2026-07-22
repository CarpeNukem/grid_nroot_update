using System;
using System.Collections.Generic;
using System.Linq;

namespace GridNrootUpdate;

internal enum IntrusionDifficulty
{
    Casual,
    Standard,
    BlackIce,
}

internal enum IntrusionPhase
{
    Ready,
    Countdown,
    Playing,
    Success,
    BufferLocked,
    TimedOut,
}

internal readonly record struct IntrusionCell(int Row, int Column);

internal sealed record IntrusionObjective(string Label, IReadOnlyList<string> Sequence);

/// <summary>
/// Pure state and generation logic for the hidden row/column intrusion puzzle.
/// Rendering and persistence remain outside this type.
/// </summary>
internal sealed class IntrusionGame
{
    private static readonly string[] TokenPool = ["N7", "A3", "FF", "K2", "E9", "1X", "C4", "V0", "R8", "D6", "B5", "Q1", "7E", "0D"];
    private static readonly string[] ObjectiveLabels = ["DISABLE ICE", "OPEN GHOST PORT", "ROOT OVERRIDE", "SPOOF TRACE"];

    private readonly List<string> buffer = [];
    private readonly List<IntrusionCell> selections = [];
    private readonly HashSet<IntrusionCell> selectedCells = [];
    private readonly long countdownStartedAt;
    private long startedAt;
    private long finishedAt;

    private IntrusionGame(
        IntrusionDifficulty difficulty,
        string[,] grid,
        IReadOnlyList<IntrusionObjective> objectives,
        int bufferCapacity,
        int? timeLimitSeconds,
        int optimalBufferLength,
        long now)
    {
        Difficulty = difficulty;
        Grid = grid;
        Objectives = objectives;
        BufferCapacity = bufferCapacity;
        TimeLimitSeconds = timeLimitSeconds;
        OptimalBufferLength = optimalBufferLength;
        if (difficulty == IntrusionDifficulty.BlackIce)
        {
            Phase = IntrusionPhase.Countdown;
            countdownStartedAt = now;
        }
    }

    public IntrusionDifficulty Difficulty { get; }
    public IntrusionPhase Phase { get; private set; } = IntrusionPhase.Ready;
    public string[,] Grid { get; }
    public IReadOnlyList<IntrusionObjective> Objectives { get; }
    public IReadOnlyList<string> Buffer => buffer;
    public IReadOnlyList<IntrusionCell> Selections => selections;
    public int GridSize => Grid.GetLength(0);
    public int BufferCapacity { get; }
    public int? TimeLimitSeconds { get; }
    public int OptimalBufferLength { get; }
    public int CountdownSeconds => Difficulty == IntrusionDifficulty.BlackIce ? 2 : 0;
    public int FinalScore { get; private set; }
    public bool IsTerminal => Phase is IntrusionPhase.Success or IntrusionPhase.BufferLocked or IntrusionPhase.TimedOut;
    public int CompletedObjectiveCount => Objectives.Count(IsObjectiveComplete);
    public bool AllObjectivesCompleted => CompletedObjectiveCount == Objectives.Count;
    public bool UsedOptimalBuffer => Phase == IntrusionPhase.Success && buffer.Count == OptimalBufferLength;

    public static IntrusionGame Create(IntrusionDifficulty difficulty, long now = 0)
    {
        var (gridSize, bufferCapacity, timeLimitSeconds) = difficulty switch
        {
            IntrusionDifficulty.Casual => (5, 8, (int?)null),
            IntrusionDifficulty.BlackIce => (8, 12, 18),
            _ => (6, 8, 45),
        };

        if (difficulty == IntrusionDifficulty.BlackIce)
            return CreateBlackIce(gridSize, bufferCapacity, timeLimitSeconds.GetValueOrDefault(), now);

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var grid = CreateRandomGrid(gridSize);
            var solutionPath = GenerateSelectionPath(gridSize, bufferCapacity);
            if (solutionPath is null)
                continue;

            var solutionTokens = new List<string>(bufferCapacity);
            foreach (var cell in solutionPath)
            {
                var token = TokenPool[Random.Shared.Next(TokenPool.Length)];
                grid[cell.Row, cell.Column] = token;
                solutionTokens.Add(token);
            }

            var objectives = difficulty == IntrusionDifficulty.Standard
                ? new List<IntrusionObjective>
                {
                    new(ObjectiveLabels[0], solutionTokens.GetRange(0, 3)),
                    new(ObjectiveLabels[1], solutionTokens.GetRange(2, 4)),
                    new(ObjectiveLabels[2], solutionTokens.GetRange(4, 4)),
                }
                : new List<IntrusionObjective>
                {
                    new(ObjectiveLabels[0], solutionTokens.GetRange(0, 3)),
                    new(ObjectiveLabels[1], solutionTokens.GetRange(2, 3)),
                    new(ObjectiveLabels[2], solutionTokens.GetRange(5, 3)),
                };

            if (ValidateGeneratedPuzzle(gridSize, bufferCapacity, solutionPath, solutionTokens, objectives))
                return new IntrusionGame(difficulty, grid, objectives, bufferCapacity, timeLimitSeconds, bufferCapacity, now);
        }

        throw new InvalidOperationException("Could not generate a valid intrusion puzzle.");
    }

    public string GetToken(int row, int column)
        => Grid[row, column];

    public bool IsCellSelected(int row, int column)
        => selectedCells.Contains(new IntrusionCell(row, column));

    public bool CanSelect(int row, int column)
    {
        if (Phase == IntrusionPhase.Countdown || IsTerminal || buffer.Count >= BufferCapacity || IsCellSelected(row, column))
            return false;
        if (row < 0 || row >= GridSize || column < 0 || column >= GridSize)
            return false;
        if (selections.Count == 0)
            return row == 0;

        var previous = selections[^1];
        return selections.Count % 2 == 1
            ? column == previous.Column
            : row == previous.Row;
    }

    public bool Select(int row, int column, long now)
    {
        Tick(now);
        if (!CanSelect(row, column))
            return false;

        if (Phase == IntrusionPhase.Ready)
        {
            Phase = IntrusionPhase.Playing;
            startedAt = now;
        }

        var cell = new IntrusionCell(row, column);
        selectedCells.Add(cell);
        selections.Add(cell);
        buffer.Add(Grid[row, column]);

        if (AllObjectivesCompleted)
            Finish(IntrusionPhase.Success, now);
        else if (buffer.Count >= BufferCapacity)
            Finish(IntrusionPhase.BufferLocked, now);
        else if (!HasLegalSelection())
            Finish(IntrusionPhase.BufferLocked, now);

        return true;
    }

    public void Tick(long now)
    {
        if (Phase == IntrusionPhase.Countdown)
        {
            var countdownDuration = CountdownSeconds * 1000L;
            if (now - countdownStartedAt < countdownDuration)
                return;

            Phase = IntrusionPhase.Playing;
            startedAt = countdownStartedAt + countdownDuration;
        }

        if (Phase != IntrusionPhase.Playing || TimeLimitSeconds is not { } timeLimit)
            return;

        if (now - startedAt >= timeLimit * 1000L)
            Finish(IntrusionPhase.TimedOut, now);
    }

    public int GetRemainingSeconds(long now)
    {
        if (TimeLimitSeconds is not { } timeLimit)
            return 0;
        if (Phase is IntrusionPhase.Ready or IntrusionPhase.Countdown)
            return timeLimit;

        var end = IsTerminal ? finishedAt : now;
        var elapsedSeconds = Math.Max(0, (end - startedAt) / 1000d);
        return Math.Max(0, (int)Math.Ceiling(timeLimit - elapsedSeconds));
    }

    public float GetRemainingFraction(long now)
    {
        if (TimeLimitSeconds is not { } timeLimit)
            return 1f;
        return Math.Clamp(GetRemainingSeconds(now) / (float)timeLimit, 0f, 1f);
    }

    public int GetCountdownRemainingSeconds(long now)
    {
        if (Phase != IntrusionPhase.Countdown)
            return 0;

        var elapsed = Math.Max(0, now - countdownStartedAt);
        return Math.Max(0, (int)Math.Ceiling(CountdownSeconds - (elapsed / 1000d)));
    }

    public int GetCurrentScore(long now)
        => IsTerminal ? FinalScore : CalculateScore(now);

    public string GetSelectionHint()
    {
        if (selections.Count == 0)
            return "SELECT FROM TOP ROW";

        var previous = selections[^1];
        return selections.Count % 2 == 1
            ? $"SELECT FROM COLUMN {previous.Column + 1}"
            : $"SELECT FROM ROW {previous.Row + 1}";
    }

    public bool IsObjectiveComplete(IntrusionObjective objective)
        => ContainsSequence(buffer, objective.Sequence);

    public int GetObjectivePrefixLength(IntrusionObjective objective)
    {
        if (IsObjectiveComplete(objective))
            return objective.Sequence.Count;

        var maximum = Math.Min(buffer.Count, objective.Sequence.Count - 1);
        for (var length = maximum; length > 0; length--)
        {
            var bufferStart = buffer.Count - length;
            var matches = true;
            for (var index = 0; index < length; index++)
            {
                if (string.Equals(buffer[bufferStart + index], objective.Sequence[index], StringComparison.Ordinal))
                    continue;
                matches = false;
                break;
            }

            if (matches)
                return length;
        }

        return 0;
    }

    private void Finish(IntrusionPhase phase, long now)
    {
        Phase = phase;
        finishedAt = now;
        FinalScore = CalculateScore(now);
    }

    private int CalculateScore(long now)
    {
        if (Difficulty == IntrusionDifficulty.BlackIce)
        {
            var blackIceScore = CompletedObjectiveCount * 1200;
            if (AllObjectivesCompleted)
            {
                blackIceScore += 2000; // Full breach.
                blackIceScore += 600;  // Resolve the overlapping daemons.
                blackIceScore += Math.Max(0, BufferCapacity - buffer.Count) * 600;
                if (buffer.Count == OptimalBufferLength)
                    blackIceScore += 1500;
                if (Phase != IntrusionPhase.Countdown)
                    blackIceScore += GetRemainingSeconds(now) * 75;
            }

            return (int)Math.Round(blackIceScore * 1.25, MidpointRounding.AwayFromZero);
        }

        var completed = CompletedObjectiveCount;
        var score = completed * 1400;
        if (completed > 1)
            score += (completed - 1) * 500;
        if (completed == Objectives.Count)
            score += 1800;

        if (AllObjectivesCompleted)
        {
            score += Math.Max(0, BufferCapacity - buffer.Count) * 350;
            if (TimeLimitSeconds is not null && Phase != IntrusionPhase.Ready)
                score += GetRemainingSeconds(now) * 50;
        }

        var multiplier = Difficulty switch
        {
            IntrusionDifficulty.Standard => 1.25,
            _ => 1.0,
        };
        return (int)Math.Round(score * multiplier, MidpointRounding.AwayFromZero);
    }

    private static IntrusionGame CreateBlackIce(int gridSize, int bufferCapacity, int timeLimitSeconds, long now)
    {
        const int solutionLength = 12;
        for (var attempt = 0; attempt < 512; attempt++)
        {
            var targetTokens = TokenPool.ToArray();
            Shuffle(targetTokens);
            var solutionTokens = targetTokens.Take(solutionLength).ToList();
            var solutionPath = GenerateSelectionPath(gridSize, solutionLength);
            if (solutionPath is null)
                continue;

            var grid = CreateBlackIceGrid(gridSize, solutionTokens);
            for (var index = 0; index < solutionPath.Count; index++)
            {
                var cell = solutionPath[index];
                grid[cell.Row, cell.Column] = solutionTokens[index];
            }

            var entryObjective = new IntrusionObjective("ENTRY VECTOR", solutionTokens.GetRange(0, 4));
            var shuffledObjectives = new List<IntrusionObjective>
            {
                new(ObjectiveLabels[1], solutionTokens.GetRange(2, 4)),
                new(ObjectiveLabels[2], solutionTokens.GetRange(4, 5)),
                new(ObjectiveLabels[3], solutionTokens.GetRange(7, 5)),
            };
            Shuffle(shuffledObjectives);
            var objectives = new List<IntrusionObjective> { entryObjective };
            objectives.AddRange(shuffledObjectives);

            var winningRoutes = CountExactTokenRoutes(grid, solutionTokens, 3);
            var trapBranches = CountMatchingTrapBranches(grid, solutionPath, solutionTokens);
            if (winningRoutes is < 1 or > 2 || trapBranches < 4)
                continue;

            if (!ValidateGeneratedPuzzle(gridSize, solutionLength, solutionPath, solutionTokens, objectives))
                continue;

            return new IntrusionGame(
                IntrusionDifficulty.BlackIce,
                grid,
                objectives,
                bufferCapacity,
                timeLimitSeconds,
                solutionLength,
                now);
        }

        throw new InvalidOperationException("Could not generate a constrained Black ICE puzzle.");
    }

    private static int CountExactTokenRoutes(string[,] grid, IReadOnlyList<string> targetTokens, int stopAfter)
    {
        var size = grid.GetLength(0);
        var count = 0;
        for (var column = 0; column < size && count < stopAfter; column++)
        {
            if (!string.Equals(grid[0, column], targetTokens[0], StringComparison.Ordinal))
                continue;

            var path = new List<IntrusionCell> { new(0, column) };
            var used = new HashSet<IntrusionCell>(path);
            CountExactTokenRoutes(grid, targetTokens, 1, path, used, stopAfter, ref count);
        }

        return count;
    }

    private static void CountExactTokenRoutes(
        string[,] grid,
        IReadOnlyList<string> targetTokens,
        int tokenIndex,
        List<IntrusionCell> path,
        HashSet<IntrusionCell> used,
        int stopAfter,
        ref int count)
    {
        if (count >= stopAfter)
            return;
        if (tokenIndex == targetTokens.Count)
        {
            count++;
            return;
        }

        var size = grid.GetLength(0);
        var previous = path[^1];
        var sameColumn = path.Count % 2 == 1;
        for (var index = 0; index < size && count < stopAfter; index++)
        {
            var candidate = sameColumn
                ? new IntrusionCell(index, previous.Column)
                : new IntrusionCell(previous.Row, index);
            if (used.Contains(candidate) ||
                !string.Equals(grid[candidate.Row, candidate.Column], targetTokens[tokenIndex], StringComparison.Ordinal))
                continue;

            path.Add(candidate);
            used.Add(candidate);
            CountExactTokenRoutes(grid, targetTokens, tokenIndex + 1, path, used, stopAfter, ref count);
            used.Remove(candidate);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static int CountMatchingTrapBranches(
        string[,] grid,
        IReadOnlyList<IntrusionCell> solutionPath,
        IReadOnlyList<string> solutionTokens)
    {
        var size = grid.GetLength(0);
        var traps = 0;
        for (var pathIndex = 0; pathIndex < solutionPath.Count - 1; pathIndex++)
        {
            var current = solutionPath[pathIndex];
            var expected = solutionTokens[pathIndex + 1];
            var sameColumn = (pathIndex + 1) % 2 == 1;
            for (var index = 0; index < size; index++)
            {
                var candidate = sameColumn
                    ? new IntrusionCell(index, current.Column)
                    : new IntrusionCell(current.Row, index);
                if (candidate == solutionPath[pathIndex + 1] || solutionPath.Take(pathIndex + 1).Contains(candidate))
                    continue;
                if (string.Equals(grid[candidate.Row, candidate.Column], expected, StringComparison.Ordinal))
                    traps++;
            }
        }

        return traps;
    }

    private bool HasLegalSelection()
    {
        for (var row = 0; row < GridSize; row++)
        for (var column = 0; column < GridSize; column++)
            if (CanSelect(row, column))
                return true;
        return false;
    }

    private static string[,] CreateRandomGrid(int size)
    {
        var grid = new string[size, size];
        for (var row = 0; row < size; row++)
        for (var column = 0; column < size; column++)
            grid[row, column] = TokenPool[Random.Shared.Next(TokenPool.Length)];
        return grid;
    }

    private static string[,] CreateBlackIceGrid(int size, IReadOnlyList<string> solutionTokens)
    {
        var decoyTokens = TokenPool
            .Where(token => !solutionTokens.Contains(token, StringComparer.Ordinal))
            .ToArray();
        Shuffle(decoyTokens);
        var densePool = solutionTokens
            .Concat(decoyTokens.Take(3))
            .ToArray();
        var grid = new string[size, size];
        for (var row = 0; row < size; row++)
        for (var column = 0; column < size; column++)
            grid[row, column] = densePool[Random.Shared.Next(densePool.Length)];
        return grid;
    }

    private static IReadOnlyList<IntrusionCell>? GenerateSelectionPath(int size, int length)
    {
        var startColumns = Enumerable.Range(0, size).ToArray();
        Shuffle(startColumns);
        foreach (var column in startColumns)
        {
            var path = new List<IntrusionCell> { new(0, column) };
            var used = new HashSet<IntrusionCell>(path);
            if (ExtendSelectionPath(size, length, path, used))
                return path;
        }

        return null;
    }

    private static bool ExtendSelectionPath(
        int size,
        int targetLength,
        List<IntrusionCell> path,
        HashSet<IntrusionCell> used)
    {
        if (path.Count == targetLength)
            return true;

        var previous = path[^1];
        var sameColumn = path.Count % 2 == 1;
        var candidates = new List<IntrusionCell>(size - 1);
        for (var index = 0; index < size; index++)
        {
            var candidate = sameColumn
                ? new IntrusionCell(index, previous.Column)
                : new IntrusionCell(previous.Row, index);
            if (!used.Contains(candidate))
                candidates.Add(candidate);
        }

        Shuffle(candidates);
        foreach (var candidate in candidates)
        {
            path.Add(candidate);
            used.Add(candidate);
            if (ExtendSelectionPath(size, targetLength, path, used))
                return true;
            used.Remove(candidate);
            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private static bool ValidateGeneratedPuzzle(
        int size,
        int bufferCapacity,
        IReadOnlyList<IntrusionCell> path,
        IReadOnlyList<string> pathTokens,
        IReadOnlyList<IntrusionObjective> objectives)
    {
        if (path.Count != bufferCapacity || pathTokens.Count != bufferCapacity || path[0].Row != 0)
            return false;

        var used = new HashSet<IntrusionCell>();
        for (var index = 0; index < path.Count; index++)
        {
            var cell = path[index];
            if (cell.Row < 0 || cell.Row >= size || cell.Column < 0 || cell.Column >= size || !used.Add(cell))
                return false;
            if (index == 0)
                continue;

            var previous = path[index - 1];
            var requiresSameColumn = index % 2 == 1;
            if (requiresSameColumn ? cell.Column != previous.Column : cell.Row != previous.Row)
                return false;
        }

        return objectives.All(objective => ContainsSequence(pathTokens, objective.Sequence));
    }

    private static bool ContainsSequence(IReadOnlyList<string> source, IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0 || sequence.Count > source.Count)
            return false;

        for (var start = 0; start <= source.Count - sequence.Count; start++)
        {
            var matches = true;
            for (var index = 0; index < sequence.Count; index++)
            {
                if (string.Equals(source[start + index], sequence[index], StringComparison.Ordinal))
                    continue;
                matches = false;
                break;
            }

            if (matches)
                return true;
        }

        return false;
    }

    private static void Shuffle<T>(IList<T> values)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }
}
