using System.Net.WebSockets;

namespace ClientProbe.Hosting;

/// <summary>
/// Forwards requests that are not under <c>/v1</c> to the internal Vite dev server,
/// including the hot-reload WebSocket. <c>/v1/*</c> stays on Kestrel.
/// </summary>
internal sealed class ViteProxyMiddleware : IDisposable
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
    };

    private readonly RequestDelegate _next;
    private readonly ViteDevServer _vite;
    private readonly HttpClient _http;

    /// <summary>Creates the proxy.</summary>
    /// <param name="next">The rest of the pipeline. Used for <c>/v1/*</c>.</param>
    /// <param name="vite">The dev server started for this process.</param>
    public ViteProxyMiddleware(RequestDelegate next, ViteDevServer vite)
    {
        _next = next;
        _vite = vite;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Proxies one request, or passes <c>/v1/*</c> through.</summary>
    /// <param name="context">The current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/v1"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocketAsync(context, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await ProxyHttpAsync(context, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Releases the outbound HTTP client.</summary>
    public void Dispose() => _http.Dispose();

    private async Task ProxyHttpAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var target = new Uri($"{_vite.Origin}{context.Request.Path}{context.Request.QueryString}");
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
            {
                request.Content ??= new StreamContent(Stream.Null);
                request.Content.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
            }
        }

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var body = new StreamContent(context.Request.Body);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    body.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            request.Content = body;
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(context.Response, response);
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProxyWebSocketAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var protocols = ReadSubprotocols(context.Request.Headers["Sec-WebSocket-Protocol"].ToString());
        using var client = await context.WebSockets.AcceptWebSocketAsync(protocols.Length > 0 ? protocols[0] : null)
            .ConfigureAwait(false);
        using var backend = new ClientWebSocket();
        foreach (var protocol in protocols)
        {
            backend.Options.AddSubProtocol(protocol);
        }

        var target = new Uri($"ws://{ViteDevServer.Host}:{ViteDevServer.Port}{context.Request.Path}{context.Request.QueryString}");
        await backend.ConnectAsync(target, cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inbound = PumpAsync(backend, client, linked.Token);
        var outbound = PumpAsync(client, backend, linked.Token);
        await Task.WhenAny(inbound, outbound).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(inbound, outbound).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The other direction was cancelled after the first side finished.
        }
        catch (WebSocketException)
        {
            // One side closed abruptly. The other pump has been cancelled.
        }

        await CloseQuietlyAsync(client).ConfigureAwait(false);
        await CloseQuietlyAsync(backend).ConfigureAwait(false);
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            await destination.SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

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

    private static string[] ReadSubprotocols(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        return header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static void CopyResponseHeaders(HttpResponse destination, HttpResponseMessage source)
    {
        foreach (var header in source.Headers)
        {
            SetHeader(destination, header.Key, header.Value);
        }

        foreach (var header in source.Content.Headers)
        {
            SetHeader(destination, header.Key, header.Value);
        }
    }

    private static void SetHeader(HttpResponse destination, string key, IEnumerable<string> values)
    {
        if (HopByHopHeaders.Contains(key))
        {
            return;
        }

        destination.Headers[key] = values.ToArray();
    }
}
