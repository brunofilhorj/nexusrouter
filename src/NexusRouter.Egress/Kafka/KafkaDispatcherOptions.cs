namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Configuração de um producer Kafka. Senha e usuário não ficam aqui, só o nome das variáveis.
/// </summary>
public sealed class KafkaDispatcherOptions
{
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
    /// Capacidade do channel de entrada deste dispatcher.
    /// </summary>
    public int ChannelCapacity { get; init; } = 16384;

    /// <summary>
    /// Estratégia de partição. Uma instância injetada no dispatcher tem precedência.
    /// </summary>
    public KafkaPartitionStrategy PartitionStrategy { get; init; } = KafkaPartitionStrategy.None;

    /// <summary>
    /// Quantidade de partições usada por <see cref="KafkaPartitionStrategy.ByClOrdId"/>.
    /// </summary>
    public int PartitionCount { get; init; }

    /// <summary>
    /// Rejeita combinação inválida de protocolo, SASL e nomes de variáveis.
    /// </summary>
    /// <exception cref="ArgumentException">Falta tópico, broker, client id ou a combinação SASL é inválida.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Acks, capacidade ou quantidade de partições é inválida.</exception>
    public void EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(Topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        if (Acks is not (0 or 1 or -1))
        {
            throw new ArgumentOutOfRangeException(nameof(Acks), Acks, "Acks aceita 0, 1 ou -1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChannelCapacity);

        bool sasl = SecurityProtocol is KafkaSecurityProtocol.SaslPlaintext or KafkaSecurityProtocol.SaslSsl;
        if (sasl && SaslMechanism == KafkaSaslMechanism.None)
        {
            throw new ArgumentException("SaslSsl e SaslPlaintext exigem um SaslMechanism diferente de None.");
        }

        if (!sasl && SaslMechanism != KafkaSaslMechanism.None)
        {
            throw new ArgumentException("Plaintext e Ssl não usam SaslMechanism.");
        }

        if (UsesPassword(SaslMechanism)
            && (string.IsNullOrWhiteSpace(SaslUsernameEnvVar) || string.IsNullOrWhiteSpace(SaslPasswordEnvVar)))
        {
            throw new ArgumentException("PLAIN e SCRAM exigem SaslUsernameEnvVar e SaslPasswordEnvVar.");
        }

        if (PartitionStrategy == KafkaPartitionStrategy.ByClOrdId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PartitionCount);
        }
    }

    /// <summary>
    /// Indica se o mecanismo autentica com usuário e senha.
    /// </summary>
    /// <param name="mechanism">Mecanismo configurado.</param>
    /// <returns><see langword="true"/> para PLAIN e SCRAM.</returns>
    public static bool UsesPassword(KafkaSaslMechanism mechanism) =>
        mechanism is KafkaSaslMechanism.Plain or KafkaSaslMechanism.ScramSha256 or KafkaSaslMechanism.ScramSha512;
}
