using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Fix;

/// <summary>Placeholder Fix adapter. Always faults with "não implementado" until its task replaces it.</summary>
public sealed class FixInitiatorAdapter : ProbeAdapterBase
{
    /// <summary>Creates the adapter.</summary>
    /// <param name="publisher">Telemetry entry point.</param>
    /// <param name="options">Probe options.</param>
    /// <param name="time">Clock.</param>
    public FixInitiatorAdapter(ITelemetryPublisher publisher, IOptions<ClientProbeOptions> options, TimeProvider time)
        : base(ProbeTransport.Fix, publisher, options, time)
    {
    }

    /// <inheritdoc />
    public override Task ConnectAsync(ProbeProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SetState(ProbeState.Connecting);
        SetEndpoint(profile.Endpoint);
        SetState(ProbeState.Faulted, "não implementado");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task DisconnectAsync(CancellationToken ct)
    {
        SetState(ProbeState.Disconnected);
        SetEndpoint("");
        return Task.CompletedTask;
    }
}
