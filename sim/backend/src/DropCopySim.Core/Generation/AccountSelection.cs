namespace DropCopySim.Core.Generation;

/// <summary>
/// Strategy used by the order-flow generator to pick an account from
/// <see cref="SimulationConfig.Accounts"/> for each simulated order.
/// </summary>
public enum AccountSelection
{
    /// <summary>Cycle through accounts in configured order.</summary>
    RoundRobin = 0,

    /// <summary>Pick an account uniformly at random for each order.</summary>
    Random = 1,
}
