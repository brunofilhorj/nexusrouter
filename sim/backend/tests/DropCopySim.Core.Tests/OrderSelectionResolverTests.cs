using System.Text;
using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Tests;

public class OrderSelectionResolverTests
{
    [Fact]
    public void Resolve_RoundRobin_CyclesAccountsAndInstruments()
    {
        var resolver = new OrderSelectionResolver(CreateConfig(AccountSelection.RoundRobin));
        var template = CreateTemplate();

        var first = resolver.Resolve(template);
        var second = resolver.Resolve(template);
        var third = resolver.Resolve(template);
        var fourth = resolver.Resolve(template);

        Assert.Equal("ACC001", first.Account);
        Assert.Equal(200001, first.SecurityId);
        Assert.Equal("PETR4", first.Symbol);
        Assert.Equal("BVMF", first.SecurityExchange);

        Assert.Equal("ACC002", second.Account);
        Assert.Equal("VALE3", second.Symbol);

        Assert.Equal("ACC003", third.Account);
        Assert.Equal("PETR4", third.Symbol);

        Assert.Equal("ACC001", fourth.Account);
        Assert.Equal("VALE3", fourth.Symbol);
        Assert.Equal(template.ClOrdId, fourth.ClOrdId);
        Assert.Equal(template.OrderQty, fourth.OrderQty);
    }

    [Fact]
    public void Resolve_Random_UsesInjectedSequenceForAccountsOnly()
    {
        var resolver = new OrderSelectionResolver(
            CreateConfig(AccountSelection.Random),
            new SequenceRandom(2, 0));

        var first = resolver.Resolve(CreateTemplate());
        var second = resolver.Resolve(CreateTemplate());

        Assert.Equal("ACC003", first.Account);
        Assert.Equal("PETR4", first.Symbol);
        Assert.Equal("ACC001", second.Account);
        Assert.Equal("VALE3", second.Symbol);
    }

    [Fact]
    public void Resolve_SelectedAccount_IsWrittenOnAccountAndClientParty()
    {
        var resolver = new OrderSelectionResolver(CreateConfig(AccountSelection.RoundRobin));
        var order = resolver.Resolve(CreateTemplate());
        var factory = new ExecutionReportFactory("ENTRYPOINT-FIRM-0001", "CLIENT1", testMessageIndicator: false);
        var frame = new byte[2048];

        int written = factory.Build(frame, order, seqNum: 1, businessSeqNum: 1);
        string message = Encoding.ASCII.GetString(frame, 0, written);

        Assert.Contains("1=ACC001\u0001", message);
        Assert.Contains("448=ACC001\u0001447=D\u0001452=3\u0001", message);
        Assert.Contains("48=200001\u0001", message);
        Assert.Contains("22=8\u0001", message);
        Assert.Contains("55=PETR4\u0001", message);
    }

    [Fact]
    public void Constructor_EmptyAccounts_ThrowsArgumentException()
    {
        var config = CreateConfig(AccountSelection.RoundRobin) with { Accounts = [] };

        Assert.Throws<ArgumentException>(() => new OrderSelectionResolver(config));
    }

    private static SimulationConfig CreateConfig(AccountSelection selection) => new()
    {
        Accounts = ["ACC001", "ACC002", "ACC003"],
        AccountSelection = selection,
        Instruments =
        [
            new Instrument { SecurityId = 200001, Symbol = "PETR4", SecurityExchange = "BVMF" },
            new Instrument { SecurityId = 200002, Symbol = "VALE3", SecurityExchange = "BVMF" },
        ],
        Outcome = new OutcomeMix { FillPct = 100 },
    };

    private static OrderContext CreateTemplate() => new()
    {
        ClOrdId = "20261104-00000001",
        OrderId = "ORDER-1",
        ExecId = "EXEC-1",
        ExecType = '0',
        OrdStatus = '0',
        Side = '1',
        SecurityId = 1,
        Symbol = "TEMPLATE",
        SecurityExchange = "XXXX",
        OrderQty = 10,
        Price = 25.5m,
        ClearingFirm = "CLEARING",
        Account = "UNSET",
    };

    private sealed class SequenceRandom : Random
    {
        private readonly int[] _values;
        private int _index;

        public SequenceRandom(params int[] values) => _values = values;

        public override int Next(int maxValue) => _values[_index++];
    }
}
