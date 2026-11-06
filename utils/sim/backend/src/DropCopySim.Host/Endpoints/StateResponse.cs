using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Generation;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Consolidated control-plane snapshot returned by <c>GET /v1/state</c>: the FIX session, the
/// simulation FSM and counters, and the current <see cref="SimulationConfig"/> (FR-API-01).
/// </summary>
public sealed record StateResponse
{
    /// <summary>FIX acceptor session view, or <c>null</c> when no client has connected yet.</summary>
    public required SessionStateView? Session { get; init; }

    /// <summary>Simulation FSM state and round counters.</summary>
    public required SimulationStateView Simulation { get; init; }

    /// <summary>The configuration snapshot currently in effect.</summary>
    public required SimulationConfig Config { get; init; }
}

/// <summary>FIX acceptor session view for <see cref="StateResponse"/>.</summary>
public sealed record SessionStateView
{
    /// <summary>Acceptor session FSM state name (e.g. <c>Active</c>, <c>Disconnected</c>).</summary>
    public required string State { get; init; }

    /// <summary>Acceptor SenderCompID (the simulated B3 side).</summary>
    public required string SenderCompId { get; init; }

    /// <summary>Expected client TargetCompID (the NexusRouter side).</summary>
    public required string TargetCompId { get; init; }

    /// <summary>Next outbound MsgSeqNum (FIX tag 34) the session will allocate.</summary>
    public required long NextOutboundMsgSeqNum { get; init; }

    /// <summary>Next inbound MsgSeqNum the session expects from the client.</summary>
    public required long NextExpectedInboundMsgSeqNum { get; init; }

    /// <summary>UTC time of the last inbound or outbound heartbeat activity, or <c>null</c> when none yet.</summary>
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
}

/// <summary>Simulation FSM and round counters view for <see cref="StateResponse"/>.</summary>
public sealed record SimulationStateView
{
    /// <summary>Simulation control FSM state (<c>Idle</c>, <c>Running</c>, <c>Paused</c>).</summary>
    public required SimulationState State { get; init; }

    /// <summary>Total application messages sent in the active or most recent round.</summary>
    public required long TotalSent { get; init; }

    /// <summary>Send/generation errors in the active or most recent round.</summary>
    public required long Errors { get; init; }

    /// <summary>Per-ExecType counts keyed by the FIX tag 150 character.</summary>
    public required IReadOnlyDictionary<string, long> ByExecType { get; init; }

    /// <summary>Milliseconds elapsed since the current round started, or <c>0</c> when no round is active.</summary>
    public required long ElapsedMs { get; init; }
}
