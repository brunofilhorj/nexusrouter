using NexusRouter.Domain.Models;
using NexusRouter.Egress.Fix;

namespace NexusRouter.Egress.Tests;

public sealed class FixMessageSerializerTests
{
    [Fact]
    public void Serialize_ParsedOrder_RewritesHeaderAndChecksum()
    {
        byte[] beginString = "FIX.4.4"u8.ToArray();
        byte[] msgType = "D"u8.ToArray();
        byte[] oldSequence = "1"u8.ToArray();
        byte[] clOrdId = "ABC"u8.ToArray();
        FixField[] fields =
        [
            new(8, beginString),
            new(35, msgType),
            new(34, oldSequence),
            new(11, clOrdId),
            new(10, "000"u8.ToArray()),
        ];
        var message = new FixMessage(msgType, fields);
        Span<byte> destination = stackalloc byte[256];

        int written = FixMessageSerializer.Serialize(in message, destination, seqNum: 7, "NEXUS", "OMS");

        ReadOnlySpan<byte> frame = destination[..written];
        string text = ToPrintable(frame);
        Assert.StartsWith("8=FIX.4.4\u0001", text);
        Assert.Contains("35=D\u0001", text);
        Assert.Contains("49=NEXUS\u0001", text);
        Assert.Contains("56=OMS\u0001", text);
        Assert.Contains("34=7\u0001", text);
        Assert.Contains("11=ABC\u0001", text);
        Assert.DoesNotContain("34=1\u0001", text);
        Assert.Equal(BodyLength(frame), ReadBodyLength(frame));
        Assert.EndsWith("\u000110=" + Checksum(frame) + "\u0001", text);
    }

    [Fact]
    public void Serialize_WithoutBeginString_Throws()
    {
        var destination = new byte[128];

        Assert.Throws<ArgumentException>(() =>
        {
            var message = new FixMessage("0"u8, ReadOnlySpan<FixField>.Empty);
            FixMessageSerializer.Serialize(in message, destination, 1, "NEXUS", "OMS");
        });
    }

    private static int ReadBodyLength(ReadOnlySpan<byte> frame)
    {
        int valueStart = frame.IndexOf("9="u8) + 2;
        int valueEnd = frame[valueStart..].IndexOf((byte)1);
        int length = 0;
        for (int i = 0; i < valueEnd; i++)
        {
            length = (length * 10) + (frame[valueStart + i] - (byte)'0');
        }

        return length;
    }

    private static int BodyLength(ReadOnlySpan<byte> frame)
    {
        int headerEnd = frame.IndexOf("9="u8);
        headerEnd += 2;
        headerEnd += frame[headerEnd..].IndexOf((byte)1) + 1;
        int checksumAt = frame.LastIndexOf("\u000110="u8);
        return checksumAt + 1 - headerEnd;
    }

    private static string Checksum(ReadOnlySpan<byte> frame)
    {
        int marker = frame.LastIndexOf("\u000110="u8);
        Assert.True(marker >= 0);
        int sum = 0;
        for (int i = 0; i < marker + 1; i++)
        {
            sum += frame[i];
        }

        return (sum & 0xFF).ToString("000");
    }

    private static string ToPrintable(ReadOnlySpan<byte> frame)
    {
        var chars = new char[frame.Length];
        for (int i = 0; i < frame.Length; i++)
        {
            chars[i] = (char)frame[i];
        }

        return new string(chars);
    }
}
