using Microsoft.Extensions.Options;

namespace DropCopySim.Core.Acceptor;

/// <summary>
/// Validates <see cref="AcceptorOptions"/> at options bind time so invalid
/// configuration fails fast during host startup.
/// </summary>
public sealed class AcceptorOptionsValidator : IValidateOptions<AcceptorOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AcceptorOptions options)
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
            .Select(static r => r.ErrorMessage ?? "Invalid AcceptorOptions value.")
            .ToArray();
        return ValidateOptionsResult.Fail(failures);
    }
}
