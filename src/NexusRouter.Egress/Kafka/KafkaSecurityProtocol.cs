namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Protocolo de segurança do producer Kafka. O valor vai direto para <c>ProducerConfig</c>.
/// </summary>
public enum KafkaSecurityProtocol
{
    /// <summary>
    /// TCP sem TLS e sem SASL. É o default do Kafka local.
    /// </summary>
    Plaintext = 0,

    /// <summary>
    /// TLS sem SASL.
    /// </summary>
    Ssl = 1,

    /// <summary>
    /// SASL sobre TCP, sem TLS.
    /// </summary>
    SaslPlaintext = 2,

    /// <summary>
    /// SASL sobre TLS.
    /// </summary>
    SaslSsl = 3,
}
