using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Percentage mix of lifecycle outcomes produced by the order-flow generator.
/// Percentages are integer weights that must sum to 100.
/// </summary>
public sealed record OutcomeMix : IValidatableObject
{
    /// <summary>Percentage of orders that complete as a full fill.</summary>
    [Range(0, 100)]
    public int FillPct { get; init; }

    /// <summary>Percentage of orders that go through one or more partial fills.</summary>
    [Range(0, 100)]
    public int PartialFillPct { get; init; }

    /// <summary>Percentage of orders that end as canceled.</summary>
    [Range(0, 100)]
    public int CanceledPct { get; init; }

    /// <summary>Percentage of orders that are rejected.</summary>
    [Range(0, 100)]
    public int RejectedPct { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var sum = FillPct + PartialFillPct + CanceledPct + RejectedPct;
        if (sum != 100)
        {
            yield return new ValidationResult(
                $"Outcome percentages must sum to 100 (got {sum}).",
                [nameof(FillPct), nameof(PartialFillPct), nameof(CanceledPct), nameof(RejectedPct)]);
        }
    }
}
