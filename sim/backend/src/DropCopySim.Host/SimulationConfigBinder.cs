using DropCopySim.Core.Generation;

namespace DropCopySim.Host;

/// <summary>
/// Builds a <see cref="SimulationConfig"/> from configuration with full-replace semantics for its
/// collection properties.
/// </summary>
/// <remarks>
/// The default <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> <em>appends</em> to
/// collections that are already populated by a record initializer (for example
/// <see cref="SimulationConfig.EnabledMessageTypes"/> defaults to <c>["8"]</c>), which duplicates values
/// supplied in <c>appsettings.json</c>. This binder reads each configured array explicitly and only falls
/// back to the record default when the configuration key is absent, so configuration fully replaces the
/// defaults instead of extending them.
/// </remarks>
public static class SimulationConfigBinder
{
    /// <summary>
    /// Builds an immutable <see cref="SimulationConfig"/> snapshot from a configuration section.
    /// </summary>
    /// <param name="section">The <c>DropCopySim:Simulation</c> configuration section.</param>
    /// <returns>A fully populated config snapshot. Validate it before use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="section"/> is null.</exception>
    public static SimulationConfig Build(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var defaults = new SimulationConfig { Outcome = new OutcomeMix() };

        var accounts = section.GetSection(nameof(SimulationConfig.Accounts)).Get<string[]>();
        var instruments = section.GetSection(nameof(SimulationConfig.Instruments)).Get<Instrument[]>();
        var enabledMessageTypes = section.GetSection(nameof(SimulationConfig.EnabledMessageTypes)).Get<string[]>();
        var outcome = section.GetSection(nameof(SimulationConfig.Outcome)).Get<OutcomeMix>();

        return new SimulationConfig
        {
            TotalOrders = section.GetValue(nameof(SimulationConfig.TotalOrders), defaults.TotalOrders),
            RatePerSecond = section.GetValue(nameof(SimulationConfig.RatePerSecond), defaults.RatePerSecond),
            Accounts = accounts is { Length: > 0 } ? accounts : defaults.Accounts,
            AccountSelection = section.GetValue(
                nameof(SimulationConfig.AccountSelection), defaults.AccountSelection),
            Instruments = instruments is { Length: > 0 } ? instruments : defaults.Instruments,
            Outcome = outcome ?? defaults.Outcome,
            PartialFillsMax = section.GetValue(
                nameof(SimulationConfig.PartialFillsMax), defaults.PartialFillsMax),
            TestMessageIndicator = section.GetValue(
                nameof(SimulationConfig.TestMessageIndicator), defaults.TestMessageIndicator),
            EnabledMessageTypes = enabledMessageTypes is { Length: > 0 }
                ? enabledMessageTypes
                : defaults.EnabledMessageTypes,
        };
    }
}
