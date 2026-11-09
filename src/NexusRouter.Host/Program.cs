using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NexusRouter.Host.Extensions;
using NexusRouter.Router;
using NexusRouter.SessionLog;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Serilog;

namespace NexusRouter.Host;

/// <summary>
/// Entry point do processo NexusRouter.
/// </summary>
/// <remarks>
/// Usa <see cref="WebApplication"/> para expor o endpoint <c>/metrics</c> (scrape Prometheus)
/// enquanto mantém o pipeline FIX intacto via Generic Host.
/// A ordem de registro dos tiers define a ordem inversa de shutdown:
/// Ingress para primeiro, Egress drena, o log de sessão faz o flush final e grava o marcador limpo, e o Audit drena por último.
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

        // OTLP endpoint — somente configurado quando a seção existe no appsettings.json.
        string? otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];

        // OpenTelemetry: métricas e tracing.
        var otelBuilder = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("NexusRouter"))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(NexusRouterMetrics.MeterName)
                    .AddMeter(SessionLogMetrics.MeterName)
                    .AddPrometheusExporter();
            })
            .WithTracing(tracing =>
            {
                // Registra o ActivitySource do hot path.
                tracing.AddSource(NexusRouterTracing.SourceName);

                // Sampling configurável: padrão 0% (nenhum span criado), para zero overhead em produção.
                // Ative via appsettings.json: "OpenTelemetry:SamplingRatio": 1.0
                double samplingRatio = builder.Configuration.GetValue("OpenTelemetry:SamplingRatio", defaultValue: 0.0);
                if (samplingRatio > 0.0)
                {
                    tracing.SetSampler(new TraceIdRatioBasedSampler(samplingRatio));
                }
                else
                {
                    // AlwaysOffSampler garante que HasListeners() retorne false e o hot path não aloque.
                    tracing.SetSampler(new AlwaysOffSampler());
                }

                // Exporter OTLP: só adicionado quando configurado, para não gerar overhead nem dependência
                // em runtime quando o campo não está presente (NFR-OBS-04).
                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpEndpoint);
                    });
                }
            });

        _ = otelBuilder; // referência explícita para suprimir CS0219 se o compilador otimizar.

        // Registration order is the reverse of shutdown: Ingress, Egress, session log, Audit.
        builder.Services
            .AddNexusRouterAudit(builder.Configuration)
            .AddNexusRouterSessionLog(builder.Configuration)
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
