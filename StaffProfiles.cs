using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GridNrootUpdate;

internal sealed class StaffProfile
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string Age { get; set; } = string.Empty;
    public string Affiliation { get; set; } = string.Empty;
    public string Occupation { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;

    /// <summary>Optional brand mark shown under the portrait, e.g. a resident DJ logo.</summary>
    public string Logo { get; set; } = string.Empty;

    /// <summary>Free text describing what a DJ plays.</summary>
    public string Genres { get; set; } = string.Empty;

    /// <summary>Remote portrait, when the profile came from the relay.</summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>Remote brand mark, when the profile came from the relay.</summary>
    public string LogoUrl { get; set; } = string.Empty;
    public string RequestLabel { get; set; } = string.Empty;
    public string RequestMessage { get; set; } = string.Empty;
    public StaffProfileOptional? Optional { get; set; }
}

internal sealed class StaffProfileOptional
{
    public string Pronunciation { get; set; } = string.Empty;
    public string Pronouns { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
}

internal static class StaffProfileCatalog
{
    private const string FileName = "staff_profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<StaffProfile> Load(string imageDirectory, out string sourcePath, out string? error)
    {
        var pluginDirectory = Directory.GetParent(imageDirectory)?.FullName ?? imageDirectory;
        sourcePath = Path.Combine(pluginDirectory, FileName);
        error = null;

        try
        {
            if (!File.Exists(sourcePath))
            {
                error = $"Profile data file not found: {sourcePath}";
                return [];
            }

            var profiles = JsonSerializer.Deserialize<List<StaffProfile>>(
                               File.ReadAllText(sourcePath),
                               JsonOptions) ?? [];
            var valid = profiles
                .Where(profile =>
                    !string.IsNullOrWhiteSpace(profile.Id) &&
                    !string.IsNullOrWhiteSpace(profile.Category) &&
                    !string.IsNullOrWhiteSpace(profile.Name))
                .ToArray();
            if (valid.Length != profiles.Count)
                error = "Some profiles were skipped because id, category, or name was missing.";
            return valid;
        }
        catch (Exception exception)
        {
            error = $"Could not load {FileName}: {exception.Message}";
            return [];
        }
    }
}
