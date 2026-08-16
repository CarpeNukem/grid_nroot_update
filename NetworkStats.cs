using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Utility;

namespace GridNrootUpdate;

internal enum VenuePresence
{
    Unknown,
    Confirmed,
    Elsewhere,
}

public sealed class NetworkSessionCheckpoint
{
    public long StartedAtUnixMs { get; set; }
    public long LastSeenAtUnixMs { get; set; }
    public int PeakGuests { get; set; }
    public int UniqueGuests { get; set; }
    public int Arrivals { get; set; }
    public int Departures { get; set; }
    public long OccupancySampleTotal { get; set; }
    public int OccupancySampleCount { get; set; }
    public int WorldsRepresented { get; set; }
    public int DataCentersRepresented { get; set; }
    public List<NetworkOccupancyBucket> OccupancyBuckets { get; set; } = [];
}

public sealed class NetworkSessionSummary
{
    public long StartedAtUnixMs { get; set; }
    public long EndedAtUnixMs { get; set; }
    public int PeakGuests { get; set; }
    public int UniqueGuests { get; set; }
    public int Arrivals { get; set; }
    public int Departures { get; set; }
    public double AverageGuests { get; set; }
    public int WorldsRepresented { get; set; }
    public int DataCentersRepresented { get; set; }
    public List<NetworkOccupancyBucket> OccupancyBuckets { get; set; } = [];
}

public sealed class NetworkOccupancyBucket
{
    public long StartedAtUnixMs { get; set; }
    public long SampleTotal { get; set; }
    public int SampleCount { get; set; }
    public int PeakGuests { get; set; }

    public double AverageGuests => SampleCount == 0 ? 0 : SampleTotal / (double)SampleCount;
}

internal sealed record NetworkGuestObservation(
    IPlayerCharacter Player,
    string Identity,
    string DisplayName,
    string HomeWorld,
    string DataCenter,
    bool IsFriend,
    bool IsPartyMember,
    bool WeaponOut,
    bool OffhandOut,
    string? MinionName)
{
    public bool HasWeapon => WeaponOut || OffhandOut;
}

internal sealed record NetworkStatsSnapshot(
    bool IsActive,
    DateTimeOffset? StartedAt,
    int VisibleGuests,
    int PeakGuests,
    int UniqueGuests,
    int HomeWorlds,
    int DataCenters,
    int Friends,
    int PartyMembers,
    int WeaponsDrawn,
    int VisibleMinions,
    int Arrivals,
    int Departures,
    double AverageGuests,
    IReadOnlyList<NetworkOccupancyBucket> OccupancyBuckets,
    IReadOnlyDictionary<string, int> WorldDistribution,
    IReadOnlyDictionary<string, int> DataCenterDistribution)
{
    public static NetworkStatsSnapshot Empty { get; } = new(
        false, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        Array.Empty<NetworkOccupancyBucket>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>());
}

internal static class NetworkGuestScanner
{
    public static NetworkGuestObservation? CaptureLocal()
    {
        var player = PluginService.Objects.LocalPlayer;
        if (player is null || string.IsNullOrWhiteSpace(player.Name.TextValue))
            return null;

        var companion = PluginService.Objects.FirstOrDefault(obj =>
            obj is not null &&
            obj.ObjectKind == ObjectKind.Companion &&
            obj.OwnerId == player.EntityId);
        return CreateObservation(player, companion);
    }

    public static List<NetworkGuestObservation> Capture()
    {
        var localPlayerId = PluginService.Objects.LocalPlayer?.GameObjectId;
        var companionsByOwner = PluginService.Objects
            .Where(obj => obj is not null && obj.ObjectKind == ObjectKind.Companion)
            .GroupBy(obj => obj!.OwnerId)
            .ToDictionary(group => group.Key, group => group.First()!);

        return PluginService.Objects
            .OfType<IPlayerCharacter>()
            .Where(player => player.ObjectKind == ObjectKind.Pc &&
                             !string.IsNullOrWhiteSpace(player.Name.TextValue) &&
                             (localPlayerId is null || player.GameObjectId != localPlayerId.Value))
            .GroupBy(GetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var player = group.First();
                companionsByOwner.TryGetValue(player.EntityId, out var companion);
                return CreateObservation(player, companion);
            })
            .OrderBy(guest => guest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NetworkGuestObservation CreateObservation(
        IPlayerCharacter player,
        IGameObject? companion)
    {
        var world = player.HomeWorld.ValueNullable;
        var homeWorld = world?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(homeWorld))
            homeWorld = player.CurrentWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(homeWorld))
            homeWorld = "Unknown World";

