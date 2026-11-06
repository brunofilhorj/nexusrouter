using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Rabbit;

/// <summary>Placeholder Rabbit adapter. Always faults with "não implementado" until its task replaces it.</summary>
public sealed class RabbitConsumerAdapter : ProbeAdapterBase
{
    /// <summary>Creates the adapter.</summary>
    /// <param name="publisher">Telemetry entry point.</param>
    /// <param name="options">Probe options.</param>
    /// <param name="time">Clock.</param>
    public RabbitConsumerAdapter(ITelemetryPublisher publisher, IOptions<ClientProbeOptions> options, TimeProvider time)
        : base(ProbeTransport.Rabbit, publisher, options, time)
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
