namespace DropCopySim.Core.Telemetry;

/// <summary>Periodic snapshot of send counters and observed rate.</summary>
public sealed record CountersSnapshot : TelemetryEvent
{
    /// <summary>Total application messages successfully sent in the current round.</summary>
    public required long TotalSent { get; init; }

    /// <summary>Observed send rate over the last counters interval.</summary>
    public required double RatePerSec { get; init; }

    /// <summary>Counts keyed by ExecType (FIX tag 150) character.</summary>
    public required IReadOnlyDictionary<string, long> ByExecType { get; init; }

    /// <summary>Number of send/generation errors in the current round.</summary>
    public required long Errors { get; init; }

    /// <summary>Milliseconds elapsed since the current round started.</summary>
    public required long ElapsedMs { get; init; }
}