        var dataCenter = world?.DataCenter.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(dataCenter))
            dataCenter = "Unknown DC";

        string? minionName = null;
        if (companion is not null)
        {
            minionName = companion.Name.TextValue;
            if (string.IsNullOrWhiteSpace(minionName))
                minionName = player.CurrentMinion?.ValueNullable?.Singular.ExtractText();
        }

        return new NetworkGuestObservation(
            player,
            GetIdentity(player),
            $"{player.Name.TextValue}@{homeWorld}",
            homeWorld,
            dataCenter,
            player.StatusFlags.HasFlag(StatusFlags.Friend),
            player.StatusFlags.HasFlag(StatusFlags.PartyMember),
            player.StatusFlags.HasFlag(StatusFlags.WeaponOut),
            player.StatusFlags.HasFlag(StatusFlags.OffhandOut),
            minionName);
    }

    private static string GetIdentity(IPlayerCharacter player)
    {
        var homeWorld = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(homeWorld))
            homeWorld = player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "Unknown World";
        return $"{player.Name.TextValue}@{homeWorld}";
    }
}

internal sealed class NetworkStatsTracker
{
    private static readonly TimeSpan AddressGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DepartureGracePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    private const long OccupancyBucketDurationMs = 10 * 60 * 1000;
    private const int MaximumHistoryEntries = 512;

    private readonly PluginConfig config;
    private ActiveSession? active;
    private DateTimeOffset nextCheckpointAt;

    public NetworkStatsTracker(PluginConfig config)
    {
        this.config = config;
        RecoverInterruptedSession();
    }

    public NetworkStatsSnapshot Snapshot { get; private set; } = NetworkStatsSnapshot.Empty;

    public void Update(VenuePresence presence, IReadOnlyList<NetworkGuestObservation> guests, DateTimeOffset now)
    {
        if (presence == VenuePresence.Confirmed)
        {
            active ??= StartSession(now);
            active.LastConfirmedAt = now;
            Observe(active, guests, now);
            Snapshot = CreateSnapshot(active, guests);

            if (now >= nextCheckpointAt)
                SaveCheckpoint(now);
            return;
        }

        if (active is null)
        {
            Snapshot = NetworkStatsSnapshot.Empty;
            return;
        }

        if (presence == VenuePresence.Elsewhere || now - active.LastConfirmedAt >= AddressGracePeriod)
        {
            FinalizeSession(presence == VenuePresence.Elsewhere ? now : active.LastConfirmedAt);
            return;
        }

        // Loading or address data is briefly unavailable. Preserve the last confirmed
        // snapshot and do not count empty samples or fake departures.
    }

    public void FinalizeSession(DateTimeOffset endedAt)
    {
        if (active is null)
            return;

        active.Departures += active.Present.Count;
        AddSummary(new NetworkSessionSummary
        {
            StartedAtUnixMs = active.StartedAt.ToUnixTimeMilliseconds(),
            EndedAtUnixMs = Math.Max(active.StartedAt.ToUnixTimeMilliseconds(), endedAt.ToUnixTimeMilliseconds()),
            PeakGuests = active.PeakGuests,
            UniqueGuests = active.UniqueGuests.Count,
            Arrivals = active.Arrivals,
            Departures = active.Departures,
            AverageGuests = active.OccupancySampleCount == 0
                ? 0
                : active.OccupancySampleTotal / (double)active.OccupancySampleCount,
            WorldsRepresented = active.WorldsRepresented.Count,
            DataCentersRepresented = active.DataCentersRepresented.Count,
            OccupancyBuckets = CloneBuckets(active.OccupancyBuckets),
        });

        active = null;
        config.ActiveNetworkSession = null;
        TrimHistory(endedAt);
        config.Save();
        Snapshot = NetworkStatsSnapshot.Empty;
        PluginService.Log.Information("Grid network session ended at {EndedAt}.", endedAt);
    }

