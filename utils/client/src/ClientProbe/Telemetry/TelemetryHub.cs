using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ClientProbe.Probe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ClientProbe.Telemetry;

/// <summary>
/// Bounded telemetry pipeline. Adapters call <see cref="TryPublish"/>, which never blocks. A single
/// reader task samples above <c>SampleAbovePerSecond</c> and broadcasts JSON to every subscriber.
/// </summary>
public sealed class TelemetryHub : BackgroundService, ITelemetryPublisher
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private const int SubscriberCapacity = 256;

    private readonly Channel<Item> _channel;
    private readonly int _sampleAbove;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<long, Channel<string>> _subscribers = new();
    private readonly long[] _windowStart = new long[Enum.GetValues<ProbeTransport>().Length];
    private readonly int[] _windowCount = new int[Enum.GetValues<ProbeTransport>().Length];
    private long _nextId;

    /// <summary>Creates the hub.</summary>
    /// <param name="options">Probe options; uses <c>Telemetry.ChannelCapacity</c> and <c>SampleAbovePerSecond</c>.</param>
    /// <param name="time">Clock used for the sampling window.</param>
    public TelemetryHub(IOptions<ClientProbeOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        var telemetry = options.Value.Telemetry;
        _sampleAbove = Math.Max(1, telemetry.SampleAbovePerSecond);
        _time = time;
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(Math.Max(1, telemetry.ChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public bool TryPublish(ProbeEvent probeEvent, ProbeCounters counters)
    {
        ArgumentNullException.ThrowIfNull(probeEvent);
        ArgumentNullException.ThrowIfNull(counters);
        if (_channel.Writer.TryWrite(new Item(probeEvent, counters)))
        {
            return true;
        }

        counters.RecordDropped();
        return false;
    }

    /// <summary>Registers a client. Messages are queued per client with drop-oldest overflow.</summary>
    /// <returns>The client's subscription.</returns>
    public TelemetrySubscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Interlocked.Increment(ref _nextId);
        _subscribers[id] = channel;
        return new TelemetrySubscription(channel.Reader, () =>
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        });
    }

    /// <summary>Sends one JSON message to every subscriber without blocking.</summary>
    /// <param name="json">Serialized message.</param>
    public void Broadcast(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(json);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (ShouldForward(item.Event.Transport))
                {
                    Broadcast(JsonSerializer.Serialize(item.Event, ProbeJson.Options));
                }
                else
                {
                    item.Counters.RecordSampled();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    /// <summary>Within a one-second window, events up to the threshold pass; beyond it, 1 in N passes.</summary>
    private bool ShouldForward(ProbeTransport transport)
    {
        var index = (int)transport;
        var now = _time.GetTimestamp();
        if (_windowStart[index] == 0 || _time.GetElapsedTime(_windowStart[index], now) >= Window)
        {
            _windowStart[index] = now;
            _windowCount[index] = 0;
        }

        var count = ++_windowCount[index];
        if (count <= _sampleAbove)
        {
            return true;
        }

        var every = (count / _sampleAbove) + 1;
        return count % every == 0;
    }

    private readonly record struct Item(ProbeEvent Event, ProbeCounters Counters);
}
