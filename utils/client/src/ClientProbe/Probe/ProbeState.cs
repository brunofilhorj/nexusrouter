namespace ClientProbe.Probe;

/// <summary>Connection state of one adapter.</summary>
public enum ProbeState
{
    /// <summary>No connection is open.</summary>
    Disconnected,

    /// <summary>A connection is being opened.</summary>
    Connecting,

    /// <summary>The connection is open and delivering frames.</summary>
    Live,

    /// <summary>The last connect or the live connection failed. See <see cref="IProbeAdapter.LastError"/>.</summary>
    Faulted,
}
