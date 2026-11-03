using Microsoft.Extensions.Hosting;

using NexusRouter.Host.Extensions;

using Serilog;

namespace NexusRouter.Host;

/// <summary>
/// Entry point do processo NexusRouter.
/// </summary>
/// <remarks>
/// A ordem de registro dos tiers define a ordem inversa de shutdown:
/// Ingress para primeiro → Egress drena → Audit writer drena por último.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Bootstrap do Serilog via arquivo de configuração, antes do host ser construído,
        // para que erros de inicialização (falha de validação, exceções de DI) também sejam capturados.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            IHost host = BuildHost(args);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host encerrou inesperadamente");
            return 1;
        }
        finally
        {
            // Garante o flush do Serilog mesmo em saída anormal.
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static IHost BuildHost(string[] args) =>
        Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder(args)
            .UseSerilog((context, services, configuration) =>
                configuration.ReadFrom.Configuration(context.Configuration))
            .ConfigureServices((context, services) =>
            {
                // A ordem de chamada define a ordem de registro de IHostedService.
                // O Generic Host para os IHostedServices na ordem inversa ao registro,
                // portanto: Audit último a registrar → primeiro a parar no shutdown.
                // Sequência de shutdown: Ingress → Egress → Audit.
                services
                    .AddNexusRouterAudit(context.Configuration)
                    .AddNexusRouterEgress(context.Configuration)
                    .AddNexusRouterRouter(context.Configuration)
                    .AddNexusRouterIngress(context.Configuration);
            })
            .UseConsoleLifetime()
            .Build();

    /// <summary>
    /// Captura exceções não tratadas de threads de background e de pool de threads.
    /// Registra via Serilog antes que o processo encerre.
    /// </summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Exceção não tratada. Terminando={IsTerminating}", e.IsTerminating);
        }
        else
        {
            Log.Fatal("Exceção não tratada (tipo desconhecido). Terminando={IsTerminating}", e.IsTerminating);
        }

        // Flush síncrono: o processo pode sair imediatamente após este handler.
        Log.CloseAndFlush();

        if (e.IsTerminating)
        {
            Environment.Exit(1);
        }
    }
}
