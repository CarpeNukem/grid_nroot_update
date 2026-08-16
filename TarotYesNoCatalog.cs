using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GridNrootUpdate;

internal enum TarotYesNoOutcome
{
    StrongYes,
    Yes,
    Maybe,
    No,
    StrongNo,
}

internal sealed class TarotYesNoEntry
{
    public string Upright { get; set; } = "maybe";
    public string Reversed { get; set; } = "maybe";
}

internal static class TarotYesNoCatalog
{
    private const string FileName = "tarot_yes_no.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyDictionary<string, TarotYesNoEntry> Load(
        string imageDirectory,
        out string sourcePath,
        out string? error)
    {
        var pluginDirectory = Directory.GetParent(imageDirectory)?.FullName ?? imageDirectory;
        sourcePath = Path.Combine(pluginDirectory, FileName);
        error = null;

        try
        {
            if (!File.Exists(sourcePath))
            {
                error = $"Yes/No meaning file not found: {sourcePath}";
                return new Dictionary<string, TarotYesNoEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, TarotYesNoEntry>>(
                             File.ReadAllText(sourcePath),
                             JsonOptions) ?? [];
            var entries = new Dictionary<string, TarotYesNoEntry>(parsed, StringComparer.OrdinalIgnoreCase);
            if (entries.Count != TarotDeck.CardCount)
                error = $"Yes/No meaning file contains {entries.Count} of {TarotDeck.CardCount} cards.";
            return entries;
        }
        catch (Exception exception)
        {
            error = $"Could not load {FileName}: {exception.Message}";
            return new Dictionary<string, TarotYesNoEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static TarotYesNoOutcome Resolve(
        IReadOnlyDictionary<string, TarotYesNoEntry> entries,
        int cardIndex,
        bool reversed)
    {
        if (!entries.TryGetValue(TarotDeck.CardKey(cardIndex), out var entry))
            return TarotYesNoOutcome.Maybe;
        return Parse(reversed ? entry.Reversed : entry.Upright);
    }

    public static string Label(TarotYesNoOutcome outcome)
        => outcome switch
        {
            TarotYesNoOutcome.StrongYes => "STRONG YES",
            TarotYesNoOutcome.Yes => "YES",
            TarotYesNoOutcome.Maybe => "MAYBE / NOT YET",
            TarotYesNoOutcome.No => "NO",
            TarotYesNoOutcome.StrongNo => "STRONG NO",
            _ => "MAYBE / NOT YET",
        };

    private static TarotYesNoOutcome Parse(string? value)
        => value?.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "strong_yes" => TarotYesNoOutcome.StrongYes,
            "yes" => TarotYesNoOutcome.Yes,
            "no" => TarotYesNoOutcome.No,
            "strong_no" => TarotYesNoOutcome.StrongNo,
            _ => TarotYesNoOutcome.Maybe,
        };
}
