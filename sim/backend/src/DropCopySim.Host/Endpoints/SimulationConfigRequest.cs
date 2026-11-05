using DropCopySim.Core.Generation;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Request body for <c>PUT /v1/config</c> and the optional body of <c>POST /v1/sim/start</c>.
/// </summary>
/// <remarks>
/// Every field is optional: an omitted (<c>null</c>) field keeps the value from the currently effective
/// <see cref="SimulationConfig"/>, so the SimPanel can send a partial delta (e.g. only
/// <see cref="RatePerSecond"/> while Running). <see cref="Merge"/> applies the non-null fields onto a
/// base snapshot, producing the candidate the <see cref="SimulationController"/> then validates against
/// the hybrid-reconfiguration policy (FR-SIM-06, FR-SIM-08). The merge never mutates the base instance.
/// </remarks>
public sealed record SimulationConfigRequest
{
    /// <summary>Total orders to generate; <c>0</c> means unlimited. Locked while Running.</summary>
    public long? TotalOrders { get; init; }

    /// <summary>Target send rate in messages per second. Hot — adjustable while Running.</summary>
    public int? RatePerSecond { get; init; }

    /// <summary>Account / client identifiers. Locked while Running.</summary>
    public IReadOnlyList<string>? Accounts { get; init; }

    /// <summary>Account selection strategy. Locked while Running.</summary>
    public AccountSelection? AccountSelection { get; init; }

    /// <summary>Instruments available to the generator. Locked while Running.</summary>
    public IReadOnlyList<Instrument>? Instruments { get; init; }

    /// <summary>Outcome distribution. Hot — adjustable while Running.</summary>
    public OutcomeMix? Outcome { get; init; }

    /// <summary>Maximum partial fills before a final fill. Locked while Running.</summary>
    public int? PartialFillsMax { get; init; }

    /// <summary>When set, application messages carry TestMessageIndicator (464) = Y. Locked while Running.</summary>
    public bool? TestMessageIndicator { get; init; }

    /// <summary>Enabled FIX MsgType values the generator may emit. Locked while Running.</summary>
    public IReadOnlyList<string>? EnabledMessageTypes { get; init; }

    /// <summary>
    /// Produces a candidate <see cref="SimulationConfig"/> by overlaying the non-null fields of this
    /// request onto <paramref name="current"/>. The base snapshot is never mutated.
    /// </summary>
    /// <param name="current">The currently effective configuration snapshot.</param>
    /// <returns>An immutable candidate snapshot carrying the merged values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public SimulationConfig Merge(SimulationConfig current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            TotalOrders = TotalOrders ?? current.TotalOrders,
            RatePerSecond = RatePerSecond ?? current.RatePerSecond,
            Accounts = Accounts ?? current.Accounts,
            AccountSelection = AccountSelection ?? current.AccountSelection,
            Instruments = Instruments ?? current.Instruments,
            Outcome = Outcome ?? current.Outcome,
            PartialFillsMax = PartialFillsMax ?? current.PartialFillsMax,
            TestMessageIndicator = TestMessageIndicator ?? current.TestMessageIndicator,
            EnabledMessageTypes = EnabledMessageTypes ?? current.EnabledMessageTypes,
        };
    }
}
