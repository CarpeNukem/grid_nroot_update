using System;
using System.Collections.Generic;
using System.Text;

namespace GridNrootUpdate;

internal static class CollectionNameMatcher
{
    public static IReadOnlyList<string> GetSearchVariants(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var variants = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            var trimmed = candidate.Trim();
            if (seen.Add(trimmed))
                variants.Add(trimmed);
        }

        var original = value.Trim();
        var unquoted = original.Trim('\'', '"', '`', '\u2018', '\u2019', '\u201c', '\u201d');
        var compact = Normalize(unquoted);

        Add(original);
        Add(unquoted);
        Add(compact);

        if (IsGridAlias(compact))
        {
            Add("TheGrid");
            Add("The Grid");
            Add("the grid");
            Add("Grid");
            Add("grid");
        }

        foreach (var candidate in variants.ToArray())
        {
            Add($"'{candidate}'");
            Add($"\"{candidate}\"");
        }

        return variants;
    }

    public static bool IsMatch(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft.Length > 0 &&
               (normalizedLeft == normalizedRight ||
                (IsGridAlias(normalizedLeft) && IsGridAlias(normalizedRight)));
    }

    private static bool IsGridAlias(string normalized)
        => string.Equals(normalized, "grid", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(normalized, "thegrid", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
