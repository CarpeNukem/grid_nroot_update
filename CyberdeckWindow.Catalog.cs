using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Textures.TextureWraps;

namespace GridNrootUpdate;

internal sealed partial class CyberdeckWindow
{
    /// <summary>
    /// Resolves the drinks card and staff directory from the relay, falling back
    /// to what ships with the plugin.
    ///
    /// The rule is replace, not merge: once a catalogue has been loaded its list
    /// *is* the list, so what an editor sees in the admin tool is exactly what
    /// the deck shows. Bundled data covers having never reached the relay — it
    /// is not a floor that remote edits sit on top of, because then a deleted
    /// drink would reappear and nobody could explain why.
    ///
    /// A reachable relay returning nothing is a real answer and is honoured.
    /// </summary>

    /// <summary>One drink, from wherever it came.</summary>
    internal sealed record MenuEntry(
        string Name,
        string PriceLabel,
        string BundledImage,
        string ImageUrl,
        string Ingredients,
        string Description,
        string Taste);

    private IReadOnlyList<MenuEntry> GetMenuEntries()
    {
        var catalog = getCatalog();
        if (catalog.IsLoaded)
        {
            return catalog.Menu
                .Select(item => new MenuEntry(
                    item.Name,
                    string.IsNullOrWhiteSpace(item.PriceLabel) ? item.PriceGil.ToString("N0") : item.PriceLabel,
                    item.BundledImage,
                    item.ImageUrl,
                    item.Ingredients,
                    item.Description,
                    item.Taste))
                .ToArray();
        }

        return DrinkMenu
            .Select(drink => new MenuEntry(
                drink.Name,
                drink.Price,
                drink.ImageName,
                string.Empty,
                drink.Ingredients,
                drink.Description,
                drink.Taste))
            .ToArray();
    }

    /// <summary>
    /// Staff profiles for the directory, remote when available.
    ///
    /// Mapped onto the same <see cref="StaffProfile"/> the local file produces,
    /// so the directory UI does not care which source it is reading.
    /// </summary>
    private IReadOnlyList<StaffProfile> GetProfileEntries()
    {
        var catalog = getCatalog();
        if (!catalog.IsLoaded)
            return staffProfiles;

        return catalog.Profiles
            .Select(profile => new StaffProfile
            {
                Id = profile.Id,
                Category = profile.Category,
                Name = profile.Name,
                CharacterName = profile.CharacterName,
                Age = profile.Age,
                Affiliation = profile.Affiliation,
                Occupation = profile.Occupation,
                Bio = profile.Bio,
                Image = profile.BundledImage,
                ImageUrl = profile.ImageUrl,
                Logo = profile.LogoImage,
                LogoUrl = profile.LogoUrl,
                RequestLabel = profile.RequestLabel,
                RequestMessage = profile.RequestMessage,
                Optional = profile.Optional is null
                    ? null
                    : new StaffProfileOptional
                    {
                        Pronunciation = profile.Optional.Pronunciation,
                        Pronouns = profile.Optional.Pronouns,
                        Race = profile.Optional.Race,
                        Availability = profile.Optional.Availability,
                        Quote = profile.Optional.Quote,
                    },
            })
            .ToArray();
    }

    /// <summary>
    /// Picks the texture for a record that may have remote art, bundled art, or
    /// both.
    ///
    /// A verified remote download wins; while one is still downloading the
    /// bundled art shows, so a card never flashes empty waiting for the network.
    /// Animated media never resolves here — the deck cannot draw a GIF or MP4.
    /// </summary>
    private IDalamudTextureWrap? ResolveArt(string remoteUrl, string bundledName)
    {
        if (!string.IsNullOrWhiteSpace(remoteUrl) && !RemoteAssetCache.IsAnimated(remoteUrl))
        {
            var asset = remoteAssets.TryGet(remoteUrl);
            if (asset is not null)
            {
                var remote = PluginService.TextureProvider.GetFromFile(asset.LocalPath).GetWrapOrDefault();
                if (remote is not null)
                    return remote;
            }
        }

        return string.IsNullOrWhiteSpace(bundledName) ? null : GetTextureWrap(bundledName);
    }

    /// <summary>
    /// A published prose page by id, or null when the relay has not supplied one.
    ///
    /// Returning null rather than an empty page is what lets each screen keep
    /// its own hardcoded fallback: "no page" and "a page that happens to be
    /// blank" are different answers.
    /// </summary>
    private RemotePage? GetPage(string id)
    {
        var catalog = getCatalog();
        if (!catalog.IsLoaded)
            return null;

        var page = catalog.Pages.FirstOrDefault(
            entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(page?.Body) ? null : page;
    }

    /// <summary>Where the directory and drinks card are currently reading from.</summary>
    private string GetCatalogSourceLabel()
    {
        var catalog = getCatalog();
        return catalog.IsLoaded ? catalog.SourceLabel : "bundled";
    }
}
