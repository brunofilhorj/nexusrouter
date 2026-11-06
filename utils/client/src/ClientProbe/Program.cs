using ClientProbe;
using ClientProbe.Endpoints;
using ClientProbe.Fix;
using ClientProbe.Hosting;
using ClientProbe.Kafka;
using ClientProbe.Probe;
using ClientProbe.Rabbit;
using ClientProbe.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<ClientProbeOptions>()
    .Bind(builder.Configuration.GetSection(ClientProbeOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TelemetryHub>();
builder.Services.AddSingleton<ITelemetryPublisher>(sp => sp.GetRequiredService<TelemetryHub>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryHub>());
builder.Services.AddSingleton<IProbeAdapter, KafkaConsumerAdapter>();
builder.Services.AddSingleton<IProbeAdapter, RabbitConsumerAdapter>();
builder.Services.AddSingleton<IProbeAdapter, FixInitiatorAdapter>();
builder.Services.AddSingleton<ProbeProfileParser>();
builder.Services.AddSingleton<ProbeController>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProbeController>());
builder.Services.AddHostedService<CounterBroadcaster>();

var urls = builder.Configuration[$"{ClientProbeOptions.SectionName}:Urls"];
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(urls) ? ClientProbeOptions.DefaultUrls : urls);

var development = builder.Environment.IsDevelopment();
if (development)
{
    builder.Services.AddSingleton<ViteDevServer>();
}

var app = builder.Build();

app.UseWebSockets();

if (development)
{
    app.UseMiddleware<ViteProxyMiddleware>();
}
else
{
    app.UseStaticFiles();
}

// /v1 is mapped before the SPA fallback so the fallback cannot swallow the API.
app.MapProbeEndpoints();

if (!development)
{
    app.MapFallbackToFile("index.html");
}

if (development)
{
    // Start Vite before Kestrel accepts traffic so the first page load is already proxied.
    var vite = app.Services.GetRequiredService<ViteDevServer>();
    await vite.StartAsync(CancellationToken.None);
    app.Lifetime.ApplicationStopping.Register(() => vite.StopAsync(CancellationToken.None).GetAwaiter().GetResult());
}

app.Run();

/// <summary>Assembly marker so tests can reference the host entry point.</summary>
public partial class Program;
