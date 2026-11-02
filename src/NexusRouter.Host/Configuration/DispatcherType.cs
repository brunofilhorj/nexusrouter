namespace NexusRouter.Host.Configuration;

/// <summary>
/// Discriminador de <see cref="DispatcherOptions"/> na lista <c>Dispatchers</c>.
/// </summary>
public enum DispatcherType
{
    /// <summary>
    /// Producer Kafka. O valor na configuração é <c>Kafka</c>.
    /// </summary>
    Kafka = 0,

    /// <summary>
    /// Publisher RabbitMQ. O valor na configuração é <c>RabbitMQ</c>.
    /// </summary>
    RabbitMQ = 1,

    /// <summary>
    /// Acceptor FIX. O valor na configuração é <c>FixOutbound</c>.
    /// </summary>
    FixOutbound = 2,

    /// <summary>
    /// Acceptor WebSocket. O valor na configuração é <c>WebSocket</c>.
    /// </summary>
    WebSocket = 3,
}
