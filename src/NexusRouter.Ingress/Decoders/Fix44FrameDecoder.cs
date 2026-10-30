using System.Buffers;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Ingress.Decoders;

/// <summary>
/// Enquadra mensagens FIX 4.x pelo BeginString <c>8=FIX.4.x</c> e pelo trailer de checksum.
/// </summary>
public sealed class Fix44FrameDecoder : ISessionFrameDecoder
{
    /// <summary>
    /// Tenta separar uma mensagem FIX 4.x do início ainda não consumido do buffer.
    /// </summary>
    /// <param name="buffer">Bytes ainda não consumidos. Em sucesso, avança além do frame.</param>
    /// <param name="frame">Frame separado, ou o valor default quando ainda não há mensagem completa.</param>
    /// <returns><see langword="true"/> quando um frame completo foi extraído.</returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// Aceita BeginString <c>FIX.4.</c> seguido de um dígito. O <see cref="IngressFrame.SessionId"/> fica vazio.
    /// </remarks>
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame) =>
        FixFrameScanner.TryDecode(ref buffer, FixFrameScanner.BeginStringKind.Fix4, out frame);
}
