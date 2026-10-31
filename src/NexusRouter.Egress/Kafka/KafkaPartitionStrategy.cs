namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Como o producer escolhe a partição do tópico.
/// </summary>
public enum KafkaPartitionStrategy
{
    /// <summary>
    /// O cliente Kafka escolhe a partição.
    /// </summary>
    None = 0,

    /// <summary>
    /// Partição estável a partir de ClOrdID (tag 11), quando o campo existe.
    /// </summary>
    ByClOrdId = 1,
}
