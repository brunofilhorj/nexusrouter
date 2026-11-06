using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;

namespace ClientProbe.Probe;

/// <summary>
/// Shared state, counters and frame pipeline for adapters. A received frame goes through
/// <see cref="OnFrame"/>: count, extract short fields, publish without blocking.
/// </summary>
public abstract class ProbeAdapterBase : IProbeAdapter
{
    private readonly ITelemetryPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly int _previewLength;
    private int _state;
    private volatile string _endpoint = "";
    private volatile string? _lastError;

    /// <summary>Creates the base.</summary>
    /// <param name="transport">Transport served by the adapter.</param>
    /// <param name="publisher">Telemetry entry point.</param>
    /// <param name="options">Probe options; uses <c>Telemetry.RawPreviewLength</c>.</param>
    /// <param name="time">Clock for receive timestamps.</param>
    protected ProbeAdapterBase(
        ProbeTransport transport,
        ITelemetryPublisher publisher,
        IOptions<ClientProbeOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        Transport = transport;
        _publisher = publisher;
        _time = time;
        _previewLength = options.Value.Telemetry.RawPreviewLength;
    }

    /// <inheritdoc />
    public ProbeTransport Transport { get; }

    /// <inheritdoc />
    public ProbeState State => (ProbeState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public string Endpoint => _endpoint;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <inheritdoc />
    public ProbeCounters Counters { get; } = new();

    /// <inheritdoc />
    public abstract Task ConnectAsync(ProbeProfile profile, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task DisconnectAsync(CancellationToken ct);

    /// <summary>Moves to <paramref name="state"/>. A non-null <paramref name="error"/> replaces <see cref="LastError"/>; null keeps it, except on <see cref="ProbeState.Connecting"/> which clears it.</summary>
    /// <param name="state">New state.</param>
    /// <param name="error">Error text, when the change is a fault.</param>
    protected void SetState(ProbeState state, string? error = null)
    {
        if (error is not null)
        {
            _lastError = error;
        }
        else if (state == ProbeState.Connecting)
        {
            _lastError = null;
        }

        Volatile.Write(ref _state, (int)state);
    }

    /// <summary>Sets the effective endpoint. Pass an empty string after disconnect.</summary>
    /// <param name="endpoint">Endpoint text without credentials.</param>
    protected void SetEndpoint(string endpoint) => _endpoint = endpoint;

    /// <summary>
    /// Handles one received frame: counts it, extracts the short fields and publishes without blocking.
    /// A frame with no tag 35 counts as a parse failure and is still published.
    /// </summary>
    /// <param name="frame">ASCII bytes of one FIX frame.</param>
    /// <returns>The published event, for adapters that track session fields such as tag 35.</returns>
    protected ProbeEvent OnFrame(ReadOnlySpan<byte> frame)
    {
        var now = _time.GetUtcNow();
        Counters.RecordReceived(now);
        var probeEvent = ShortFieldExtractor.Extract(Transport, frame, now, _previewLength);
        if (probeEvent.MsgType is null)
        {
            Counters.RecordParseFailure();
        }

        _publisher.TryPublish(probeEvent, Counters);
        return probeEvent;
    }
}