    private ActiveSession StartSession(DateTimeOffset now)
    {
        nextCheckpointAt = now + CheckpointInterval;
        var session = new ActiveSession(now);
        active = session;
        PluginService.Log.Information("Grid network session started at {StartedAt}.", now);
        SaveCheckpoint(now);
        return session;
    }

    private static void Observe(ActiveSession session, IReadOnlyList<NetworkGuestObservation> guests, DateTimeOffset now)
    {
        var seen = guests.Select(guest => guest.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var guest in guests)
        {
            if (!session.Present.Contains(guest.Identity))
            {
                session.Present.Add(guest.Identity);
                session.Arrivals++;
            }

            session.LastSeen[guest.Identity] = now;
            session.UniqueGuests.Add(guest.Identity);
            if (guest.HomeWorld != "Unknown World")
                session.WorldsRepresented.Add(guest.HomeWorld);
            if (guest.DataCenter != "Unknown DC")
                session.DataCentersRepresented.Add(guest.DataCenter);
        }

        foreach (var identity in session.Present.ToArray())
        {
            if (seen.Contains(identity) ||
                !session.LastSeen.TryGetValue(identity, out var lastSeen) ||
                now - lastSeen < DepartureGracePeriod)
                continue;

            session.Present.Remove(identity);
            session.Departures++;
        }

        session.PeakGuests = Math.Max(session.PeakGuests, guests.Count);
        session.OccupancySampleTotal += session.Present.Count;
        session.OccupancySampleCount++;
        AddOccupancySample(session, now, session.Present.Count);
    }

    private static NetworkStatsSnapshot CreateSnapshot(ActiveSession session, IReadOnlyList<NetworkGuestObservation> guests)
    {
        var worlds = BuildDistribution(guests.Select(guest => guest.HomeWorld), "Unknown World");
        var dataCenters = BuildDistribution(guests.Select(guest => guest.DataCenter), "Unknown DC");
        return new NetworkStatsSnapshot(
            true,
            session.StartedAt,
            guests.Count,
            session.PeakGuests,
            session.UniqueGuests.Count,
            worlds.Count,
            dataCenters.Count,
            guests.Count(guest => guest.IsFriend),
            guests.Count(guest => guest.IsPartyMember),
            guests.Count(guest => guest.HasWeapon),
            guests.Count(guest => !string.IsNullOrWhiteSpace(guest.MinionName)),
            session.Arrivals,
            session.Departures,
            session.OccupancySampleCount == 0
                ? 0
                : session.OccupancySampleTotal / (double)session.OccupancySampleCount,
            CloneBuckets(session.OccupancyBuckets),
            worlds,
            dataCenters);
    }

    private static IReadOnlyDictionary<string, int> BuildDistribution(IEnumerable<string> values, string unknownValue)
        => values
            .Where(value => !string.Equals(value, unknownValue, StringComparison.Ordinal))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private void SaveCheckpoint(DateTimeOffset now)
    {
        if (active is null)
            return;

        config.ActiveNetworkSession = new NetworkSessionCheckpoint
        {
            StartedAtUnixMs = active.StartedAt.ToUnixTimeMilliseconds(),
            LastSeenAtUnixMs = now.ToUnixTimeMilliseconds(),
            PeakGuests = active.PeakGuests,
            UniqueGuests = active.UniqueGuests.Count,
            Arrivals = active.Arrivals,
            Departures = active.Departures,
            OccupancySampleTotal = active.OccupancySampleTotal,
            OccupancySampleCount = active.OccupancySampleCount,
            WorldsRepresented = active.WorldsRepresented.Count,
            DataCentersRepresented = active.DataCentersRepresented.Count,
            OccupancyBuckets = CloneBuckets(active.OccupancyBuckets),
        };
        nextCheckpointAt = now + CheckpointInterval;
        config.Save();
    }

