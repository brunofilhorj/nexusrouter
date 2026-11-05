using System.Net.WebSockets;
using DropCopySim.Core.Telemetry;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Maps the telemetry WebSocket endpoint (<c>GET /v1/ws</c>), which registers the connected client with
/// the <see cref="TelemetryHub"/> so it receives the broadcast telemetry feed (FR-API-02).
/// </summary>
public static class TelemetryEndpoints
{
    /// <summary>
    /// Registers <c>GET /v1/ws</c>. A non-WebSocket request receives <c>400 Bad Request</c>; a valid
    /// upgrade is registered with the hub and kept open until the client closes it or the host stops.
    /// </summary>
    /// <param name="routes">The route builder to register the endpoint on.</param>
    /// <returns>The same <paramref name="routes"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is null.</exception>
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/v1/ws", async (
            HttpContext context,
            TelemetryHub hub,
            ILoggerFactory loggerFactory) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return ApiResults.ValidationFailed("This endpoint requires a WebSocket upgrade request.");
            }

            var logger = loggerFactory.CreateLogger("DropCopySim.Host.Endpoints.Telemetry");
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var clientId = Guid.NewGuid().ToString("N");
            var client = new WebSocketTelemetryClient(socket, clientId);

            hub.AddClient(client);
            logger.LogInformation("Telemetry WebSocket client {ClientId} connected.", clientId);

            try
            {
                await PumpUntilClosedAsync(socket, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown or client disconnect; fall through to cleanup.
            }
            catch (WebSocketException)
            {
                logger.LogDebug("Telemetry WebSocket client {ClientId} closed abnormally.", clientId);
            }
            finally
            {
                hub.RemoveClient(clientId);
                logger.LogInformation("Telemetry WebSocket client {ClientId} disconnected.", clientId);
            }

            return Results.Empty;
        });

        return routes;
    }

    /// <summary>
    /// Reads and discards inbound frames until the client sends a Close, draining the receive side so
    /// the socket close handshake completes. The hub owns all outbound sends.
    /// </summary>
    private static async Task PumpUntilClosedAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket
                    .ReceiveAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await TryCloseGracefullyAsync(socket, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Closes <paramref name="socket"/> with a normal-closure status when it is in a state that still
    /// permits a close handshake, swallowing the <see cref="WebSocketException"/> thrown when the socket
    /// was already aborted (e.g. the client tore the connection down between frames).
    /// </summary>
    internal static async Task TryCloseGracefullyAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
    }
}
