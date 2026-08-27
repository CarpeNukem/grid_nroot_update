using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;

namespace GridNrootUpdate;

/// <summary>
/// Announces a broadcast the deck has not announced before.
///
/// A venue notice is worth interrupting for in a way a chat line is not, so
/// this rings and can bring the deck up on its own. Both halves are things a
/// plugin should be careful with: a sound and a window that appear unasked are
/// the difference between a useful notification and one people turn off. So the
/// rules here are conservative — announce once per post, never on the first
/// refresh after installing, and never while the moment belongs to the game.
/// </summary>
internal sealed class BroadcastAlert
{
    /// <summary>
    /// Moments when the deck must not put a window in front of the player.
    ///
    /// Combat and duties are self-explanatory. Cutscenes and the between-areas
    /// flags matter because a window opened during a loading screen or a scene
    /// can land in a state the game never redraws cleanly.
    ///
    /// The ring is not gated on these. A sound is a courtesy that can be turned
    /// off; a window stealing focus mid-pull is not.
    /// </summary>
    private static readonly ConditionFlag[] BadMoments =
    [
        ConditionFlag.InCombat,
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.Occupied,
        ConditionFlag.Occupied33,
        ConditionFlag.Occupied38,
        ConditionFlag.Occupied39,
    ];

    private readonly PluginConfig config;
    private readonly Action openDeck;

    public BroadcastAlert(PluginConfig config, Action openDeck)
    {
        this.config = config;
        this.openDeck = openDeck;
    }

    /// <summary>
    /// Decides whether a refreshed catalogue contains anything worth announcing.
    ///
    /// Called from the refresh loop's thread. The marker is advanced whether or
    /// not anything is announced, so a post is considered exactly once however
    /// the settings happen to be set at the time.
    /// </summary>
    public void OnCatalogUpdated(CatalogSnapshot snapshot)
    {
        if (!snapshot.HasPosts)
            return;

        var newest = snapshot.Posts.Max(post => post.PublishedAtUtc).ToUnixTimeMilliseconds();
        if (newest <= config.LastAnnouncedNewsUnixMs)
            return;

        // A deck that has never announced anything is either freshly installed
        // or updating from a build without this feature. Either way its first
        // sight of the catalogue is not news, and ringing through the whole back
        // catalogue is the worst possible introduction to the feature.
        var isFirstSight = config.LastAnnouncedNewsUnixMs == 0;

        config.LastAnnouncedNewsUnixMs = newest;
        config.Save();

        if (isFirstSight)
            return;

        Announce();
    }

    private void Announce()
    {
        if (config.BroadcastToneEnabled)
            VenueSounds.PlayCallTone();

        if (!config.AutoOpenOnBroadcast)
            return;

        // Opening a window touches the interface, so it belongs on the framework
        // thread rather than the refresh loop's.
        PluginService.Framework.RunOnTick(() =>
        {
            if (IsBadMoment())
            {
                PluginService.Log.Debug("A broadcast arrived, but the deck stayed closed: bad moment.");
                return;
            }

            openDeck();
        });
    }

    private static bool IsBadMoment()
    {
        try
        {
            return BadMoments.Any(flag => PluginService.Condition[flag]);
        }
        catch (Exception exception)
        {
            // If the condition table cannot be read, treat the moment as bad:
            // failing to open a window is a far smaller mistake than opening one
            // over a cutscene.
            PluginService.Log.Debug(exception, "Could not read conditions; leaving the deck closed.");
            return true;
        }
    }
}
