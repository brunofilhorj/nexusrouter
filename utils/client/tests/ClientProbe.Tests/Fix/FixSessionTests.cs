using System.Text;
using ClientProbe.Fix;

namespace ClientProbe.Tests.Fix;

public sealed class FixSessionTests
{
    private static readonly TimeProvider Clock = new FixedTime(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static FixSessionWriter Writer() => new("SND", "TGT", Clock);

    private static string Text(byte[] b) => Encoding.ASCII.GetString(b).Replace('\u0001', '|');

    private static void AssertValid(byte[] frame)
    {
        var s = Encoding.ASCII.GetString(frame);
        var bodyStart = s.IndexOf('\u0001') + 1;
        var lenEnd = s.IndexOf('\u0001', bodyStart);
        var declared = int.Parse(s[(bodyStart + 2)..lenEnd]);
        var trailerStart = s.LastIndexOf("\u000110=", StringComparison.Ordinal) + 1;
        Assert.Equal(trailerStart - (lenEnd + 1), declared);
        var expected = FixSessionWriter.ComputeCheckSum(frame.AsSpan(0, trailerStart));
        Assert.Equal($"10={expected:D3}\u0001", s[trailerStart..]);
    }

    [Fact]
    public void Logon_HasFieldsBodyLengthAndCheckSum()
    {
        var f = Writer().Logon(1, 30);
        Assert.Equal("8=FIX.4.4|9=", Text(f)[..12]);
        Assert.Contains("|35=A|49=SND|56=TGT|34=1|52=20260102-03:04:05.006|98=0|108=30|10=", Text(f));
        AssertValid(f);
    }

    [Fact]
    public void Heartbeat_HasSeqAndValidTrailer()
    {
        var f = Writer().Heartbeat(42);
        Assert.Contains("|35=0|49=SND|56=TGT|34=42|", Text(f));
        AssertValid(f);
        AssertValid(Writer().Heartbeat(2, "T1"));
    }

    [Fact]
    public void Logout_HasMsgType5AndValidTrailer()
    {
        var f = Writer().Logout(7, "bye");
        Assert.Contains("|35=5|", Text(f));
        Assert.Contains("|58=bye|", Text(f));
        AssertValid(f);
    }

    [Fact]
    public void CheckSum_IsSumModulo256()
    {
        Assert.Equal((1 + 2 + 255) % 256, FixSessionWriter.ComputeCheckSum([1, 2, 255]));
    }

    [Fact]
    public void Scanner_ReadsFrameSplitInChunks()
    {
        var f = Writer().Logon(1, 30);
        var sc = new FixFrameScanner();
        for (var i = 0; i < f.Length - 1; i++)
        {
            sc.Append(f.AsSpan(i, 1));
            Assert.False(sc.TryRead(out _));
        }
        sc.Append(f.AsSpan(f.Length - 1, 1));
        Assert.True(sc.TryRead(out var got));
        Assert.Equal(f, got);
        Assert.Equal(0, sc.Pending);
    }

    [Fact]
    public void Scanner_ReadsTwoFramesAndSkipsLeadingGarbage()
    {
        var a = Writer().Heartbeat(1);
        var b = Writer().Logout(2);
        var sc = new FixFrameScanner();
        sc.Append("xx"u8);
        sc.Append(a);
        sc.Append(b);
        Assert.True(sc.TryRead(out var f1));
        Assert.True(sc.TryRead(out var f2));
        Assert.False(sc.TryRead(out _));
        Assert.Equal(a, f1);
        Assert.Equal(b, f2);
    }

    [Fact]
    public void Scanner_WaitsForCompleteTrailer()
    {
        var sc = new FixFrameScanner();
        sc.Append("8=FIX.4.4\u00019=5\u000135=0\u000110=12"u8);
        Assert.False(sc.TryRead(out _));
        sc.Append("3\u0001"u8);
        Assert.True(sc.TryRead(out var f));
        Assert.EndsWith("10=123\u0001", Encoding.ASCII.GetString(f));
    }
}
