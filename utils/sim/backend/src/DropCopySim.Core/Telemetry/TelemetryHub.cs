using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DropCopySim.Core.Telemetry;

/// <summary>
/// Fan-out hub for control-plane telemetry. Producers (the <c>OrderFlowGenerator</c> and
/// <c>AcceptorSession</c>) call <see cref="TryPublish"/>, which never blocks the FIX send path;
/// a single background consumer serializes each event to JSON once and broadcasts it to every
/// registered <see cref="ITelemetryClient"/> (FR-API-02, FR-API-03, NFR-PERF-02).
/// </summary>
/// <remarks>
/// The event channel is bounded with <see cref="BoundedChannelFullMode.DropOldest"/>: under pressure
/// events are dropped, never delayed, so a slow or stalled WebSocket client can never back-pressure
/// the generator. <see cref="MessageSample"/> events are additionally rate-limited to
/// <see cref="TelemetryOptions.MessageSampleMaxPerSecond"/> per second, and a
/// <see cref="CountersSnapshot"/> is emitted every <see cref="TelemetryOptions.CountersIntervalMs"/>
/// milliseconds from the supplied snapshot provider.
/// </remarks>
public sealed class TelemetryHub : IHostedService, IAsyncDisposable
{
    // Serialize enums as names (e.g. "Running") so the WebSocket feed matches the HTTP control-API
    // contract and the SimPanel DTOs; without this the SimulationState enum would be written as a
    // number and the UI's stateSlug(state) would fail on a non-string value.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Channel<TelemetryEvent> _channel;
    private readonly ConcurrentDictionary<string, ITelemetryClient> _clients = new(StringComparer.Ordinal);
    private readonly ILogger<TelemetryHub> _logger;
    private readonly Func<DateTimeOffset, CountersSnapshot?>? _countersProvider;
    private readonly int _messageSampleMaxPerSecond;
    private readonly TimeSpan _countersInterval;

    private long _sampleWindowSeconds;
    private int _samplesInWindow;

    private CancellationTokenSource? _stoppingCts;
    private Task? _consumerLoop;
    private Task? _countersLoop;
    private int _disposed;

    /// <summary>
    /// Creates a telemetry hub.
    /// </summary>
    /// <param name="options">Validated telemetry tuning options.</param>
    /// <param name="logger">Logger for lifecycle and client-send failures.</param>
    /// <param name="countersProvider">
    /// Optional provider invoked on each counters tick to build the periodic
    /// <see cref="CountersSnapshot"/>; returning <c>null</c> suppresses that tick's snapshot
    /// (e.g. when no round is active). When omitted, no periodic snapshots are emitted.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public TelemetryHub(
        IOptions<TelemetryOptions> options,
        ILogger<TelemetryHub> logger,
        Func<DateTimeOffset, CountersSnapshot?>? countersProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;
        _logger = logger;
        _countersProvider = countersProvider;
        _messageSampleMaxPerSecond = value.MessageSampleMaxPerSecond;
        _countersInterval = TimeSpan.FromMilliseconds(value.CountersIntervalMs);

        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(value.TelemetryChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Number of currently registered clients.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Publishes a telemetry event for broadcast without blocking the caller.
    /// </summary>
    /// <param name="telemetryEvent">The event to broadcast.</param>
    /// <returns>
    /// <c>true</c> when the event was accepted into the channel; <c>false</c> when it was dropped
    /// (sample budget exceeded, channel completed, or the window counter full).
    /// </returns>
    /// <remarks>
    /// Non-blocking. <see cref="MessageSample"/> events over the per-second budget are dropped here so
    /// the channel is never filled by a burst of samples. All other events are offered to the channel,
    /// which drops its oldest entry on overflow rather than blocking.
    /// </remarks>
    public bool TryPublish(TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        if (telemetryEvent is MessageSample && !TryAdmitSample(telemetryEvent.TimestampUtc))
        {
            return false;
        }

        return _channel.Writer.TryWrite(telemetryEvent);
    }

    /// <summary>
    /// Registers a client to receive subsequent broadcasts.
    /// </summary>
    /// <param name="client">The client to add; replaces any existing client with the same id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public void AddClient(ITelemetryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients[client.Id] = client;
        _logger.LogDebug("Telemetry client {ClientId} registered ({ClientCount} total)", client.Id, _clients.Count);
    }

    /// <summary>
    /// Removes a previously registered client.
    /// </summary>
    /// <param name="clientId">The id of the client to remove.</param>
    /// <returns><c>true</c> when a client was removed.</returns>
    public bool RemoveClient(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        var removed = _clients.TryRemove(clientId, out _);
        if (removed)
        {
            _logger.LogDebug("Telemetry client {ClientId} removed ({ClientCount} total)", clientId, _clients.Count);
        }

        return removed;
    }

    /// <summary>Starts the background broadcast consumer and the periodic counters loop.</summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A completed task; the loops run until <see cref="StopAsync"/>.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stoppingCts = new CancellationTokenSource();
        _consumerLoop = Task.Run(() => ConsumeAsync(_stoppingCts.Token), CancellationToken.None);
        _countersLoop = Task.Run(() => PublishCountersAsync(_stoppingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Signals the broadcast and counters loops to stop and awaits their completion.</summary>
    /// <param name="cancellationToken">Cancels the graceful drain.</param>
    /// <returns>A task that completes once both loops have stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_stoppingCts is not null)
        {
            await _stoppingCts.CancelAsync().ConfigureAwait(false);
        }

        var loops = new[] { _consumerLoop, _countersLoop }
            .Where(static loop => loop is not null)
            .Cast<Task>()
            .ToArray();

        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown token fired before the loops drained; nothing more to do.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _stoppingCts?.Dispose();
        }
    }

    private bool TryAdmitSample(DateTimeOffset now)
    {
        if (_messageSampleMaxPerSecond <= 0)
        {
            return false;
        }

        var window = now.ToUnixTimeSeconds();
        if (Interlocked.Exchange(ref _sampleWindowSeconds, window) != window)
        {
            Interlocked.Exchange(ref _samplesInWindow, 0);
        }

        return Interlocked.Increment(ref _samplesInWindow) <= _messageSampleMaxPerSecond;
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var telemetryEvent in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await BroadcastAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry broadcast consumer stopped unexpectedly");
        }
    }

    private async Task PublishCountersAsync(CancellationToken cancellationToken)
    {
        if (_countersProvider is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_countersInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var snapshot = _countersProvider(DateTimeOffset.UtcNow);
                if (snapshot is not null)
                {
                    _channel.Writer.TryWrite(snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry counters loop stopped unexpectedly");
        }
    }

    private async Task BroadcastAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        byte[] frame;
        try
        {
            frame = JsonSerializer.SerializeToUtf8Bytes(telemetryEvent, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize telemetry event {EventType}", telemetryEvent.GetType().Name);
            return;
        }

        foreach (var client in _clients.Values)
        {
            try
            {
                await client.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Removing telemetry client {ClientId} after a send failure", client.Id);
                RemoveClient(client.Id);
            }
        }
    }
}
