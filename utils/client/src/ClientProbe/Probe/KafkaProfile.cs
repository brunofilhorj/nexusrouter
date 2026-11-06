namespace ClientProbe.Probe;

/// <summary>Kafka consumer profile.</summary>
/// <param name="BootstrapServers">Broker address list, including port.</param>
/// <param name="Topic">Topic that carries the raw FIX frame.</param>
/// <param name="GroupId">Consumer group id.</param>
/// <param name="AutoOffsetReset">Offset reset policy: <c>Latest</c> or <c>Earliest</c>.</param>
public sealed record KafkaProfile(string BootstrapServers, string Topic, string GroupId, string AutoOffsetReset)
    : ProbeProfile
{
    /// <inheritdoc />
    public override ProbeTransport Transport => ProbeTransport.Kafka;

    /// <inheritdoc />
    public override string Endpoint => BootstrapServers;
}
