using System.Buffers;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Ingress.Decoders;

/// <summary>
/// Enquadra FIX 5.0 e FIXT 1.1 pelo BeginString e pelo trailer de checksum.
/// </summary>
public sealed class Fix50FrameDecoder : ISessionFrameDecoder
{
    /// <summary>
    /// Tenta separar uma mensagem <c>FIX.5.0</c> ou <c>FIXT.1.1</c> do buffer.
    /// </summary>
    /// <param name="buffer">Bytes ainda não consumidos. Em sucesso, avança além do frame.</param>
    /// <param name="frame">Frame separado, ou o valor default quando ainda não há mensagem completa.</param>
    /// <returns><see langword="true"/> quando um frame completo foi extraído.</returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// O <see cref="IngressFrame.SessionId"/> fica vazio.
    /// </remarks>
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame) =>
        FixFrameScanner.TryDecode(ref buffer, FixFrameScanner.BeginStringKind.Fix50OrFixt, out frame);
}
