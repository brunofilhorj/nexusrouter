using System.Diagnostics.Metrics;

namespace NexusRouter.SessionLog;

/// <summary>
/// Counters for append failures and synchronous segment rotation.
/// </summary>
public sealed class SessionLogMetrics : IDisposable
{
    /// <summary>
    /// Meter name exported by the host.
    /// </summary>
    public const string MeterName = "NexusRouter.SessionLog";

    private readonly Meter _meter;
    private int _disposed;

    /// <summary>
    /// Creates the meter and its counters.
    /// </summary>
    public SessionLogMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        AppendFailed = _meter.CreateCounter<long>(
            "sessionlog_append_failed_total",
            unit: "{failures}",
            description: "Appends that failed and left the stream degraded.");
        SyncRotation = _meter.CreateCounter<long>(
            "sessionlog_sync_rotation_total",
            unit: "{rotations}",
            description: "Segment rotations performed on the writer thread.");
    }

    /// <summary>
    /// Incremented once per failed append.
    /// </summary>
    public Counter<long> AppendFailed { get; }

    /// <summary>
    /// Incremented when the next segment was not ready.
    /// </summary>
    public Counter<long> SyncRotation { get; }

    /// <summary>
    /// Disposes the meter.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _meter.Dispose();
    }
}
