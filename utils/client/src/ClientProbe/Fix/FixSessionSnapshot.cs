namespace ClientProbe.Fix;

/// <summary>Session identity and sequence numbers shown on the FIX screen card.</summary>
/// <param name="SenderCompId">SenderCompID (tag 49) the probe sends.</param>
/// <param name="TargetCompId">TargetCompID (tag 56) the probe sends.</param>
/// <param name="MsgSeqNum">MsgSeqNum (tag 34) of the last message the probe sent. Local to the probe session.</param>
/// <param name="LastReceivedSeqNum">MsgSeqNum (tag 34) of the last message received, or 0 when none arrived.</param>
public sealed record FixSessionSnapshot(string SenderCompId, string TargetCompId, int MsgSeqNum, int LastReceivedSeqNum);

/// <summary>Implemented by adapters that keep a FIX session and expose it in the state snapshot.</summary>
public interface IFixSessionSource
{
    /// <summary>Current session, or null when no session was opened since the last disconnect.</summary>
    FixSessionSnapshot? Session { get; }
}
