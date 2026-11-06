using DropCopySim.Core.Acceptor;

namespace DropCopySim.Host.Endpoints;

/// <summary>Body of <c>POST /v1/fault/logout</c>: the Text (tag 58) carried in the forced Logout (FR-FAULT-02).</summary>
public sealed record ForceLogoutRequest
{
    /// <summary>
    /// Human-readable Logout reason, e.g. <c>"MsgSeqNum too low"</c> or <c>"Password will expire"</c>
    /// (<c>.kiro/steering/b3-guidelines.md</c> §13).
    /// </summary>
    public string? Text { get; init; }
}

/// <summary>Body of <c>POST /v1/fault/logon-mode</c>: how the next client Logon is answered (FR-FAULT-03).</summary>
public sealed record LogonModeRequest
{
    /// <summary>
    /// Logon response applied to the next Logon: <see cref="LogonMode.Success"/>,
    /// <see cref="LogonMode.InvalidCredentials"/> or <see cref="LogonMode.NotAuthorized"/>.
    /// </summary>
    public LogonMode? Mode { get; init; }
}

/// <summary>Body of <c>POST /v1/fault/seq-gap</c>: how many outbound MsgSeqNum values to skip (FR-FAULT-04).</summary>
public sealed record SequenceGapRequest
{
    /// <summary>Number of outbound sequence numbers to skip. Must be positive.</summary>
    public int? Skip { get; init; }
}
