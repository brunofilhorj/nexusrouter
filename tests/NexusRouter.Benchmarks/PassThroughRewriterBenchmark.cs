using BenchmarkDotNet.Attributes;

using NexusRouter.Router;

namespace NexusRouter.Benchmarks;

/// <summary>
/// Mede o custo de reescrita de MsgSeqNum, BodyLength e CheckSum de um frame FIX 4.4.
/// </summary>
/// <remarks>
/// Alocação esperada: 0 B por iteração — toda a operação é feita sobre <see cref="Span{T}"/>
/// pré-alocados no <see cref="GlobalSetup"/>.
/// </remarks>
[MemoryDiagnoser]
public class PassThroughRewriterBenchmark
{
    // Frame FIX 4.4 mínimo: New Order Single (D) com MsgSeqNum=1.
    // O campo CheckSum está correto para este frame.
    private static readonly byte[] TemplateFix44 = System.Text.Encoding.ASCII.GetBytes(
        "8=FIX.4.4\x019=152\x0135=D\x0149=CLIENT1\x0156=B3ENTRYPOINT\x0134=1\x01" +
        "52=20240101-12:00:00.000\x01115=NEXUS\x0111=ORD001\x0121=1\x01" +
        "55=PETR4\x0154=1\x0160=20240101-12:00:00.000\x0138=100\x0140=2\x0144=25.50\x0159=0\x01" +
        "10=157\x01");

    // Buffer de trabalho: uma cópia do template, reutilizado a cada iteração.
    private byte[] _workBuffer = [];
    private byte[] _destination = [];

    /// <summary>
    /// Aloca o buffer de trabalho uma única vez antes das iterações.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _workBuffer = new byte[TemplateFix44.Length];
        TemplateFix44.CopyTo(_workBuffer, 0);
        _destination = new byte[TemplateFix44.Length + PassThroughRewriter.MaxGrowthBytes];
    }

    /// <summary>
    /// Reescreve MsgSeqNum e recalcula CheckSum in-place, sem alocação.
    /// </summary>
    [Benchmark]
    public void Rewrite()
    {
        // Restaura o frame ao template antes de cada reescrita para manter a validade do campo.
        TemplateFix44.CopyTo(_workBuffer, 0);
        PassThroughRewriter.Rewrite(_workBuffer.AsSpan(), newSeqNum: 7L);
    }

    /// <summary>
    /// Copia o frame para o destino com um MsgSeqNum de largura diferente e recalcula tags 9 e 10, sem alocação.
    /// </summary>
    [Benchmark]
    public int TryRewriteToWithResize()
    {
        PassThroughRewriter.TryRewriteTo(TemplateFix44, _destination, newSeqNum: 37582L, out int written);
        return written;
    }
}
