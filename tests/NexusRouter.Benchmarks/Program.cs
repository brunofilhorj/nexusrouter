using BenchmarkDotNet.Running;

namespace NexusRouter.Benchmarks;

/// <summary>
/// Entry point dos benchmarks de hot path.
/// </summary>
/// <remarks>
/// Executar em modo Release: <c>dotnet run -c Release --project tests/NexusRouter.Benchmarks</c>.
/// Sem argumentos, executa todos os benchmarks da assembly.
/// Para execução rápida de validação: adicionar <c>-- --filter "*" --job short</c>.
/// Artefatos gerados em <c>BenchmarkDotNet.Artifacts/</c> (ignorados pelo .gitignore).
/// </remarks>
internal static class Program
{
    private static void Main(string[] args)
    {
        string[] benchmarkArgs = args.Length == 0 ? ["--filter", "*"] : args;

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(benchmarkArgs);
    }
}
