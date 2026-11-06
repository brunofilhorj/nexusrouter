using System.Net.WebSockets;

namespace ClientProbe.Endpoints;

/// <summary>Maps the same-origin probe API. Telemetry events are not emitted yet.</summary>
internal static class ProbeEndpoints
{
    /// <summary>
    /// Maps <c>GET /v1/state</c> and <c>GET /v1/ws</c>.
    /// Call this before the SPA fallback so the fallback does not swallow the API.
    /// </summary>
    /// <param name="app">The application.</param>
    public static void MapProbeEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/state", GetState);
        app.MapGet("/v1/ws", AcceptTelemetrySocketAsync);
    }

    /// <summary>Returns the three transports disconnected, with counters at zero.</summary>
    internal static ProbeStateResponse GetState()
    {
        var counters = new CounterStateResponse(0, 0, null, 0, 0, 0);
        return new ProbeStateResponse(
        [
            new AdapterStateResponse("kafka", "Disconnected", "", null, counters),
            new AdapterStateResponse("rabbit", "Disconnected", "", null, counters),
            new AdapterStateResponse("fix", "Disconnected", "", null, counters),
        ]);
    }

    /// <summary>
    /// Accepts the telemetry WebSocket and reads until the client closes.
    /// No data frame is written.
    /// </summary>
    /// <param name="context">The current request.</param>
    internal static async Task AcceptTelemetrySocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiError("websocket_required", "This endpoint requires a WebSocket upgrade request."));
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            // The client went away without a close frame. Nothing was sent.
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The host is stopping. Nothing was sent.
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The peer already dropped the socket.
            }
        }
    }
}

/// <summary>Snapshot of the three probe adapters.</summary>
/// <param name="Adapters">Kafka, Rabbit, and FIX, in that order.</param>
internal sealed record ProbeStateResponse(IReadOnlyList<AdapterStateResponse> Adapters);

/// <summary>One adapter row in <c>GET /v1/state</c>.</summary>
/// <param name="Transport">Transport id: kafka, rabbit, or fix.</param>
/// <param name="State">Connection state name.</param>
/// <param name="Endpoint">Effective endpoint. Empty while disconnected.</param>
/// <param name="LastError">Last error, or null when there is none.</param>
/// <param name="Counters">Receive and sampling counters.</param>
internal sealed record AdapterStateResponse(
    string Transport,
    string State,
    string Endpoint,
    string? LastError,
    CounterStateResponse Counters);

/// <summary>Counters reported for one adapter. Rate is derived and starts at zero.</summary>
/// <param name="Received">Frames received from the broker or session.</param>
/// <param name="RatePerSecond">Messages per second.</param>
/// <param name="LastReceivedAt">Timestamp of the last received frame, if any.</param>
/// <param name="ParseFailures">Frames that failed to decode.</param>
/// <param name="Sampled">Events kept back by sampling.</param>
/// <param name="Dropped">Events discarded because the telemetry channel was full.</param>
internal sealed record CounterStateResponse(
    long Received,
    double RatePerSecond,
    DateTimeOffset? LastReceivedAt,
    long ParseFailures,
    long Sampled,
    long Dropped);

/// <summary>Structured API error body.</summary>
/// <param name="Code">Stable error code.</param>
/// <param name="Message">Operator-facing explanation.</param>
internal sealed record ApiError(string Code, string Message);
