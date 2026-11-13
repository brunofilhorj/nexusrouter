namespace ClientProbe.Probe;

/// <summary>
/// What the acceptor should deliver when the Logon opens a new epoch.
/// Wire values match tag 19001: <c>1</c> = <see cref="FromLogon"/>, <c>2</c> = <see cref="StartOfDay"/>.
/// </summary>
public enum ProbeReplayMode
{
    /// <summary>Messages that arrive after the Logon. Tag 19001=1.</summary>
    FromLogon = 1,

    /// <summary>Every application message from the trading day. Tag 19001=2.</summary>
    StartOfDay = 2,
}
