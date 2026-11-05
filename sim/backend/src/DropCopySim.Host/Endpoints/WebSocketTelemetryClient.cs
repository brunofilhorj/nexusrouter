using System.Net.WebSockets;
using DropCopySim.Core.Telemetry;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Adapts a connected <see cref="WebSocket"/> to the <see cref="ITelemetryClient"/> contract so the
/// <see cref="TelemetryHub"/> can broadcast serialized telemetry frames to a SimPanel browser client
/// without <c>DropCopySim.Core</c> taking an ASP.NET Core dependency (FR-API-02).
/// </summary>
/// <remarks>
/// The hub calls <see cref="SendAsync"/> from a single background consumer, so sends are never
/// concurrent. Each frame is written as one complete text message. A send failure surfaces to the hub,
/// which removes the client; the endpoint loop then completes and the socket is closed.
/// </remarks>
public sealed class WebSocketTelemetryClient : ITelemetryClient
{
    private readonly WebSocket _socket;

    /// <summary>Creates a telemetry client wrapping an accepted WebSocket.</summary>
    /// <param name="socket">The accepted WebSocket.</param>
    /// <param name="id">Stable identity used to register and remove the client with the hub.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public WebSocketTelemetryClient(WebSocket socket, string id)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrEmpty(id);
        _socket = socket;
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException($"WebSocket is not open (state {_socket.State}).");
        }

        return _socket.SendAsync(utf8Json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
