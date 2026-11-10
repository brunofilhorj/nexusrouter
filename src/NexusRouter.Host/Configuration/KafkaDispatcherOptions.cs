using Microsoft.Extensions.Options;

using NexusRouter.Egress;
using NexusRouter.Egress.Kafka;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Producer Kafka. Usuário e senha SASL não ficam aqui, só o nome das variáveis.
/// </summary>
/// <remarks>
/// O default é PLAINTEXT, que alcança o Kafka local. MSK com SASL/SCRAM usa
/// <see cref="KafkaSecurityProtocol.SaslSsl"/> e <see cref="KafkaSaslMechanism.ScramSha512"/>.
/// </remarks>
public sealed class KafkaDispatcherOptions : DispatcherOptions, IValidateOptions<KafkaDispatcherOptions>
{
    /// <inheritdoc />
    public override DispatcherType Type => DispatcherType.Kafka;

    /// <summary>
    /// Lista <c>host:porta</c> dos brokers.
    /// </summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// Tópico de publicação.
    /// </summary>
    public required string Topic { get; init; }

    /// <summary>
    /// Client id do producer.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Confirmação do broker: 0, 1 ou -1 (todos os replicas).
    /// </summary>
    public int Acks { get; init; }

    /// <summary>
    /// Quando o cursor durável avança. <see cref="CursorAckMode.OnBrokerAck"/> não combina com <see cref="Acks"/> zero.
    /// </summary>
    public CursorAckMode CursorAckMode { get; init; } = CursorAckMode.OnBrokerAck;

    /// <summary>
    /// Protocolo de transporte e autenticação.
    /// </summary>
    public KafkaSecurityProtocol SecurityProtocol { get; init; } = KafkaSecurityProtocol.Plaintext;

    /// <summary>
    /// Mecanismo SASL. <see cref="KafkaSaslMechanism.None"/> quando o protocolo não usa SASL.
    /// </summary>
    public KafkaSaslMechanism SaslMechanism { get; init; } = KafkaSaslMechanism.None;

    /// <summary>
    /// Nome da variável com o usuário SASL. Obrigatório para PLAIN e SCRAM.
    /// </summary>
    public string? SaslUsernameEnvVar { get; init; }

    /// <summary>
    /// Nome da variável com a senha SASL. Obrigatório para PLAIN e SCRAM.
    /// </summary>
    public string? SaslPasswordEnvVar { get; init; }

    /// <summary>
    /// Caminho do CA para o producer. Nulo usa o trust store do sistema.
    /// </summary>
    public string? SslCaLocation { get; init; }

    /// <summary>
    /// Quando <see langword="false"/>, o producer não valida o certificado do broker.
    /// </summary>
    public bool EnableSslCertificateVerification { get; init; } = true;

    /// <summary>
    /// Como o producer escolhe a partição. <see cref="KafkaPartitionStrategy.ByClOrdId"/> exige <see cref="PartitionCount"/>.
    /// </summary>
    public KafkaPartitionStrategy PartitionStrategy { get; init; } = KafkaPartitionStrategy.None;

    /// <summary>
    /// Quantidade de partições usada por <see cref="KafkaPartitionStrategy.ByClOrdId"/>.
    /// </summary>
    public int PartitionCount { get; init; }

    /// <summary>
    /// Capacidade do channel de entrada deste dispatcher.
    /// </summary>
    public int ChannelCapacity { get; init; } = 16384;

    /// <summary>
    /// <inheritdoc />
    public override ValidateOptionsResult Validate(string? name) => Validate(name, this);

    /// <summary>
    /// Confere broker, SASL e partição. As combinações acompanham o <c>EnsureValid</c> do producer.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Dispatcher já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, KafkaDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        AddIdentity(options, failures);
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.BootstrapServers, nameof(options.BootstrapServers)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.Topic, nameof(options.Topic)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.ClientId, nameof(options.ClientId)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.SecurityProtocol, nameof(options.SecurityProtocol)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.SaslMechanism, nameof(options.SaslMechanism)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.PartitionStrategy, nameof(options.PartitionStrategy)));
        if (options.Acks is not (0 or 1 or -1))
        {
            failures.Add("Acks aceita 0, 1 ou -1.");
        }

        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.CursorAckMode, nameof(options.CursorAckMode)));
        if (options.Acks == 0 && options.CursorAckMode == CursorAckMode.OnBrokerAck)
        {
            failures.Add("Acks=0 só combina com CursorAckMode OnEnqueue.");
        }

        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.ChannelCapacity, nameof(options.ChannelCapacity)));
        if (!string.IsNullOrWhiteSpace(options.SslCaLocation))
        {
            ConfigurationChecks.Add(failures, ConfigurationChecks.Required(options.SslCaLocation, nameof(options.SslCaLocation)));
        }

        AddSasl(options, failures);
        if (options.PartitionStrategy == KafkaPartitionStrategy.ByClOrdId)
        {
            ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.PartitionCount, nameof(options.PartitionCount)));
        }

        return ConfigurationChecks.From(failures);
    }

    private static void AddSasl(KafkaDispatcherOptions options, List<string> failures)
    {
        if (!Enum.IsDefined(options.SecurityProtocol) || !Enum.IsDefined(options.SaslMechanism))
        {
            return;
        }

        bool sasl = options.SecurityProtocol is KafkaSecurityProtocol.SaslPlaintext or KafkaSecurityProtocol.SaslSsl;
        if (sasl && options.SaslMechanism == KafkaSaslMechanism.None)
        {
            failures.Add("SaslSsl e SaslPlaintext exigem um SaslMechanism diferente de None.");
        }

        if (!sasl && options.SaslMechanism != KafkaSaslMechanism.None)
        {
            failures.Add("Plaintext e Ssl não usam SaslMechanism.");
        }

        bool password = options.SaslMechanism is KafkaSaslMechanism.Plain
            or KafkaSaslMechanism.ScramSha256
            or KafkaSaslMechanism.ScramSha512;
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.EnvVar(options.SaslUsernameEnvVar, nameof(options.SaslUsernameEnvVar), password));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.EnvVar(options.SaslPasswordEnvVar, nameof(options.SaslPasswordEnvVar), password));
    }
}
