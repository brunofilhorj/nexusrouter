using BenchmarkDotNet.Running;

namespace NexusRouter.Benchmarks;

/// <summary>
/// Entry point dos benchmarks de hot path.
/// </summary>
/// <remarks>
/// Executar em modo Release: <c>dotnet run -c Release --project tests/NexusRouter.Benchmarks</c>.
/// Para execução rápida de validação: adicionar <c>-- --filter "*" --job short</c>.
/// Artefatos gerados em <c>BenchmarkDotNet.Artifacts/</c> (ignorados pelo .gitignore).
/// </remarks>
internal static class Program
{
    private static void Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args);
    }
}
