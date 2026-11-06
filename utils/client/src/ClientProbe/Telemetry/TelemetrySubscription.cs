using System.Threading.Channels;

namespace ClientProbe.Telemetry;

/// <summary>One WebSocket client's feed of JSON messages. Dispose to unsubscribe.</summary>
public sealed class TelemetrySubscription : IDisposable
{
    private readonly Action _onDispose;

    internal TelemetrySubscription(ChannelReader<string> reader, Action onDispose)
    {
        Reader = reader;
        _onDispose = onDispose;
    }

    /// <summary>JSON messages for this client. A slow client loses its oldest messages, never blocks others.</summary>
    public ChannelReader<string> Reader { get; }

    /// <inheritdoc />
    public void Dispose() => _onDispose();
}
