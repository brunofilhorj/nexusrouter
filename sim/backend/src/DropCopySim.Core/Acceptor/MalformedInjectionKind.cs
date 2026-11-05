namespace DropCopySim.Core.Acceptor;

/// <summary>
/// Kinds of malformed FIX frames the acceptor can inject on demand (FR-FAULT-05).
/// </summary>
/// <remarks>
/// Used by <see cref="AcceptorSession.InjectMalformedAsync"/> to exercise the client's
/// session-level error handling against frames that deviate from the B3 wire rules
/// (<c>.kiro/steering/b3-guidelines.md</c> §13, §15).
/// </remarks>
public enum MalformedInjectionKind
{
    /// <summary>
    /// A Heartbeat (MsgType=0) whose CheckSum (tag 10) digits are corrupted, so the client's
    /// checksum validation must reject it (<c>.kiro/steering/b3-guidelines.md</c> §15).
    /// </summary>
    InvalidCheckSum = 0,

    /// <summary>
    /// A Heartbeat (MsgType=0) with the SendingTime (tag 52) header field removed, so the frame
    /// is missing a required header field.
    /// </summary>
    MissingField = 1,
}
