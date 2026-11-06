using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Tests;

public class ApplicationMessageFactoryCatalogTests
{
    [Fact]
    public void Create_DefaultMessageTypes_ReturnsOnlyExecutionReport()
    {
        var factories = ApplicationMessageFactoryCatalog.Create(
            "ENTRYPOINT-FIRM-0001",
            "CLIENT1",
            CreateConfig(["8"]));

        IFixMessageFactory factory = Assert.Single(factories);
        Assert.IsType<ExecutionReportFactory>(factory);
    }

    [Fact]
    public void Create_OptionalTypes_IncludesRejectFactoriesInConfigOrder()
    {
        var factories = ApplicationMessageFactoryCatalog.Create(
            "ENTRYPOINT-FIRM-0001",
            "CLIENT1",
            CreateConfig(["9", "j"]));

        Assert.Equal(2, factories.Count);
        Assert.IsType<OrderCancelRejectFactory>(factories[0]);
        Assert.IsType<BusinessMessageRejectFactory>(factories[1]);
    }

    [Fact]
    public void Create_UnsupportedType_ThrowsArgumentException()
    {
        var config = CreateConfig(["8"]);
        config = config with { EnabledMessageTypes = ["X"] };

        Assert.Throws<ArgumentException>(() =>
            ApplicationMessageFactoryCatalog.Create("ENTRYPOINT-FIRM-0001", "CLIENT1", config));
    }

    private static SimulationConfig CreateConfig(IReadOnlyList<string> messageTypes) => new()
    {
        Accounts = ["ACC001"],
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
        EnabledMessageTypes = messageTypes,
    };
}
