using System.Buffers;
using System.Text;
using DropCopySim.Core.Fix;
using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Tests;

public class BusinessMessageRejectFactoryTests
{
    [Fact]
    public void Build_ExplicitFields_WritesParseableRejectIncludingSanityValidation()
    {
        var factory = new BusinessMessageRejectFactory("ENTRYPOINT-FIRM-0001", "CLIENT1");
        var frame = new byte[2048];
        var order = CreateOrder() with
        {
            RefMsgType = "F",
            BusinessRejectReason = 5,
            Text = "Missing field",
            SanityValidation = 'Y',
        };

        int written = factory.Build(frame, order, seqNum: 3, businessSeqNum: 9);
        ReadOnlySequence<byte> remaining = new(frame.AsMemory(0, written));

        Assert.True(Fix44FrameScanner.TryDecode(ref remaining, out ReadOnlySequence<byte> decoded));
        Assert.True(remaining.IsEmpty);
        string message = Encoding.ASCII.GetString(decoded.ToArray());
        Assert.Contains("35=j\u0001", message);
        Assert.Contains("34=3\u0001", message);
        Assert.Contains("10016=9\u0001", message);
        Assert.Contains("464=Y\u0001", message);
        Assert.Contains("372=F\u0001", message);
        Assert.Contains("380=5\u0001", message);
        Assert.Contains("58=Missing field\u0001", message);
        Assert.Contains("10031=Y\u0001", message);
    }

    [Fact]
    public void Build_MissingOptionalFields_UsesDefaultsAndOmitsSanityValidation()
    {
        var factory = new BusinessMessageRejectFactory(
            "ENTRYPOINT-FIRM-0001",
            "CLIENT1",
            testMessageIndicator: false);
        var frame = new byte[2048];

        int written = factory.Build(frame, CreateOrder(), 1, 1);
        string message = Encoding.ASCII.GetString(frame, 0, written);

        Assert.Contains("372=D\u0001", message);
        Assert.Contains("380=0\u0001", message);
        Assert.Contains("58=Business message rejected\u0001", message);
        Assert.DoesNotContain("10031=", message);
        Assert.DoesNotContain("464=", message);
    }

    [Fact]
    public void Build_InvalidSanityValidation_ThrowsArgumentException()
    {
        var factory = new BusinessMessageRejectFactory("ENTRYPOINT-FIRM-0001", "CLIENT1");

        Assert.Throws<ArgumentException>(() =>
            factory.Build(new byte[2048], CreateOrder() with { SanityValidation = 'X' }, 1, 1));
    }

    private static OrderContext CreateOrder() => new()
    {
        ClOrdId = "20261104-00000001",
        OrderId = "ORDER-1",
        ExecId = "EXEC-1",
        ExecType = '8',
        OrdStatus = '8',
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
