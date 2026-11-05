using System.Buffers;
using System.Text;
using DropCopySim.Core.Fix;
using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Tests;

public class ExecutionReportFactoryTests
{
    [Fact]
    public void Build_ValidOrder_WritesParseableB3ExecutionReport()
    {
        var factory = new ExecutionReportFactory("ENTRYPOINT-FIRM-0001", "CLIENT1");
        var frame = new byte[2048];

        int written = factory.Build(frame, CreateOrder(), seqNum: 7, businessSeqNum: 12);
        ReadOnlySequence<byte> remaining = new(frame.AsMemory(0, written));

        Assert.True(Fix44FrameScanner.TryDecode(ref remaining, out ReadOnlySequence<byte> decoded));
        Assert.True(remaining.IsEmpty);
        string message = Encoding.ASCII.GetString(decoded.ToArray());
        Assert.Contains("35=8\u0001", message);
        Assert.Contains("34=7\u0001", message);
        Assert.Contains("10016=12\u0001", message);
        Assert.Contains("464=Y\u0001", message);
        Assert.Contains("37=ORDER-1\u0001", message);
        Assert.Contains("48=200001\u0001", message);
        Assert.Contains("22=8\u0001", message);
        Assert.Contains("55=PETR4\u0001", message);
        Assert.Contains("207=BVMF\u0001", message);
        Assert.Contains("453=2\u0001448=CLEARING\u0001447=D\u0001452=4\u0001448=ACCOUNT-1\u0001447=D\u0001452=3\u0001", message);
        Assert.Contains("10029=1\u0001", message);
        Assert.Contains("9773=Y\u0001", message);
        Assert.Contains("32=2\u0001", message);
        Assert.Contains("31=25.5\u0001", message);
        Assert.Contains("151=8\u0001", message);
        Assert.Contains("14=2\u0001", message);
        Assert.Contains("6=25.5\u0001", message);
        Assert.Contains("60=", message);
    }

    [Fact]
    public void Build_TestIndicatorDisabled_OmitsIndicator()
    {
        var factory = new ExecutionReportFactory("ENTRYPOINT-FIRM-0001", "CLIENT1", testMessageIndicator: false);
        var frame = new byte[2048];

        int written = factory.Build(frame, CreateOrder() with { MmProtectionReset = null }, 1, 1);
        string message = Encoding.ASCII.GetString(frame, 0, written);

        Assert.DoesNotContain("464=", message);
        Assert.DoesNotContain("9773=", message);
    }

    [Theory]
    [InlineData("123456789012345678901")]
    [InlineData("bad id")]
    [InlineData("bad.id")]
    public void Build_InvalidClOrdId_ThrowsArgumentException(string clOrdId)
    {
        var factory = new ExecutionReportFactory("ENTRYPOINT-FIRM-0001", "CLIENT1");

        Assert.Throws<ArgumentException>(() =>
            factory.Build(new byte[2048], CreateOrder() with { ClOrdId = clOrdId }, 1, 1));
    }

    private static OrderContext CreateOrder() => new()
    {
        ClOrdId = "20261104-00000001",
        OrderId = "ORDER-1",
        ExecId = "EXEC-1",
        ExecType = '1',
        OrdStatus = '1',
        BtsFinalTxOrdStatus = '1',
        MmProtectionReset = 'Y',
        Side = '1',
        SecurityId = 200001,
        Symbol = "PETR4",
        SecurityExchange = "BVMF",
        OrderQty = 10,
        LastQty = 2,
        LeavesQty = 8,
        CumQty = 2,
        Price = 25.5m,
        LastPx = 25.5m,
        AvgPx = 25.5m,
        ClearingFirm = "CLEARING",
        Account = "ACCOUNT-1",
    };
}
