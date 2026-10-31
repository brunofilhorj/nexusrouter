using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Particiona pelo ClOrdID (tag 11). Sem o campo, devolve <see langword="null"/>.
/// </summary>
public sealed class ByClOrdIdPartitionStrategy : IKafkaPartitionStrategy
{
    private const int ClOrdIdTag = 11;
    private readonly int _partitionCount;

    /// <summary>
    /// Cria a estratégia para um tópico com a quantidade de partições informada.
    /// </summary>
    /// <param name="partitionCount">Quantidade de partições do tópico. Precisa ser positiva.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionCount"/> não é positivo.</exception>
    public ByClOrdIdPartitionStrategy(int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        _partitionCount = partitionCount;
    }

    /// <summary>
    /// Hash estável do ClOrdID, módulo a quantidade de partições.
    /// </summary>
    /// <param name="payload">Payload com mensagem parseada. Sem parse ou sem a tag 11, o retorno é <see langword="null"/>.</param>
    /// <returns>Partição no intervalo <c>[0, partitionCount)</c>, ou <see langword="null"/>.</returns>
    /// <remarks>Zero allocation. O hash é FNV-1a de 32 bits sobre os bytes do campo.</remarks>
    public int? GetPartition(DispatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.TryGetParsedMessage(out FixMessage message)
            || !message.TryGetField(ClOrdIdTag, out ReadOnlySpan<byte> clOrdId)
            || clOrdId.IsEmpty)
        {
            return null;
        }

        uint hash = 2166136261;
        for (int i = 0; i < clOrdId.Length; i++)
        {
            hash ^= clOrdId[i];
            hash *= 16777619;
        }

        return (int)(hash % (uint)_partitionCount);
    }
}
