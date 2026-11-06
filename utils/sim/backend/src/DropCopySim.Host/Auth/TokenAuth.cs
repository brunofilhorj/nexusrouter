using System.Security.Cryptography;
using System.Text;
using DropCopySim.Host;
using Microsoft.Extensions.Options;

namespace DropCopySim.Host.Auth;

/// <summary>
/// Resolves the control-API bearer token from the environment variable named by
/// <see cref="ControlOptions.AuthTokenEnvVar"/> and validates presented credentials in constant time.
/// </summary>
/// <remarks>
/// The token value lives only in process memory, read once at construction from the environment.
/// It is never written to configuration, logs or URLs (FR-API-06, NFR-CFG-02). When the configured
/// environment variable is absent or empty, the provider reports <see cref="AuthRequired"/> as
/// <c>false</c> and the API runs in open lab mode.
/// </remarks>
public sealed class ControlTokenProvider
{
    private readonly byte[]? _expectedTokenUtf8;

    /// <summary>
    /// Creates a provider that reads the token from the environment variable named in
    /// <paramref name="options"/>. A whitespace-only value is treated as unset (open lab mode).
    /// </summary>
    /// <param name="options">Control-tier options carrying the token environment variable name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ControlTokenProvider(IOptions<ControlOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var envVarName = options.Value.AuthTokenEnvVar;
        var token = string.IsNullOrWhiteSpace(envVarName)
            ? null
            : Environment.GetEnvironmentVariable(envVarName);

        _expectedTokenUtf8 = string.IsNullOrEmpty(token)
            ? null
            : Encoding.UTF8.GetBytes(token);
    }

    /// <summary>
    /// <c>true</c> when a non-empty token is configured and callers must present it; <c>false</c> in
    /// open lab mode (no token configured).
    /// </summary>
    public bool AuthRequired => _expectedTokenUtf8 is not null;

    /// <summary>
    /// Validates a presented token against the configured one using a fixed-time comparison that does
    /// not leak length or content through timing.
    /// </summary>
    /// <param name="presentedToken">The raw token supplied by the caller, or <c>null</c> when absent.</param>
    /// <returns>
    /// <c>true</c> when auth is not required (open lab mode) or when <paramref name="presentedToken"/>
    /// matches the configured token; otherwise <c>false</c>.
    /// </returns>
    public bool IsValid(string? presentedToken)
    {
        if (_expectedTokenUtf8 is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(presentedToken))
        {
            return false;
        }

        var presentedUtf8 = Encoding.UTF8.GetBytes(presentedToken);
        return CryptographicOperations.FixedTimeEquals(presentedUtf8, _expectedTokenUtf8);
    }
}

/// <summary>
/// Middleware that enforces the control-API bearer token on every request when a token is configured.
/// </summary>
/// <remarks>
/// HTTP clients present the token via <c>Authorization: Bearer &lt;token&gt;</c>. Browser WebSocket
/// clients, which cannot set request headers on the handshake, may instead present it via the
/// short-lived <c>access_token</c> query-string parameter on the upgrade request. Rejected requests
/// receive <c>401 Unauthorized</c>. In open lab mode (no token configured) every request passes.
/// Neither the configured nor the presented token is ever logged.
/// </remarks>
public sealed class TokenAuthMiddleware
{
    private const string BearerPrefix = "Bearer ";
    private const string WebSocketTokenQueryKey = "access_token";

    private readonly RequestDelegate _next;
    private readonly ControlTokenProvider _tokenProvider;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next delegate in the request pipeline.</param>
    /// <param name="tokenProvider">Provider that resolves and validates the control token.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public TokenAuthMiddleware(RequestDelegate next, ControlTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _next = next;
        _tokenProvider = tokenProvider;
    }

    /// <summary>Validates the presented token, short-circuiting with 401 when it is missing or wrong.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!_tokenProvider.AuthRequired)
        {
            await _next(context);
            return;
        }

        var presentedToken = ExtractToken(context.Request);
        if (!_tokenProvider.IsValid(presentedToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Extracts the bearer token from the <c>Authorization</c> header, falling back to the
    /// <c>access_token</c> query parameter for WebSocket upgrade handshakes.
    /// </summary>
    private static string? ExtractToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return authorization[BearerPrefix.Length..].Trim();
        }

        if (request.Headers.ContainsKey("Sec-WebSocket-Key")
            && request.Query.TryGetValue(WebSocketTokenQueryKey, out var queryToken))
        {
            return queryToken.ToString();
        }

        return null;
    }
}

/// <summary>Registration and pipeline helpers for control-API token authentication.</summary>
public static class TokenAuthExtensions
{
    /// <summary>Registers the <see cref="ControlTokenProvider"/> as a singleton.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddControlTokenAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ControlTokenProvider>();
        return services;
    }

    /// <summary>Inserts the <see cref="TokenAuthMiddleware"/> into the request pipeline.</summary>
    /// <param name="app">The application builder to configure.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IApplicationBuilder UseControlTokenAuth(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TokenAuthMiddleware>();
    }
}
