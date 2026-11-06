namespace ClientProbe.Probe;

/// <summary>FIX initiator profile.</summary>
/// <param name="Host">Acceptor host.</param>
/// <param name="Port">Acceptor port.</param>
/// <param name="SenderCompId">SenderCompID (tag 49) sent by the probe.</param>
/// <param name="TargetCompId">TargetCompID (tag 56) expected by the acceptor.</param>
/// <param name="HeartBtInt">Heartbeat interval, in seconds.</param>
public sealed record FixProfile(string Host, int Port, string SenderCompId, string TargetCompId, int HeartBtInt)
    : ProbeProfile
{
    /// <inheritdoc />
    public override ProbeTransport Transport => ProbeTransport.Fix;

    /// <inheritdoc />
    public override string Endpoint => $"{Host}:{Port}";
}
