using System.Text.Json;
using ClientProbe.Probe;
using Microsoft.Extensions.Hosting;

namespace ClientProbe.Telemetry;

/// <summary>Broadcasts a counters snapshot on <c>/v1/ws</c> at a short interval.</summary>
public sealed class CounterBroadcaster : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ProbeController _controller;
    private readonly TelemetryHub _hub;
    private readonly TimeProvider _time;

    /// <summary>Creates the broadcaster.</summary>
    /// <param name="controller">Source of adapter state.</param>
    /// <param name="hub">Destination hub.</param>
    /// <param name="time">Clock.</param>
    public CounterBroadcaster(ProbeController controller, TelemetryHub hub, TimeProvider time)
    {
        _controller = controller;
        _hub = hub;
        _time = time;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var now = _time.GetUtcNow();
                var adapters = _controller.Adapters.Select(a => new
                {
                    transport = a.Transport,
                    state = a.State,
                    received = a.Counters.Received,
                    ratePerSecond = a.Counters.GetRate(now),
                    sampled = a.Counters.Sampled,
                    dropped = a.Counters.Dropped,
                }).ToArray();
                _hub.Broadcast(JsonSerializer.Serialize(new { type = "counters", adapters }, ProbeJson.Options));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }
}
