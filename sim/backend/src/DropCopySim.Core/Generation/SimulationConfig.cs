using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Strongly typed simulation options for the order-flow generator.
/// Hot fields (<see cref="RatePerSecond"/>, <see cref="Outcome"/>) may change while Running;
/// all other fields are locked and only apply in Idle/Paused.
/// Published as immutable snapshots (never mutated in place).
/// </summary>
public sealed record SimulationConfig : IValidatableObject
{
    /// <summary>
    /// Total orders to generate in the round. <c>0</c> means unlimited.
    /// Locked while Running.
    /// </summary>
    [Range(0, long.MaxValue)]
    public long TotalOrders { get; init; }

    /// <summary>
    /// Target application-message send rate in messages per second.
    /// Hot — may be updated while Running.
    /// </summary>
    [Range(1, 100_000)]
    public int RatePerSecond { get; init; } = 10;

    /// <summary>
    /// Account / client identifiers used when populating Parties / Account fields.
    /// Locked while Running.
    /// </summary>
    [MinLength(1)]
    public IReadOnlyList<string> Accounts { get; init; } = [];

    /// <summary>
    /// How accounts are selected for each order.
    /// Locked while Running.
    /// </summary>
    public AccountSelection AccountSelection { get; init; } = AccountSelection.RoundRobin;

    /// <summary>
    /// Instruments available to the generator.
    /// Locked while Running.
    /// </summary>
    [MinLength(1)]
    public IReadOnlyList<Instrument> Instruments { get; init; } = [];

    /// <summary>
    /// Outcome distribution for order lifecycles.
    /// Hot — may be updated while Running.
    /// </summary>
    [Required]
    public required OutcomeMix Outcome { get; init; }

    /// <summary>
    /// Maximum number of partial fills before a final fill, when the outcome is PartialFill.
    /// Locked while Running.
    /// </summary>
    [Range(1, 100)]
    public int PartialFillsMax { get; init; } = 3;

    /// <summary>
    /// When <c>true</c>, application messages set TestMessageIndicator (464) = Y.
    /// Locked while Running.
    /// </summary>
    public bool TestMessageIndicator { get; init; } = true;

    /// <summary>
    /// FIX MsgType values the generator is allowed to emit (v1 default: ExecutionReport <c>8</c>).
    /// Locked while Running.
    /// </summary>
    [MinLength(1)]
    public IReadOnlyList<string> EnabledMessageTypes { get; init; } = ["8"];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        for (var i = 0; i < Accounts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Accounts[i]))
            {
                yield return new ValidationResult(
                    $"Accounts[{i}] must be a non-empty account identifier.",
                    [nameof(Accounts)]);
            }
        }

        for (var i = 0; i < Instruments.Count; i++)
        {
            var instrument = Instruments[i];
            var context = new ValidationContext(instrument) { MemberName = $"{nameof(Instruments)}[{i}]" };
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(instrument, context, results, validateAllProperties: true))
            {
                foreach (var result in results)
                {
                    yield return new ValidationResult(
                        result.ErrorMessage,
                        [nameof(Instruments)]);
                }
            }
        }

        var outcomeContext = new ValidationContext(Outcome);
        var outcomeResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(Outcome, outcomeContext, outcomeResults, validateAllProperties: true))
        {
            foreach (var result in outcomeResults)
            {
                yield return result;
            }
        }

        foreach (var msgType in EnabledMessageTypes)
        {
            if (string.IsNullOrWhiteSpace(msgType))
            {
                yield return new ValidationResult(
                    "EnabledMessageTypes entries must be non-empty FIX MsgType values.",
                    [nameof(EnabledMessageTypes)]);
            }
        }
    }
}
