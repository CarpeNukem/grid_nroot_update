using System.Text.RegularExpressions;

namespace GridNrootUpdate;

internal static partial class Glob
{
    public static bool IsMatch(string value, string pattern)
        => Regex.IsMatch(value, ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".");
        return $"^{escaped}$";
    }
}
