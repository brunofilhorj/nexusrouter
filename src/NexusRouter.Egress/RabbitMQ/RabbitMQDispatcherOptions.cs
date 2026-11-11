using NexusRouter.Egress;

namespace NexusRouter.Egress.RabbitMQ;

/// <summary>
/// Configuração de um publisher RabbitMQ. Usuário e senha são nomes de variáveis de ambiente.
/// </summary>
public sealed class RabbitMQDispatcherOptions
{
    /// <summary>
    /// Host do broker.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Porta AMQP. O default do protocolo é 5672.
    /// </summary>
    public int Port { get; init; } = 5672;

    /// <summary>
    /// Virtual host. O default do broker é <c>/</c>.
    /// </summary>
    public string VirtualHost { get; init; } = "/";

    /// <summary>
    /// Nome da variável de ambiente com o usuário.
    /// </summary>
    public required string UsernameEnvVar { get; init; }

    /// <summary>
    /// Nome da variável de ambiente com a senha.
    /// </summary>
    public required string PasswordEnvVar { get; init; }

    /// <summary>
    /// Exchange de publicação.
    /// </summary>
    public required string Exchange { get; init; }

    /// <summary>
    /// Routing key fixa. Vazio publica só na exchange.
    /// </summary>
    public string RoutingKey { get; init; } = string.Empty;

    /// <summary>
    /// Template hierárquico, por exemplo <c>fix.&lt;1&gt;.&lt;55&gt;</c>.
    /// Nulo ou vazio mantém <see cref="RoutingKey"/>. Cada placeholder tem de estar em <see cref="HeaderFields"/>.
    /// </summary>
    public string? RoutingKeyTemplate { get; init; }

    /// <summary>
    /// Capacidade do channel de entrada deste dispatcher.
    /// </summary>
    public int ChannelCapacity { get; init; } = 8192;

    /// <summary>
    /// Prefetch do model. Nulo não chama <c>BasicQos</c>.
    /// </summary>
    public ushort? PrefetchCount { get; init; }

    /// <summary>
    /// Quando o cursor durável avança. <see cref="CursorAckMode.OnBrokerAck"/> liga publisher confirms.
    /// </summary>
    public CursorAckMode CursorAckMode { get; init; } = CursorAckMode.OnBrokerAck;

    /// <summary>
    /// Formato do corpo publicado. <see cref="BrokerPayloadEncoding.Json"/> é o default.
    /// </summary>
    public BrokerPayloadEncoding PayloadEncoding { get; init; } = BrokerPayloadEncoding.Json;

    /// <summary>
    /// Tags FIX carimbadas nos headers da mensagem. Vazio mantém só o header de idempotência.
    /// </summary>
    public IReadOnlyList<int> HeaderFields { get; init; } = [];

    /// <summary>
    /// Rejeita host, porta, exchange ou capacidade inválidos.
    /// </summary>
    /// <exception cref="ArgumentException">Falta host, exchange ou o nome de uma variável.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A porta ou a capacidade é inválida.</exception>
    public void EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(VirtualHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(UsernameEnvVar);
        ArgumentException.ThrowIfNullOrWhiteSpace(PasswordEnvVar);
        ArgumentException.ThrowIfNullOrWhiteSpace(Exchange);
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "A porta deve estar entre 1 e 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChannelCapacity);
        if (!Enum.IsDefined(PayloadEncoding))
        {
            throw new ArgumentOutOfRangeException(nameof(PayloadEncoding), PayloadEncoding, "PayloadEncoding é desconhecido.");
        }

        if (!Enum.IsDefined(CursorAckMode))
        {
            throw new ArgumentOutOfRangeException(nameof(CursorAckMode), CursorAckMode, "CursorAckMode é desconhecido.");
        }

        BrokerHeaderFields.EnsureTags(HeaderFields);
        BrokerHeaderFields.EnsureRoutingKeyTemplate(RoutingKeyTemplate, HeaderFields);
    }
}
