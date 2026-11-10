using System.Globalization;

using NexusRouter.Domain.Models;

namespace NexusRouter.Egress;

/// <summary>
/// Header de identidade estável de uma mensagem publicada em Kafka ou RabbitMQ.
/// </summary>
/// <remarks>
/// O valor é <c>stream:época:seq</c>. O primeiro campo é o id estável da sessão de origem
/// (o stream de ingress em <see cref="LogRef"/>). Ele não muda no replay depois de um restart,
/// ao contrário do texto de <see cref="SessionId"/>, que o catch-up não recupera.
/// </remarks>
public static class BrokerIdentity
{
    /// <summary>
    /// Nome do record header no Kafka e do header de mensagem no RabbitMQ.
    /// </summary>
    public const string HeaderName = "nexus-id";

    /// <summary>
    /// Monta o header quando o frame tem posição no log.
    /// </summary>
    /// <param name="logRef">Posição do frame. Seq não positivo ou stream zero não geram header.</param>
    /// <returns>O texto do header, ou nulo quando não há identidade estável.</returns>
    public static string? Format(LogRef logRef)
    {
        if (logRef.InSeq <= 0 || logRef.Stream == 0)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{logRef.Stream}:{logRef.Epoch}:{logRef.InSeq}");
    }
}
