using BenchmarkDotNet.Attributes;

using NexusRouter.Domain.Models;
using NexusRouter.Router;

namespace NexusRouter.Benchmarks;

/// <summary>
/// Mede o custo de tokenização de um frame FIX 4.4 com 20 campos.
/// </summary>
/// <remarks>
/// Alocação esperada: 0 B por iteração — o buffer de campos é pré-alocado
/// no <see cref="GlobalSetup"/> e reutilizado a cada chamada.
/// </remarks>
[MemoryDiagnoser]
public class FixTokenizerBenchmark
{
    // Frame FIX 4.4 com aproximadamente 20 campos (New Order Single).
    private static readonly byte[] Fix44Frame = System.Text.Encoding.ASCII.GetBytes(
        "8=FIX.4.4\x019=200\x0135=D\x0149=CLIENT1\x0156=B3ENTRYPOINT\x0134=1\x01" +
        "52=20240101-12:00:00.000\x01115=NEXUS\x0111=ORD001\x0121=1\x01" +
        "55=PETR4\x0154=1\x0160=20240101-12:00:00.000\x0138=100\x0140=2\x01" +
        "44=25.50\x0159=0\x01100=BVMF\x01207=BVSP\x01" +
        "10=217\x01");

    // Memória pré-alocada do frame, sem alocação por iteração.
    private ReadOnlyMemory<byte> _frameMemory;

    // Buffer de campos pré-alocado: reutilizado a cada iteração.
    private FixField[] _fieldBuffer = [];

    /// <summary>
    /// Pré-aloca os recursos necessários antes das iterações.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _frameMemory = Fix44Frame.AsMemory();
        _fieldBuffer = new FixField[32]; // capacidade acima dos 20 campos do frame de teste
    }

    /// <summary>
    /// Tokeniza o frame FIX sem alocação heap por iteração.
    /// </summary>
    [Benchmark]
    public void Tokenize()
    {
        FixTokenizer.Tokenize(_frameMemory, _fieldBuffer.AsSpan(), out _);
    }
}
