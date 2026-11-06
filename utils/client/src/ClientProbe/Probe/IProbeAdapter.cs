namespace ClientProbe.Probe;

/// <summary>
/// One egress transport. Implementations never throw from a background loop to the host:
/// a failure moves <see cref="State"/> to <see cref="ProbeState.Faulted"/> and fills <see cref="LastError"/>.
/// </summary>
public interface IProbeAdapter
{
    /// <summary>Transport served by this adapter. Constant for the adapter lifetime.</summary>
    ProbeTransport Transport { get; }

    /// <summary>Current connection state. Safe to read from any thread.</summary>
    ProbeState State { get; }

    /// <summary>Effective endpoint of the last connect attempt. Empty after disconnect.</summary>
    string Endpoint { get; }

    /// <summary>Last error, or null when there is none.</summary>
    string? LastError { get; }

    /// <summary>Counters for this adapter. The same instance is returned on every call.</summary>
    ProbeCounters Counters { get; }

    /// <summary>
    /// Opens a connection with the given profile. The profile is the subtype for <see cref="Transport"/>.
    /// A connect failure is reported through <see cref="State"/>, not by throwing.
    /// </summary>
    /// <param name="profile">Profile of this transport.</param>
    /// <param name="ct">Cancels the connect attempt.</param>
    /// <returns>A task that completes when the attempt has finished.</returns>
    Task ConnectAsync(ProbeProfile profile, CancellationToken ct);

    /// <summary>Closes the connection. Idempotent: calling it while disconnected has no effect.</summary>
    /// <param name="ct">Cancels the wait for the close.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    Task DisconnectAsync(CancellationToken ct);
}
