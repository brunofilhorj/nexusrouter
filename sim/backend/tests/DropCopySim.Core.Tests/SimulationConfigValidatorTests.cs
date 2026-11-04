using DropCopySim.Core.Generation;
using Microsoft.Extensions.Options;

namespace DropCopySim.Core.Tests;

public class SimulationConfigValidatorTests
{
    private readonly SimulationConfigValidator _sut = new();

    [Fact]
    public void Validate_Accepts_DefaultAlignedConfig()
    {
        var config = CreateValidConfig();

        var result = _sut.Validate(Options.DefaultName, config);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenOutcomePercentagesDoNotSumTo100()
    {
        var config = CreateValidConfig() with
        {
            Outcome = new OutcomeMix
            {
                FillPct = 50,
                PartialFillPct = 20,
                CanceledPct = 7,
                RejectedPct = 3,
            },
        };

        var result = _sut.Validate(Options.DefaultName, config);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static f => f.Contains("sum to 100", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_WhenAccountsEmpty()
    {
        var config = CreateValidConfig() with { Accounts = [] };

        var result = _sut.Validate(Options.DefaultName, config);

        Assert.True(result.Failed);
    }

    private static SimulationConfig CreateValidConfig() => new()
    {
        TotalOrders = 0,
        RatePerSecond = 10,
        Accounts = ["ACC001", "ACC002"],
        AccountSelection = AccountSelection.RoundRobin,
        Instruments =
        [
            new Instrument
            {
                SecurityId = 200001,
                Symbol = "PETR4",
                SecurityExchange = "BVMF",
            },
        ],
        Outcome = new OutcomeMix
        {
            FillPct = 70,
            PartialFillPct = 20,
            CanceledPct = 7,
            RejectedPct = 3,
        },
        PartialFillsMax = 3,
        TestMessageIndicator = true,
        EnabledMessageTypes = ["8"],
    };
}
