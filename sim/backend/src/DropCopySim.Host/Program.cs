using System.Threading.Channels;
using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Generation;
using DropCopySim.Core.Telemetry;
using DropCopySim.Host;
using DropCopySim.Host.Auth;
using Microsoft.Extensions.Options;
using Serilog;

const string SimPanelCorsPolicy = "SimPanel";

var builder = WebApplication.CreateBuilder(args);

// Structured logging via Serilog, read from DropCopySim config + appsettings (NFR-OBS-01).
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

var configRoot = builder.Configuration.GetSection("DropCopySim");

// Strongly typed options bound from configuration, validated at startup (NFR-CFG-01).
builder.Services
    .AddOptions<AcceptorOptions>()
    .Bind(configRoot.GetSection(AcceptorOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AcceptorOptions>, AcceptorOptionsValidator>();

// Bind the initial SimulationConfig once at startup. The record is an immutable snapshot used
// as the generator's starting config; runtime reconfiguration goes through SimulationController.
// Reconstructed via SimulationConfigBinder so configured collections fully replace the record's
// defaults (the default ConfigurationBinder appends to pre-initialized lists such as ["8"]).
var simulationConfig = SimulationConfigBinder.Build(configRoot.GetSection("Simulation"));
var simulationValidation = new SimulationConfigValidator().Validate(Options.DefaultName, simulationConfig);
if (simulationValidation.Failed)
{
    throw new OptionsValidationException(
        nameof(SimulationConfig),
        typeof(SimulationConfig),
        simulationValidation.Failures);
}

builder.Services.AddSingleton(Options.Create(simulationConfig));

builder.Services
    .AddOptions<TelemetryOptions>()
    .Bind(configRoot.GetSection(TelemetryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<ControlOptions>()
    .Bind(configRoot.GetSection(ControlOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Round counters, shared by the generator, controller and the telemetry snapshot provider.
builder.Services.AddSingleton<SimulationCounters>();

// Telemetry hub: owns the bounded broadcast channel and emits periodic counters snapshots.
builder.Services.AddSingleton<TelemetryHub>(static sp =>
{
    var options = sp.GetRequiredService<IOptions<TelemetryOptions>>();
    var logger = sp.GetRequiredService<ILogger<TelemetryHub>>();
    var counters = sp.GetRequiredService<SimulationCounters>();
    return new TelemetryHub(options, logger, nowUtc => BuildCountersSnapshot(counters, nowUtc));
});
builder.Services.AddHostedService(static sp => sp.GetRequiredService<TelemetryHub>());

// Bridge the generator's ChannelWriter<TelemetryEvent> contract onto the hub.
builder.Services.AddSingleton<ChannelWriter<TelemetryEvent>>(static sp =>
    new TelemetryHubChannelWriter(sp.GetRequiredService<TelemetryHub>()));

// Order-flow generator driven by the controller.
builder.Services.AddSingleton<OrderFlowGenerator>(static sp =>
{
    var config = sp.GetRequiredService<IOptions<SimulationConfig>>().Value;
    var counters = sp.GetRequiredService<SimulationCounters>();
    var telemetry = sp.GetRequiredService<ChannelWriter<TelemetryEvent>>();
    var logger = sp.GetRequiredService<ILogger<OrderFlowGenerator>>();
    var telemetryOptions = sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;
    return new OrderFlowGenerator(
        config,
        counters,
        telemetry,
        logger,
        telemetryOptions.MessageSampleMaxPerSecond);
});

// Simulation state machine and reconfiguration policy owner.
builder.Services.AddSingleton<SimulationController>(static sp =>
{
    var generator = sp.GetRequiredService<OrderFlowGenerator>();
    var counters = sp.GetRequiredService<SimulationCounters>();
    var config = sp.GetRequiredService<IOptions<SimulationConfig>>().Value;
    return new SimulationController(generator, counters, config);
});

// FIX acceptor hosted service (listens for the NexusRouter inbound session).
builder.Services.AddHostedService<FixAcceptorService>();

// CORS for the SimPanel frontend (Control:AllowedOrigins).
var controlOptions = configRoot.GetSection(ControlOptions.SectionName).Get<ControlOptions>() ?? new ControlOptions();
var corsOrigins = controlOptions.AllowedOrigins.Count > 0
    ? controlOptions.AllowedOrigins.ToArray()
    : [ControlOptions.DefaultViteOrigin];
builder.Services.AddCors(options => options.AddPolicy(SimPanelCorsPolicy, policy => policy
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Optional control-API bearer token (FR-API-06): enforced only when the env var named by
// Control:AuthTokenEnvVar has a value; otherwise the API runs in open lab mode.
builder.Services.AddControlTokenAuth();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCors(SimPanelCorsPolicy);
app.UseWebSockets();

// Token gate runs after CORS (so browser preflight OPTIONS is answered by the CORS middleware)
// and before any control endpoint is matched.
app.UseControlTokenAuth();

app.MapGet("/", () => Results.Ok(new { service = "DropCopySim.Host", status = "up" }));

app.Run();

static CountersSnapshot? BuildCountersSnapshot(SimulationCounters counters, DateTimeOffset nowUtc)
{
    if (counters.RoundStartedUtc is null)
    {
        return null;
    }

    var elapsedMs = counters.ElapsedMs(nowUtc);
    var totalSent = counters.TotalSent;
    var ratePerSec = elapsedMs > 0 ? totalSent / (elapsedMs / 1000d) : 0d;

    return new CountersSnapshot
    {
        TimestampUtc = nowUtc,
        TotalSent = totalSent,
        RatePerSec = ratePerSec,
        ByExecType = counters.SnapshotByExecType(),
        Errors = counters.Errors,
        ElapsedMs = elapsedMs,
    };
}

/// <summary>Assembly marker so integration tests can reference the host entry point.</summary>
public partial class Program;
