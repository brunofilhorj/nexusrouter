using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Tests;

public class OrderLifecycleModelTests
{
    [Fact]
    public void CreateLifecycle_FillOutcome_ReturnsNewThenFill()
    {
        var events = OrderLifecycleModel.CreateLifecycle(
            CreateOrder(),
            CreateOutcome(fillPct: 100),
            partialFillsMax: 3,
            new FixedRandom(0));

        Assert.Collection(
            events,
            newEvent =>
            {
                Assert.Equal('0', newEvent.ExecType);
                Assert.Equal('0', newEvent.OrdStatus);
                Assert.Equal(0, newEvent.CumQty);
                Assert.Equal(10, newEvent.LeavesQty);
            },
            fillEvent =>
            {
                Assert.Equal('2', fillEvent.ExecType);
                Assert.Equal('2', fillEvent.OrdStatus);
                Assert.Equal(10, fillEvent.LastQty);
                Assert.Equal(10, fillEvent.CumQty);
                Assert.Equal(0, fillEvent.LeavesQty);
            });
    }

    [Fact]
    public void CreateLifecycle_PartialFillOutcome_ConservesQuantityAndUsesUniqueExecutionIds()
    {
        var events = OrderLifecycleModel.CreateLifecycle(
            CreateOrder(),
            CreateOutcome(partialFillPct: 100),
            partialFillsMax: 3,
            new FixedRandom(0, 2));

        Assert.Equal('0', events[0].ExecType);
        Assert.Equal('2', events[^1].ExecType);
        Assert.All(events.Skip(1).SkipLast(1), execution =>
        {
            Assert.Equal('1', execution.ExecType);
            Assert.Equal('1', execution.OrdStatus);
            Assert.True(execution.LastQty > 0);
            Assert.True(execution.LeavesQty > 0);
        });
        Assert.Equal(10, events[^1].CumQty);
        Assert.Equal(0, events[^1].LeavesQty);
        Assert.Equal(events.Count, events.Select(static e => e.ExecId).Distinct().Count());
    }

    [Theory]
    [InlineData(0, '4')]
    [InlineData(50, '8')]
    public void CreateLifecycle_TerminalOutcome_EndsWithMatchingStatus(int roll, char expectedExecType)
    {
        var events = OrderLifecycleModel.CreateLifecycle(
            CreateOrder(),
            CreateOutcome(canceledPct: 50, rejectedPct: 50),
            partialFillsMax: 3,
            new FixedRandom(roll));

        Assert.Equal('0', events[0].ExecType);
        Assert.Equal(expectedExecType, events[^1].ExecType);
        Assert.Equal(expectedExecType, events[^1].OrdStatus);
        Assert.Equal(0, events[^1].LeavesQty);
    }

    [Fact]
    public void GetOrdStatus_UsesExecTypeAndLeavesQtyIndependently()
    {
        var cases = new (char ExecType, decimal LeavesQty, char Expected)[]
        {
            ('0', 10, '0'),
            ('1', 4, '1'),
            ('2', 4, '1'),
            ('1', 0, '2'),
            ('4', 0, '4'),
            ('8', 0, '8'),
        };

        foreach (var testCase in cases)
        {
            Assert.Equal(testCase.Expected, OrderLifecycleModel.GetOrdStatus(testCase.ExecType, testCase.LeavesQty));
        }
    }

    [Fact]
    public void CreateLifecycle_UsesOutcomePercentagesAsWeightedBoundaries()
    {
        var outcome = new OutcomeMix
        {
            FillPct = 40,
            PartialFillPct = 30,
            CanceledPct = 20,
            RejectedPct = 10,
        };

        var fillEvents = CreateLifecycle(outcome, 0);
        var partialFillEvents = CreateLifecycle(outcome, 40, 1);
        Assert.Equal(2, fillEvents.Count);
        Assert.Equal(4, partialFillEvents.Count);
        Assert.Equal('2', fillEvents[^1].ExecType);
        Assert.Equal('2', partialFillEvents[^1].ExecType);
        Assert.Equal('4', CreateLifecycle(outcome, 70)[^1].ExecType);
        Assert.Equal('8', CreateLifecycle(outcome, 90)[^1].ExecType);
    }

    [Fact]
    public void CreateLifecycle_InvalidOutcomePercentages_ThrowsArgumentException()
    {
        var outcome = CreateOutcome(fillPct: 60);

        Assert.Throws<ArgumentException>(() =>
            OrderLifecycleModel.CreateLifecycle(CreateOrder(), outcome, 3, new FixedRandom(0)));
    }

    private static IReadOnlyList<OrderContext> CreateLifecycle(
        OutcomeMix outcome,
        int outcomeRoll,
        int? partialFillCountRoll = null) =>
        OrderLifecycleModel.CreateLifecycle(
            CreateOrder(),
            outcome,
            partialFillsMax: 3,
            partialFillCountRoll is null
                ? new FixedRandom(outcomeRoll)
                : new FixedRandom(outcomeRoll, partialFillCountRoll.Value));

    private static OutcomeMix CreateOutcome(
        int fillPct = 0,
        int partialFillPct = 0,
        int canceledPct = 0,
        int rejectedPct = 0) =>
        new()
        {
            FillPct = fillPct,
            PartialFillPct = partialFillPct,
            CanceledPct = canceledPct,
            RejectedPct = rejectedPct,
        };

    private static OrderContext CreateOrder() => new()
    {
        ClOrdId = "20261104-00000001",
        OrderId = "ORDER-1",
        ExecId = "EXEC-1",
        ExecType = '0',
        OrdStatus = '0',
        Side = '1',
        SecurityId = 200001,
        Symbol = "PETR4",
        SecurityExchange = "BVMF",
        OrderQty = 10,
        Price = 25.5m,
        ClearingFirm = "CLEARING",
        Account = "ACCOUNT-1",
    };

    private sealed class FixedRandom(params int[] values) : Random
    {
        private int _index;

        public override int Next(int maxValue)
        {
            var value = values[_index++];
            return value % maxValue;
        }

        public override int Next(int minValue, int maxValue)
        {
            var value = values[_index++];
            return minValue + (value % (maxValue - minValue));
        }
    }
}