    private void RecoverInterruptedSession()
    {
        var checkpoint = config.ActiveNetworkSession;
        if (checkpoint is null || checkpoint.StartedAtUnixMs <= 0 || checkpoint.LastSeenAtUnixMs <= 0)
            return;

        AddSummary(new NetworkSessionSummary
        {
            StartedAtUnixMs = checkpoint.StartedAtUnixMs,
            EndedAtUnixMs = Math.Max(checkpoint.StartedAtUnixMs, checkpoint.LastSeenAtUnixMs),
            PeakGuests = checkpoint.PeakGuests,
            UniqueGuests = checkpoint.UniqueGuests,
            Arrivals = checkpoint.Arrivals,
            Departures = checkpoint.Departures,
            AverageGuests = checkpoint.OccupancySampleCount == 0
                ? 0
                : checkpoint.OccupancySampleTotal / (double)checkpoint.OccupancySampleCount,
            WorldsRepresented = checkpoint.WorldsRepresented,
            DataCentersRepresented = checkpoint.DataCentersRepresented,
            OccupancyBuckets = CloneBuckets(checkpoint.OccupancyBuckets),
        });
        config.ActiveNetworkSession = null;
        TrimHistory(DateTimeOffset.UtcNow);
        config.Save();
    }

    private void AddSummary(NetworkSessionSummary summary)
        => config.NetworkSessionHistory.Add(summary);

    private static void AddOccupancySample(ActiveSession session, DateTimeOffset now, int guests)
    {
        var bucketStart = now.ToUnixTimeMilliseconds() / OccupancyBucketDurationMs * OccupancyBucketDurationMs;
        var bucket = session.OccupancyBuckets.LastOrDefault();
        if (bucket is null || bucket.StartedAtUnixMs != bucketStart)
        {
            bucket = new NetworkOccupancyBucket { StartedAtUnixMs = bucketStart };
            session.OccupancyBuckets.Add(bucket);
        }

        bucket.SampleTotal += guests;
        bucket.SampleCount++;
        bucket.PeakGuests = Math.Max(bucket.PeakGuests, guests);
    }

    private static List<NetworkOccupancyBucket> CloneBuckets(IEnumerable<NetworkOccupancyBucket>? buckets)
        => buckets?.Select(bucket => new NetworkOccupancyBucket
        {
            StartedAtUnixMs = bucket.StartedAtUnixMs,
            SampleTotal = bucket.SampleTotal,
            SampleCount = bucket.SampleCount,
            PeakGuests = bucket.PeakGuests,
        }).ToList() ?? [];

    private void TrimHistory(DateTimeOffset now)
    {
        var cutoff = (now - HistoryRetention).ToUnixTimeMilliseconds();
        config.NetworkSessionHistory.RemoveAll(summary => summary.EndedAtUnixMs < cutoff);
        if (config.NetworkSessionHistory.Count <= MaximumHistoryEntries)
            return;

        config.NetworkSessionHistory = config.NetworkSessionHistory
            .OrderByDescending(summary => summary.EndedAtUnixMs)
            .Take(MaximumHistoryEntries)
            .OrderBy(summary => summary.EndedAtUnixMs)
            .ToList();
    }

    private sealed class ActiveSession(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastConfirmedAt { get; set; } = startedAt;
        public int PeakGuests { get; set; }
        public int Arrivals { get; set; }
        public int Departures { get; set; }
        public long OccupancySampleTotal { get; set; }
        public int OccupancySampleCount { get; set; }
        public HashSet<string> UniqueGuests { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Present { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> LastSeen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> WorldsRepresented { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DataCentersRepresented { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<NetworkOccupancyBucket> OccupancyBuckets { get; } = [];
    }
}
