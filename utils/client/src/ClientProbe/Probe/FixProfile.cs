namespace ClientProbe.Probe;

/// <summary>FIX initiator profile.</summary>
/// <param name="Host">Acceptor host.</param>
/// <param name="Port">Acceptor port.</param>
/// <param name="SenderCompId">SenderCompID (tag 49) sent by the probe.</param>
/// <param name="TargetCompId">TargetCompID (tag 56) expected by the acceptor.</param>
/// <param name="HeartBtInt">Heartbeat interval, in seconds.</param>
/// <param name="ResetSeqNum">
/// Sends Logon with ResetSeqNumFlag 141=Y and MsgSeqNum 1, restarting both sides at 1.
/// False continues the stored sequence (recovery test).
/// </param>
/// <param name="SendNextExpected">
/// Sends NextExpectedMsgSeqNum 789 on Logon, so the acceptor resends what the probe missed.
/// </param>
/// <param name="ReplayMode">
/// Tag 19001 on Logon. Null omits the tag and leaves the acceptor default in place.
/// </param>
public sealed record FixProfile(
    string Host,
    int Port,
    string SenderCompId,
    string TargetCompId,
    int HeartBtInt,
    bool ResetSeqNum = false,
    bool SendNextExpected = true,
    ProbeReplayMode? ReplayMode = null)
    : ProbeProfile
{
    /// <inheritdoc />
    public override ProbeTransport Transport => ProbeTransport.Fix;

    /// <inheritdoc />
    public override string Endpoint => $"{Host}:{Port}";
}
