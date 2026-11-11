using System.Text;
using ClientProbe.Probe;
using ClientProbe.Telemetry;

namespace ClientProbe.Tests.Telemetry;

public sealed class ShortFieldExtractorTests
{
    private static ProbeEvent Run(string frame, int preview = 240) =>
        ShortFieldExtractor.Extract(ProbeTransport.Fix, Encoding.ASCII.GetBytes(frame), DateTimeOffset.UnixEpoch, preview);

    [Fact]
    public void Extract_ExecutionReport_FillsShortFields()
    {
        var e = Run("8=FIX.4.4\u00019=50\u000135=8\u000111=ORD1\u000155=PETR4\u000154=1\u000138=100\u000139=2\u000110=000\u0001");

        Assert.Equal("8", e.MsgType);
        Assert.Equal("ORD1", e.Fields["11"]);
        Assert.Equal("PETR4", e.Fields["55"]);
        Assert.Equal("1", e.Fields["54"]);
        Assert.Equal("100", e.Fields["38"]);
        Assert.Equal("2", e.Fields["39"]);
        Assert.StartsWith("8=FIX.4.4|9=50|35=8|", e.RawPreview);
    }

    [Fact]
    public void Extract_JsonEnvelope_FillsShortFields()
    {
        var e = Run("""{"type":"exec","msgType":"8","ts":1,"fields":{"11":"ORD1","55":"PETR4","39":"2"}}""");
        Assert.Equal("8", e.MsgType);
        Assert.Equal("ORD1", e.Fields["11"]);
        Assert.Equal("PETR4", e.Fields["55"]);
        Assert.Equal("2", e.Fields["39"]);
        Assert.False(e.Fields.ContainsKey("54"));
    }

    [Fact]
    public void Extract_InvalidJson_IsParseFailure()
    {
        var e = Run("{not json");
        Assert.Null(e.MsgType);
    }

    [Fact]
    public void Extract_MissingTags_AreNotListed()
    {
        var e = Run("8=FIX.4.4\u000135=0\u0001");

        Assert.Equal("0", e.MsgType);
        Assert.Empty(e.Fields);
    }

    [Fact]
    public void Extract_NoTag35_LeavesMsgTypeNullAndKeepsPreview()
    {
        var e = Run("garbage\u0001");

        Assert.Null(e.MsgType);
        Assert.Equal("garbage|", e.RawPreview);
    }

    [Fact]
    public void Extract_LongFrame_TruncatesPreview()
    {
        var e = Run("35=8\u0001" + new string('x', 500), preview: 240);

        Assert.Equal(240, e.RawPreview.Length);
    }
}
