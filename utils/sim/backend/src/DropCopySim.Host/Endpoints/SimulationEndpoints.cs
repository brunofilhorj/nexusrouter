using System.ComponentModel.DataAnnotations;
using DropCopySim.Core.Generation;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Maps the simulation control endpoints: start, pause, resume, stop and configuration update
/// (FR-SIM-01, FR-SIM-08, FR-API-01).
/// </summary>
public static class SimulationEndpoints
{
    /// <summary>
    /// Registers <c>POST /v1/sim/{start,pause,resume,stop}</c> and <c>PUT /v1/config</c>.
    /// </summary>
    /// <param name="routes">The route builder to register the endpoints on.</param>
    /// <returns>The same <paramref name="routes"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is null.</exception>
    public static IEndpointRouteBuilder MapSimulationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/v1/sim/start", (SimulationController controller, SimulationConfigRequest? body) =>
        {
            if (body is not null)
            {
                var applied = ApplyConfig(controller, body);
                if (applied is not null)
                {
                    return applied;
                }
            }

            try
            {
                controller.Start();
                return Results.NoContent();
            }
            catch (InvalidSimulationStateException ex)
            {
                return ApiResults.Conflict(ex);
            }
        });

        routes.MapPost("/v1/sim/pause", (SimulationController controller) =>
        {
            try
            {
                controller.Pause();
                return Results.NoContent();
            }
            catch (InvalidSimulationStateException ex)
            {
                return ApiResults.Conflict(ex);
            }
        });

        routes.MapPost("/v1/sim/resume", (SimulationController controller) =>
        {
            try
            {
                controller.Resume();
                return Results.NoContent();
            }
            catch (InvalidSimulationStateException ex)
            {
                return ApiResults.Conflict(ex);
            }
        });

        routes.MapPost("/v1/sim/stop", async (SimulationController controller) =>
        {
            try
            {
                await controller.StopAsync().ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (InvalidSimulationStateException ex)
            {
                return ApiResults.Conflict(ex);
            }
        });

        routes.MapPut("/v1/config", (SimulationController controller, SimulationConfigRequest body) =>
        {
            if (body is null)
            {
                return ApiResults.ValidationFailed("A configuration body is required.");
            }

            var applied = ApplyConfig(controller, body);
            return applied ?? Results.Ok(controller.Config);
        });

        return routes;
    }

    /// <summary>
    /// Merges a request onto the current config, validates it, and applies it through the controller's
    /// hybrid-reconfiguration policy. Returns an error <see cref="IResult"/> on failure, or <c>null</c>
    /// when the candidate was accepted.
    /// </summary>
    private static IResult? ApplyConfig(SimulationController controller, SimulationConfigRequest body)
    {
        var candidate = body.Merge(controller.Config);

        if (!TryValidate(candidate, out var error))
        {
            return ApiResults.ValidationFailed(error);
        }

        try
        {
            controller.UpdateConfig(candidate);
            return null;
        }
        catch (InvalidSimulationStateException ex)
        {
            // Locked field touched while Running — all-or-nothing, no partial effect (FR-SIM-08).
            return ApiResults.Conflict(ex);
        }
    }

    /// <summary>
    /// Runs data-annotation and <see cref="IValidatableObject"/> validation over the candidate config.
    /// </summary>
    /// <param name="candidate">The merged candidate configuration.</param>
    /// <param name="error">The first validation error message when invalid; otherwise empty.</param>
    /// <returns><c>true</c> when the candidate is valid.</returns>
    private static bool TryValidate(SimulationConfig candidate, out string error)
    {
        var context = new ValidationContext(candidate);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(candidate, context, results, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }

        error = results.Count > 0
            ? results[0].ErrorMessage ?? "Invalid SimulationConfig value."
            : "Invalid SimulationConfig value.";
        return false;
    }
}
