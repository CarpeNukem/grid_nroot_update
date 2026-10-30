using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GridNrootUpdate;

/// <summary>
/// What a mod folder says about itself.
///
/// Penumbra keeps a meta.json in every mod folder and exposes none of it over
/// IPC: the mod list is directory name to display name and nothing more. The
/// venue packs write the release number into that file, so it is the one place
/// the version of an installed pack can be read from. Read-only, and only ever
/// for a directory name Penumbra itself listed.
/// </summary>
internal readonly record struct PenumbraModMeta(string Name, string Version, string? Identifier)
{
    public static PenumbraModMeta? TryRead(string? penumbraRoot, string? modDirectory)
    {
        if (string.IsNullOrWhiteSpace(penumbraRoot) || string.IsNullOrWhiteSpace(modDirectory))
            return null;

        // A directory name, not a path. Anything else is refused rather than
        // resolved: this reads files, and the name came over IPC.
        if (modDirectory.IndexOfAny(['/', '\\']) >= 0 || modDirectory is "." or "..")
            return null;

        try
        {
            var root = Path.GetFullPath(penumbraRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folder = Path.GetFullPath(Path.Combine(root, modDirectory));
            if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;

            var metaPath = Path.Combine(folder, "meta.json");
            if (!File.Exists(metaPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
            var element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            return new PenumbraModMeta(
                ReadString(element, "Name") ?? string.Empty,
                ReadString(element, "Version") ?? string.Empty,
                ReadString(element, "Identifier"));
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not read meta.json for Penumbra mod {ModDirectory}.", modDirectory);
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// A release number the deck is willing to compare.
///
/// Strict on purpose: two to four dotted integers, an optional leading "v", and
/// nothing after. "1.4.1-halloween.4" does not parse, and that is the point: a
/// pack carrying a suffix is somebody's work in progress, and ranking it below
/// the release it was cut from would have the update path delete it. What does
/// not parse is never older than anything.
/// </summary>
internal readonly record struct ModVersion(int Major, int Minor, int Patch, int Revision) : IComparable<ModVersion>
{
    public static bool TryParse(string? text, out ModVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        var parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 9)
                return false;

            foreach (var character in part)
            {
                if (character is < '0' or > '9')
                    return false;
            }

            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new ModVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public int CompareTo(ModVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
            return minor;

        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : Revision.CompareTo(other.Revision);
    }

    public override string ToString()
        => Revision > 0 ? $"{Major}.{Minor}.{Patch}.{Revision}" : $"{Major}.{Minor}.{Patch}";
}
