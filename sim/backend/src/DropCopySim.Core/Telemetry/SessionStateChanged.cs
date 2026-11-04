namespace DropCopySim.Core.Telemetry;

/// <summary>Emitted when the FIX acceptor session FSM transitions.</summary>
public sealed record SessionStateChanged : TelemetryEvent
{
    /// <summary>Human-readable session state name (e.g. Active, Disconnected).</summary>
    public required string State { get; init; }

    /// <summary>Acceptor SenderCompID.</summary>
    public required string SenderCompId { get; init; }

    /// <summary>Connected client TargetCompID / expected CompID.</summary>
    public required string TargetCompId { get; init; }

    /// <summary>Next outbound MsgSeqNum at the time of the transition.</summary>
    public required long MsgSeqNum { get; init; }

    /// <summary>UTC time of the last inbound or outbound heartbeat activity.</summary>
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
}
