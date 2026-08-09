using System;
using System.Threading;

namespace GridNrootUpdate;

public enum UpdateOperationKind
{
    None,
    UpdateCheck,
    Reconcile,
    Repair,
    Assignment,
}

public enum UpdateOperationPhase
{
    Idle,
    Queued,
    Checking,
    Downloading,
    Importing,
    WaitingForPenumbra,
    Configuring,
    Assigning,
    Success,
    NeedsAttention,
    Error,
}

public enum UpdateReleaseAvailability
{
    Unknown,
    UpToDate,
    UpdateAvailable,
}

/// <summary>
/// Immutable updater state intended to be sampled by the UI from any thread.
/// Release availability is deliberately independent of the active operation phase.
/// </summary>
public sealed record UpdateUiSnapshot(
    UpdateOperationKind Operation,
    UpdateOperationPhase Phase,
    UpdateReleaseAvailability ReleaseAvailability,
    string Status,
    string? Detail,
    string? InstalledVersion,
    string? LatestVersion,
    string? AvailableVersion,
    long? BytesDownloaded,
    long? TotalBytes,
    string? ErrorMessage,
    UpdateOperationPhase? FailureStage,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    public static UpdateUiSnapshot Initial { get; } = new(
        UpdateOperationKind.None,
        UpdateOperationPhase.Idle,
        UpdateReleaseAvailability.Unknown,
        "READY",
        "No update operation is active.",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.UtcNow,
        null);

    public bool IsBusy => Phase is
        UpdateOperationPhase.Queued or
        UpdateOperationPhase.Checking or
        UpdateOperationPhase.Downloading or
        UpdateOperationPhase.Importing or
        UpdateOperationPhase.WaitingForPenumbra or
        UpdateOperationPhase.Configuring or
        UpdateOperationPhase.Assigning;

    public double? ProgressFraction =>
        BytesDownloaded is { } downloaded && TotalBytes is > 0
            ? Math.Clamp((double)downloaded / TotalBytes.Value, 0d, 1d)
            : null;
}

/// <summary>
/// Owns the current immutable snapshot. Updates use compare/exchange so readers never
/// observe a partially-mutated state and concurrent publishers do not lose fields.
/// </summary>
internal sealed class UpdateUiStateStore
{
    private UpdateUiSnapshot current = UpdateUiSnapshot.Initial;

    public UpdateUiSnapshot Snapshot => Volatile.Read(ref current);

    public void Initialize(string? installedVersion)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            InstalledVersion = NullIfWhiteSpace(installedVersion),
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// Reclassifies the current result without disturbing its phase or progress. This is
    /// used when a completed sync hands off to assignment so retry actions target only
    /// the part that needs attention.
    /// </summary>
    public void SetOperation(UpdateOperationKind operation)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            Operation = operation,
            UpdatedAt = now,
        });
    }

    public void Queue(UpdateOperationKind operation, string status, string? detail)
    {
        while (true)
        {
            var before = Snapshot;
            if (before.IsBusy)
                return;

            var now = DateTimeOffset.UtcNow;
            var after = before with
            {
                Operation = operation,
                Phase = UpdateOperationPhase.Queued,
                Status = status,
                Detail = NullIfWhiteSpace(detail),
                BytesDownloaded = null,
                TotalBytes = null,
                ErrorMessage = null,
                FailureStage = null,
                StartedAt = now,
                UpdatedAt = now,
                CompletedAt = null,
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref current, after, before), before))
                return;
        }
    }

    public void Begin(UpdateOperationKind operation, UpdateOperationPhase phase, string status, string? detail)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            Operation = operation,
            Phase = phase,
            Status = status,
            Detail = NullIfWhiteSpace(detail),
            BytesDownloaded = null,
            TotalBytes = null,
            ErrorMessage = null,
            FailureStage = null,
            StartedAt = snapshot.Operation == operation && snapshot.Phase == UpdateOperationPhase.Queued
                ? snapshot.StartedAt ?? now
                : now,
            UpdatedAt = now,
            CompletedAt = null,
        });
    }

    public void Transition(UpdateOperationPhase phase, string status, string? detail)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            Phase = phase,
            Status = status,
            Detail = NullIfWhiteSpace(detail),
            BytesDownloaded = null,
            TotalBytes = null,
            ErrorMessage = null,
            FailureStage = null,
            UpdatedAt = now,
            CompletedAt = null,
        });
    }

    public void ReportDownloadProgress(long bytesDownloaded, long? totalBytes, string? detail)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            Phase = UpdateOperationPhase.Downloading,
            Status = "DOWNLOADING",
            Detail = NullIfWhiteSpace(detail),
            BytesDownloaded = Math.Max(0, bytesDownloaded),
            TotalBytes = totalBytes is > 0 ? totalBytes : null,
            ErrorMessage = null,
            FailureStage = null,
            UpdatedAt = now,
            CompletedAt = null,
        });
    }

    public void SetRelease(
        UpdateReleaseAvailability availability,
        string? latestVersion,
        string? installedVersion)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedLatest = NullIfWhiteSpace(latestVersion);
        Update(snapshot => snapshot with
        {
            ReleaseAvailability = availability,
            LatestVersion = normalizedLatest,
            AvailableVersion = availability == UpdateReleaseAvailability.UpdateAvailable
                ? normalizedLatest
                : null,
            InstalledVersion = NullIfWhiteSpace(installedVersion),
            UpdatedAt = now,
        });
    }

    public void Complete(string status, string? detail)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            Phase = UpdateOperationPhase.Success,
            Status = status,
            Detail = NullIfWhiteSpace(detail),
            BytesDownloaded = null,
            TotalBytes = null,
            ErrorMessage = null,
            FailureStage = null,
            UpdatedAt = now,
            CompletedAt = now,
        });
    }

    public void NeedsAttention(string status, string? detail)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            FailureStage = GetFailureStage(snapshot),
            Phase = UpdateOperationPhase.NeedsAttention,
            Status = status,
            Detail = NullIfWhiteSpace(detail),
            BytesDownloaded = null,
            TotalBytes = null,
            ErrorMessage = null,
            UpdatedAt = now,
            CompletedAt = now,
        });
    }

    public void Fail(string status, Exception exception, string? detail = null)
    {
        var now = DateTimeOffset.UtcNow;
        Update(snapshot => snapshot with
        {
            FailureStage = GetFailureStage(snapshot),
            Phase = UpdateOperationPhase.Error,
            Status = status,
            Detail = NullIfWhiteSpace(detail) ?? exception.Message,
            BytesDownloaded = null,
            TotalBytes = null,
            ErrorMessage = exception.Message,
            UpdatedAt = now,
            CompletedAt = now,
        });
    }

    public void Fail(string status, string errorMessage, string? detail = null)
        => Fail(status, new InvalidOperationException(errorMessage), detail);

    private void Update(Func<UpdateUiSnapshot, UpdateUiSnapshot> update)
    {
        while (true)
        {
            var before = Snapshot;
            var after = update(before);
            if (ReferenceEquals(Interlocked.CompareExchange(ref current, after, before), before))
                return;
        }
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static UpdateOperationPhase? GetFailureStage(UpdateUiSnapshot snapshot)
        => snapshot.Phase is UpdateOperationPhase.Error or UpdateOperationPhase.NeedsAttention
            ? snapshot.FailureStage
            : snapshot.Phase;
}
