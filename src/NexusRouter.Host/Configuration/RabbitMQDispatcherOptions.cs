using Microsoft.Extensions.Options;

using NexusRouter.Egress;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Publisher RabbitMQ. Usuário e senha não ficam aqui, só o nome das variáveis.
/// </summary>
public sealed class RabbitMQDispatcherOptions : DispatcherOptions, IValidateOptions<RabbitMQDispatcherOptions>
{
    /// <inheritdoc />
    public override DispatcherType Type => DispatcherType.RabbitMQ;

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
    /// Formato do corpo publicado: <c>Json</c> (default) ou <c>Raw</c> (frame FIX bruto).
    /// </summary>
    public BrokerPayloadEncoding PayloadEncoding { get; init; } = BrokerPayloadEncoding.Json;

    /// <summary>
    /// Tags FIX carimbadas nos headers da mensagem. Vazio mantém só o header de idempotência.
    /// </summary>
    public List<int> HeaderFields { get; init; } = [];

    /// <inheritdoc />
    public override ValidateOptionsResult Validate(string? name) => Validate(name, this);

    /// <summary>
    /// Confere host, exchange, porta e os nomes das variáveis.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Dispatcher já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, RabbitMQDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        AddIdentity(options, failures);
        ConfigurationChecks.Add(failures, ConfigurationChecks.Host(options.Host, nameof(options.Host)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Port(options.Port, nameof(options.Port)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.VirtualHost, nameof(options.VirtualHost)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.EnvVar(options.UsernameEnvVar, nameof(options.UsernameEnvVar), required: true));
        ConfigurationChecks.Add(failures, ConfigurationChecks.EnvVar(options.PasswordEnvVar, nameof(options.PasswordEnvVar), required: true));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.Exchange, nameof(options.Exchange)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.ChannelCapacity, nameof(options.ChannelCapacity)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.CursorAckMode, nameof(options.CursorAckMode)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.PayloadEncoding, nameof(options.PayloadEncoding)));
        ConfigurationChecks.Add(failures, BrokerHeaderFields.TagsError(options.HeaderFields));
        ConfigurationChecks.Add(failures, BrokerHeaderFields.RoutingKeyTemplateError(options.RoutingKeyTemplate, options.HeaderFields));
        return ConfigurationChecks.From(failures);
    }
}
