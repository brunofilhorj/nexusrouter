using Microsoft.Extensions.Options;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Validates <see cref="SimulationConfig"/> at options bind time so invalid
/// configuration fails fast during host startup.
/// </summary>
public sealed class SimulationConfigValidator : IValidateOptions<SimulationConfig>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SimulationConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(options);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                options, context, results, validateAllProperties: true))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = results
            .Select(static r => r.ErrorMessage ?? "Invalid SimulationConfig value.")
            .ToArray();
        return ValidateOptionsResult.Fail(failures);
    }
}
