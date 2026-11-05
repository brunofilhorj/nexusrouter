using DropCopySim.Core.Acceptor;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Maps the fault-injection endpoints that act on the active FIX session: drop connection, force
/// Logout, switch the Logon response mode, and inject a sequence gap (FR-FAULT-01 to FR-FAULT-04,
/// FR-API-01).
/// </summary>
public static class FaultEndpoints
{
    /// <summary>
    /// Registers <c>POST /v1/fault/{drop,logout,logon-mode,seq-gap}</c>.
    /// </summary>
    /// <param name="routes">The route builder to register the endpoints on.</param>
    /// <returns>The same <paramref name="routes"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is null.</exception>
    public static IEndpointRouteBuilder MapFaultEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Drop the TCP connection without a Logout (FR-FAULT-01).
        routes.MapPost("/v1/fault/drop", (FixAcceptorService acceptor) =>
        {
            var session = acceptor.ActiveSession;
            if (session is null)
            {
                return NoActiveSession();
            }

            session.DropConnection();
            return Results.NoContent();
        });

        // Force a Logout (MsgType=5) carrying a configurable Text (FR-FAULT-02).
        routes.MapPost("/v1/fault/logout", async (FixAcceptorService acceptor, ForceLogoutRequest? body) =>
        {
            if (string.IsNullOrEmpty(body?.Text))
            {
                return ApiResults.ValidationFailed("A non-empty 'text' is required for a forced Logout.");
            }

            var session = acceptor.ActiveSession;
            if (session is null)
            {
                return NoActiveSession();
            }

            try
            {
                await session.ForceLogoutAsync(body.Text).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (InvalidSessionStateException ex)
            {
                return ApiResults.Conflict(ex);
            }
        });

        // Switch how the next client Logon is answered (FR-FAULT-03).
        routes.MapPost("/v1/fault/logon-mode", (FixAcceptorService acceptor, LogonModeRequest? body) =>
        {
            if (body?.Mode is not { } mode)
            {
                return ApiResults.ValidationFailed(
                    "A 'mode' is required: Success, InvalidCredentials or NotAuthorized.");
            }

            if (mode is not (LogonMode.Success or LogonMode.InvalidCredentials or LogonMode.NotAuthorized))
            {
                return ApiResults.ValidationFailed($"Unknown Logon mode '{mode}'.");
            }

            var session = acceptor.ActiveSession;
            if (session is null)
            {
                return NoActiveSession();
            }

            session.SetLogonMode(mode);
            return Results.NoContent();
        });

        // Inject a gap in the outbound sequence (FR-FAULT-04).
        routes.MapPost("/v1/fault/seq-gap", (FixAcceptorService acceptor, SequenceGapRequest? body) =>
        {
            if (body?.Skip is not { } skip || skip <= 0)
            {
                return ApiResults.ValidationFailed("A positive 'skip' count is required.");
            }

            var session = acceptor.ActiveSession;
            if (session is null)
            {
                return NoActiveSession();
            }

            session.InjectSequenceGap(skip);
            return Results.NoContent();
        });

        return routes;
    }

    private static IResult NoActiveSession() =>
        ApiResults.NotFound("No active FIX session is connected.");
}
