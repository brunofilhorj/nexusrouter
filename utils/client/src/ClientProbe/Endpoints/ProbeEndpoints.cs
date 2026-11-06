using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClientProbe.Fix;
using ClientProbe.Probe;
using ClientProbe.Telemetry;

namespace ClientProbe.Endpoints;

/// <summary>Maps the same-origin probe API: state, connect, disconnect and the telemetry socket.</summary>
internal static class ProbeEndpoints
{
    /// <summary>
    /// Maps <c>GET /v1/state</c>, <c>POST /v1/connect</c>, <c>POST /v1/disconnect</c> and <c>GET /v1/ws</c>.
    /// Call this before the SPA fallback so the fallback does not swallow the API.
    /// </summary>
    /// <param name="app">The application.</param>
    public static void MapProbeEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/state", GetState);
        app.MapPost("/v1/connect", ConnectAsync);
        app.MapPost("/v1/disconnect", DisconnectAsync);
        app.MapGet("/v1/ws", AcceptTelemetrySocketAsync);
    }

    /// <summary>Returns the three adapters with their state and counters.</summary>
    internal static ProbeStateResponse GetState(ProbeController controller, TimeProvider time)
    {
        var now = time.GetUtcNow();
        return new ProbeStateResponse([.. controller.Adapters.Select(a => ToResponse(a, now))]);
    }

    /// <summary>Connects one transport. 400 for a bad body or profile, 409 while that transport is connecting.</summary>
    internal static async Task<IResult> ConnectAsync(
        HttpContext context,
        ProbeController controller,
        ProbeProfileParser parser,
        TimeProvider time)
    {
        try
        {
            var request = await ReadBodyAsync<ConnectRequest>(context).ConfigureAwait(false);
            var transport = ProbeProfileParser.ParseTransport(request?.Transport);
            var profile = parser.Parse(transport, request?.Profile);
            await controller.ConnectAsync(profile, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ToResponse(controller.Adapters.First(a => a.Transport == transport), time.GetUtcNow()));
        }
        catch (ProbeValidationException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Code, ex.Message);
        }
        catch (ProbeConflictException ex)
        {
            return Error(StatusCodes.Status409Conflict, "connect_in_progress", ex.Message);
        }
    }

    /// <summary>Disconnects one transport. 400 for a bad body.</summary>
    internal static async Task<IResult> DisconnectAsync(
        HttpContext context,
        ProbeController controller,
        TimeProvider time)
    {
        try
        {
            var request = await ReadBodyAsync<DisconnectRequest>(context).ConfigureAwait(false);
            var transport = ProbeProfileParser.ParseTransport(request?.Transport);
            await controller.DisconnectAsync(transport, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ToResponse(controller.Adapters.First(a => a.Transport == transport), time.GetUtcNow()));
        }
        catch (ProbeValidationException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Accepts the telemetry WebSocket, forwards hub messages to it and reads until the client closes.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="hub">Telemetry hub.</param>
    internal static async Task AcceptTelemetrySocketAsync(HttpContext context, TelemetryHub hub)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiError("websocket_required", "This endpoint requires a WebSocket upgrade request."));
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var subscription = hub.Subscribe();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var sender = SendLoopAsync(socket, subscription, linked.Token);
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
            // The client went away without a close frame.
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The host is stopping.
        }

        await linked.CancelAsync().ConfigureAwait(false);
        await sender.ConfigureAwait(false);

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

    private static async Task SendLoopAsync(WebSocket socket, TelemetrySubscription subscription, CancellationToken ct)
    {
        try
        {
            await foreach (var json in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                {
                    break;
                }

                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The receive side finished first.
        }
        catch (WebSocketException)
        {
            // The client went away mid-send.
        }
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context)
        where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new ProbeValidationException("invalid_body", "Request body must be a JSON object.");
        }
        catch (InvalidOperationException)
        {
            throw new ProbeValidationException("invalid_body", "Request body must be application/json.");
        }
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: status);

    private static AdapterStateResponse ToResponse(IProbeAdapter adapter, DateTimeOffset now)
    {
        var c = adapter.Counters;
        return new AdapterStateResponse(
            adapter.Transport.ToString().ToLowerInvariant(),
            adapter.State.ToString(),
            adapter.Endpoint,
            adapter.LastError,
            new CounterStateResponse(c.Received, c.GetRate(now), c.LastReceivedAt, c.ParseFailures, c.Sampled, c.Dropped),
            (adapter as IFixSessionSource)?.Session);
    }
}

/// <summary>Body of <c>POST /v1/connect</c>.</summary>
/// <param name="Transport">Transport id: kafka, rabbit, or fix.</param>
/// <param name="Profile">Profile object for that transport.</param>
internal sealed record ConnectRequest(string? Transport, JsonElement? Profile);

/// <summary>Body of <c>POST /v1/disconnect</c>.</summary>
/// <param name="Transport">Transport id: kafka, rabbit, or fix.</param>
internal sealed record DisconnectRequest(string? Transport);

/// <summary>Snapshot of the three probe adapters.</summary>
/// <param name="Adapters">Kafka, Rabbit, and FIX, in that order.</param>
internal sealed record ProbeStateResponse(IReadOnlyList<AdapterStateResponse> Adapters);

/// <summary>One adapter row in <c>GET /v1/state</c>.</summary>
/// <param name="Transport">Transport id: kafka, rabbit, or fix.</param>
/// <param name="State">Connection state name.</param>
/// <param name="Endpoint">Effective endpoint. Empty while disconnected.</param>
/// <param name="LastError">Last error, or null when there is none.</param>
/// <param name="Counters">Receive and sampling counters.</param>
/// <param name="Session">FIX session identity and sequence numbers; null for other transports.</param>
internal sealed record AdapterStateResponse(
    string Transport,
    string State,
    string Endpoint,
    string? LastError,
    CounterStateResponse Counters,
    FixSessionSnapshot? Session = null);

/// <summary>Counters reported for one adapter.</summary>
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
