using System.Text;

namespace ClientProbe.Probe;

/// <summary>Rabbit consumer profile.</summary>
/// <param name="Host">Broker host.</param>
/// <param name="Port">Broker port.</param>
/// <param name="VirtualHost">Virtual host.</param>
/// <param name="Exchange">Exchange the router publishes to.</param>
/// <param name="RoutingKey">Routing key used for the queue binding.</param>
/// <param name="UsernameEnvVar">Name of the environment variable that holds the user name.</param>
/// <param name="PasswordEnvVar">Name of the environment variable that holds the password.</param>
/// <param name="Password">Explicit password sent by the modal. Memory only; null falls back to the environment variable.</param>
public sealed record RabbitProfile(
    string Host,
    int Port,
    string VirtualHost,
    string Exchange,
    string RoutingKey,
    string UsernameEnvVar,
    string PasswordEnvVar,
    string? Password = null) : ProbeProfile
{
    /// <inheritdoc />
    public override ProbeTransport Transport => ProbeTransport.Rabbit;

    /// <inheritdoc />
    public override string Endpoint => $"{Host}:{Port}";

    /// <summary>Prints every member except <see cref="Password"/>, so the secret cannot reach a log.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Host = ").Append(Host)
            .Append(", Port = ").Append(Port)
            .Append(", VirtualHost = ").Append(VirtualHost)
            .Append(", Exchange = ").Append(Exchange)
            .Append(", RoutingKey = ").Append(RoutingKey);
        return true;
    }
}
