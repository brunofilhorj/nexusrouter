using System.Buffers;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Frame inbound ainda apoiado no buffer do pipe de leitura.
/// </summary>
/// <remarks>
/// É um <c>ref struct</c>: não pode implementar interfaces, não pode ser campo de uma classe,
/// não pode ser capturado por lambda e não pode atravessar um <c>await</c>.
/// </remarks>
public readonly ref struct IngressFrame
{
    /// <summary>
    /// Cria um frame a partir da fatia já delimitada no buffer do pipe.
    /// </summary>
    /// <param name="sessionId">Sessão que recebeu os bytes.</param>
    /// <param name="rawBuffer">Fatia do buffer do pipe que contém o frame.</param>
    /// <param name="ingressTimestampTicks">Instante de ingresso, em ticks de <see cref="Environment.TickCount64"/> ou de um relógio equivalente.</param>
    /// <param name="frameLength">Comprimento do frame em bytes.</param>
    public IngressFrame(SessionId sessionId, ReadOnlySequence<byte> rawBuffer, long ingressTimestampTicks, int frameLength)
    {
        SessionId = sessionId;
        RawBuffer = rawBuffer;
        IngressTimestampTicks = ingressTimestampTicks;
        FrameLength = frameLength;
    }

    /// <summary>
    /// Sessão que produziu o frame.
    /// </summary>
    public SessionId SessionId { get; }

    /// <summary>
    /// Fatia do buffer do <c>PipeReader</c> que contém o frame.
    /// </summary>
    public ReadOnlySequence<byte> RawBuffer { get; }

    /// <summary>
    /// Instante de ingresso, em ticks do relógio usado pela sessão.
    /// </summary>
    public long IngressTimestampTicks { get; }

    /// <summary>
    /// Comprimento do frame em bytes.
    /// </summary>
    public int FrameLength { get; }
}
