using System.Threading.Channels;
using DropCopySim.Core.Telemetry;

namespace DropCopySim.Host;

/// <summary>
/// Adapts the <see cref="TelemetryHub"/> to the <see cref="ChannelWriter{T}"/> contract the
/// <c>OrderFlowGenerator</c> publishes telemetry through. Every <see cref="TryWrite"/> is forwarded
/// to <see cref="TelemetryHub.TryPublish"/>, which never blocks the FIX send path.
/// </summary>
/// <remarks>
/// The hub owns the real bounded channel and its drop-oldest / sample-budget policy; this writer only
/// forwards. Writes never complete the underlying channel, so <see cref="TryComplete"/> is a no-op and
/// <see cref="WaitToWriteAsync"/> always reports writable.
/// </remarks>
public sealed class TelemetryHubChannelWriter : ChannelWriter<TelemetryEvent>
{
    private readonly TelemetryHub _hub;

    /// <summary>Creates a writer that forwards events to <paramref name="hub"/>.</summary>
    /// <param name="hub">The telemetry hub that owns the broadcast channel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hub"/> is null.</exception>
    public TelemetryHubChannelWriter(TelemetryHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <inheritdoc />
    public override bool TryWrite(TelemetryEvent item) => _hub.TryPublish(item);

    /// <inheritdoc />
    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<bool>(cancellationToken)
            : ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public override bool TryComplete(Exception? error = null) => false;
}
