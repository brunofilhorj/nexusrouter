using System.Buffers;

using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Extrai um frame completo do buffer de leitura de uma sessão.
/// </summary>
public interface ISessionFrameDecoder
{
    /// <summary>
    /// Tenta decodificar um frame completo a partir do início do buffer.
    /// </summary>
    /// <param name="buffer">
    /// Bytes ainda não consumidos. Em caso de sucesso, a sequência é avançada para depois do frame consumido.
    /// </param>
    /// <param name="frame">Frame decodificado quando o método retorna <see langword="true"/>; valor default quando retorna <see langword="false"/>.</param>
    /// <returns><see langword="true"/> quando um frame completo foi extraído; <see langword="false"/> quando o buffer ainda está incompleto.</returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// <paramref name="frame"/> é um <c>ref struct</c> com uma fatia do buffer do pipe e não pode atravessar um <c>await</c>.
    /// </remarks>
    bool TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame);
}
