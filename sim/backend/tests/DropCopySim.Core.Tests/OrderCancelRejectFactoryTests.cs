using System.Buffers;
using System.Text;
using DropCopySim.Core.Fix;
using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Tests;

public class OrderCancelRejectFactoryTests
{
    [Fact]
    public void Build_ValidOrder_WritesParseableCancelReject()
    {
        var factory = new OrderCancelRejectFactory("ENTRYPOINT-FIRM-0001", "CLIENT1");
        var frame = new byte[2048];
        var order = CreateOrder() with
        {
            OrigClOrdId = "ORIG-1",
            CxlRejResponseTo = '2',
            CxlRejReason = 99,
            OrdStatus = '4',
        };

        int written = factory.Build(frame, order, seqNum: 7, businessSeqNum: 12);
        ReadOnlySequence<byte> remaining = new(frame.AsMemory(0, written));

        Assert.True(Fix44FrameScanner.TryDecode(ref remaining, out ReadOnlySequence<byte> decoded));
        Assert.True(remaining.IsEmpty);
        string message = Encoding.ASCII.GetString(decoded.ToArray());
        Assert.Contains("35=9\u0001", message);
        Assert.Contains("34=7\u0001", message);
        Assert.Contains("10016=12\u0001", message);
        Assert.Contains("464=Y\u0001", message);
        Assert.Contains("37=ORDER-1\u0001", message);
        Assert.Contains("11=20261104-00000001\u0001", message);
        Assert.Contains("41=ORIG-1\u0001", message);
        Assert.Contains("39=4\u0001", message);
        Assert.Contains("434=2\u0001", message);
        Assert.Contains("102=99\u0001", message);
    }

    [Fact]
    public void Build_MissingOptionalFields_UsesDefaultsAndCanOmitTestIndicator()
    {
        var factory = new OrderCancelRejectFactory("ENTRYPOINT-FIRM-0001", "CLIENT1", testMessageIndicator: false);
        var frame = new byte[2048];

        int written = factory.Build(frame, CreateOrder(), 1, 1);
        string message = Encoding.ASCII.GetString(frame, 0, written);

        Assert.Contains("41=20261104-00000001\u0001", message);
        Assert.Contains("434=1\u0001", message);
        Assert.Contains("102=0\u0001", message);
        Assert.DoesNotContain("464=", message);
    }

    [Theory]
    [InlineData("123456789012345678901")]
    [InlineData("bad id")]
    public void Build_InvalidClOrdId_ThrowsArgumentException(string clOrdId)
    {
        var factory = new OrderCancelRejectFactory("ENTRYPOINT-FIRM-0001", "CLIENT1");

        Assert.Throws<ArgumentException>(() =>
            factory.Build(new byte[2048], CreateOrder() with { ClOrdId = clOrdId }, 1, 1));
    }

    private static OrderContext CreateOrder() => new()
    {
        ClOrdId = "20261104-00000001",
        OrderId = "ORDER-1",
        ExecId = "EXEC-1",
        ExecType = '4',
        OrdStatus = '4',
        Side = '1',
        SecurityId = 200001,
        Symbol = "PETR4",
        SecurityExchange = "BVMF",
        OrderQty = 10,
        LastQty = 0,
        LeavesQty = 10,
        CumQty = 0,
        Price = 25.5m,
        LastPx = 0,
        AvgPx = 0,
        ClearingFirm = "CLEARING",
        Account = "ACCOUNT-1",
    };
}
