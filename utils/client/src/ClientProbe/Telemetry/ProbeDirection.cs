namespace ClientProbe.Telemetry;

/// <summary>Whether a telemetry frame was sent by the probe or received from the peer.</summary>
public enum ProbeDirection
{
    /// <summary>The probe received the frame.</summary>
    In,

    /// <summary>The probe sent the frame.</summary>
    Out,
}
