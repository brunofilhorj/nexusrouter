using System.Buffers;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Ingress.Decoders;

/// <summary>
/// Ponto de extensão para framing SBE. Ainda não decodifica.
/// </summary>
public sealed class SbeFrameDecoder : ISessionFrameDecoder
{
    /// <summary>
    /// Não decodifica SBE.
    /// </summary>
    /// <param name="buffer">Bytes ainda não consumidos. Não é avançado.</param>
    /// <param name="frame">Valor default.</param>
    /// <returns>Não retorna.</returns>
    /// <exception cref="NotImplementedException">SBE ainda não é suportado.</exception>
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame)
    {
        frame = default;
        throw new NotImplementedException("SBE decoding is a future extension");
    }
}
