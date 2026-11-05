using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Core.Telemetry;

/// <summary>
/// Telemetry broadcast tuning options for the <see cref="TelemetryHub"/>.
/// Bound from configuration section <c>DropCopySim:Telemetry</c>.
/// </summary>
public sealed record TelemetryOptions
{
    /// <summary>Configuration section name under <c>DropCopySim</c>.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Capacity of the bounded telemetry <see cref="System.Threading.Channels.Channel{T}"/>.
    /// When full, the oldest event is dropped (overflow = DropOldest) so publishing never blocks the FIX send path.
    /// </summary>
    [Range(1, 1_000_000)]
    public int TelemetryChannelCapacity { get; init; } = 4096;

    /// <summary>
    /// Maximum <see cref="MessageSample"/> events forwarded to clients per second.
    /// Samples beyond the per-second budget are dropped. Zero disables message samples entirely.
    /// </summary>
    [Range(0, 100_000)]
    public int MessageSampleMaxPerSecond { get; init; } = 50;

    /// <summary>
    /// Interval, in milliseconds, between periodic <see cref="CountersSnapshot"/> broadcasts.
    /// </summary>
    [Range(50, 60_000)]
    public int CountersIntervalMs { get; init; } = 500;
}
