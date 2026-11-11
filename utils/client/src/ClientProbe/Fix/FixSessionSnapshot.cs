namespace ClientProbe.Fix;

/// <summary>Session identity and sequence numbers shown on the FIX screen card.</summary>
/// <param name="SenderCompId">SenderCompID (tag 49) the probe sends.</param>
/// <param name="TargetCompId">TargetCompID (tag 56) the probe sends.</param>
/// <param name="MsgSeqNum">MsgSeqNum (tag 34) of the last message the probe sent. Kept across reconnects.</param>
/// <param name="LastReceivedSeqNum">MsgSeqNum (tag 34) of the last message received in this connection, or 0 when none arrived.</param>
/// <param name="NextExpectedSeqNum">MsgSeqNum the probe expects next from the acceptor. Sent as 789 on Logon.</param>
/// <param name="GapFrom">First missing MsgSeqNum while a ResendRequest is pending, or 0.</param>
public sealed record FixSessionSnapshot(
    string SenderCompId,
    string TargetCompId,
    int MsgSeqNum,
    int LastReceivedSeqNum,
    int NextExpectedSeqNum = 1,
    int GapFrom = 0);

/// <summary>Implemented by adapters that keep a FIX session and expose it in the state snapshot.</summary>
public interface IFixSessionSource
{
    /// <summary>Current session, or null when no session was opened since the last disconnect.</summary>
    FixSessionSnapshot? Session { get; }
}
