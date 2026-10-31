using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Escolhe a partição Kafka de um payload. <see langword="null"/> deixa a escolha com o cliente.
/// </summary>
public interface IKafkaPartitionStrategy
{
    /// <summary>
    /// Calcula a partição do payload.
    /// </summary>
    /// <param name="payload">Mensagem já roteada.</param>
    /// <returns>Número da partição, ou <see langword="null"/> para o particionamento do cliente.</returns>
    /// <remarks>Zero allocation quando a estratégia não materializa o ClOrdID. Seguro para a task de consumo.</remarks>
    int? GetPartition(DispatchPayload payload);
}
