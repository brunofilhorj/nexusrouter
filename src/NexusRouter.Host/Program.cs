using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NexusRouter.Host.Extensions;

using OpenTelemetry.Metrics;

using Serilog;

namespace NexusRouter.Host;

/// <summary>
/// Entry point do processo NexusRouter.
/// </summary>
/// <remarks>
/// Usa <see cref="WebApplication"/> para expor o endpoint <c>/metrics</c> (scrape Prometheus)
/// enquanto mantém o pipeline FIX intacto via Generic Host.
/// A ordem de registro dos tiers define a ordem inversa de shutdown:
/// Ingress para primeiro → Egress drena → Audit writer drena por último.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Bootstrap do Serilog antes do host, para capturar erros de inicialização (DI, validação).
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            WebApplication app = BuildWebApplication(args);
            await app.RunAsync().ConfigureAwait(false);
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

    private static WebApplication BuildWebApplication(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Serilog: lê configuração do appsettings.json.
        builder.Host.UseSerilog((context, services, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration));

        // OpenTelemetry: registra o SDK com o exporter Prometheus.
        // O SDK coleta métricas do Meter "NexusRouter" (NexusRouterMetrics).
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("NexusRouter")
                    .AddPrometheusExporter();
            });

        // Tiers do NexusRouter — ordem define sequência inversa de shutdown.
        // Sequência de shutdown: Ingress → Egress → Audit (Audit último a registrar, primeiro a parar).
        builder.Services
            .AddNexusRouterAudit(builder.Configuration)
            .AddNexusRouterEgress(builder.Configuration)
            .AddNexusRouterRouter(builder.Configuration)
            .AddNexusRouterIngress(builder.Configuration);

        WebApplication app = builder.Build();

        // Endpoint de scrape Prometheus — não interfere no hot path FIX.
        app.MapPrometheusScrapingEndpoint("/metrics");

        return app;
    }

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
