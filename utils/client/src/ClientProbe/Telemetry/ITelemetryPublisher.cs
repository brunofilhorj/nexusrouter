using ClientProbe.Probe;

namespace ClientProbe.Telemetry;

/// <summary>Entry point adapters use to hand events to the telemetry hub.</summary>
public interface ITelemetryPublisher
{
    /// <summary>
    /// Queues an event without blocking. When the channel is full the event is discarded and
    /// <see cref="ProbeCounters.RecordDropped"/> is called.
    /// </summary>
    /// <param name="probeEvent">Event to publish.</param>
    /// <param name="counters">Counters of the adapter that produced the event.</param>
    /// <returns>True when queued; false when dropped.</returns>
    bool TryPublish(ProbeEvent probeEvent, ProbeCounters counters);
}
