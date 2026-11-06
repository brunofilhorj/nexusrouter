using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Generation;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Maps control-plane domain outcomes onto the HTTP status codes and structured error body the
/// SimPanel expects (FR-API-05): 409 for state conflicts, 400 for validation, 404 for a missing
/// resource, with a <see cref="ApiError"/> payload of <c>{ code, message }</c>.
/// </summary>
public static class ApiResults
{
    /// <summary>
    /// <c>409 Conflict</c> for an illegal simulation transition or a reconfiguration that touched a
    /// locked field while Running (<see cref="InvalidSimulationStateException"/>).
    /// </summary>
    /// <param name="exception">The refused transition or reconfiguration.</param>
    /// <returns>A 409 result carrying an <see cref="ApiError"/> with code <c>invalid_state</c>.</returns>
    public static IResult Conflict(InvalidSimulationStateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Results.Json(new ApiError("invalid_state", exception.Message), statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// <c>409 Conflict</c> for a FIX session transition refused by the acceptor FSM
    /// (<see cref="InvalidSessionStateException"/>), e.g. a forced Logout while not Active.
    /// </summary>
    /// <param name="exception">The refused session transition.</param>
    /// <returns>A 409 result carrying an <see cref="ApiError"/> with code <c>invalid_session_state</c>.</returns>
    public static IResult Conflict(InvalidSessionStateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Results.Json(
            new ApiError("invalid_session_state", exception.Message),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// <c>400 Bad Request</c> for a validation failure (malformed body or a config that violates its
    /// data-annotation / <see cref="System.ComponentModel.DataAnnotations.IValidatableObject"/> rules).
    /// </summary>
    /// <param name="message">Human-readable validation detail.</param>
    /// <returns>A 400 result carrying an <see cref="ApiError"/> with code <c>validation_failed</c>.</returns>
    public static IResult ValidationFailed(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        return Results.Json(
            new ApiError("validation_failed", message),
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// <c>404 Not Found</c> when the addressed resource is absent (e.g. no active FIX session for a
    /// fault-injection command).
    /// </summary>
    /// <param name="message">Human-readable detail naming the missing resource.</param>
    /// <returns>A 404 result carrying an <see cref="ApiError"/> with code <c>not_found</c>.</returns>
    public static IResult NotFound(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        return Results.Json(new ApiError("not_found", message), statusCode: StatusCodes.Status404NotFound);
    }
}
