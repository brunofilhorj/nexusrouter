using System.Text;

using NexusRouter.Router;

namespace NexusRouter.Router.Tests;

public sealed class PassThroughRewriterTests
{
    [Fact]
    public void Rewrite_WhenSequenceHasTheSameDigitCount_UpdatesSequenceAndChecksum()
    {
        byte[] frame = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);

        bool rewritten = PassThroughRewriter.Rewrite(frame, newSeqNum: 98);

        Assert.True(rewritten);
        Assert.Contains("\u000134=98\u0001", Encoding.ASCII.GetString(frame));
        Assert.True(FixMessageTestData.HasValidChecksum(frame));
        Assert.True(FixMessageTestData.HasValidBodyLength(frame));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(123)]
    public void Rewrite_WhenSequenceHasDifferentDigitCount_ReturnsFalseWithoutChangingFrame(long newSeqNum)
    {
        byte[] frame = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        byte[] original = frame.ToArray();

        bool rewritten = PassThroughRewriter.Rewrite(frame, newSeqNum);

        Assert.False(rewritten);
        Assert.Equal(original, frame);
        Assert.DoesNotContain(" ", Encoding.ASCII.GetString(frame).Split('\u0001').First(f => f.StartsWith("34=")));
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(12, 7)]
    [InlineData(9982, 37582)]
    [InlineData(1, 1234567890123456789)]
    public void TryRewriteForResend_WhenFrameHasSendingTime_InsertsPossDupAndOrigSendingTime(long original, long newSeqNum)
    {
        byte[] source = FixMessageTestData.CreateValidFrame(sequenceNumber: original);
        byte[] destination = new byte[source.Length + PassThroughRewriter.MaxResendGrowthBytes];
        bool rewritten = PassThroughRewriter.TryRewriteForResend(
            source,
            destination,
            newSeqNum,
            "20260102-03:04:05.678"u8,
            out int written);
        Assert.True(rewritten);
        byte[] frame = destination[..written];
        string text = Encoding.ASCII.GetString(frame);
        Assert.Contains($"\u000134={newSeqNum}\u000143=Y\u000152=20260102-03:04:05.678\u0001122=20260101-00:00:00\u0001", text);
        Assert.Contains("\u000111=ORDER-1\u000155=IBM\u0001", text);
        Assert.DoesNotContain("\u000152=20260101-00:00:00\u0001", text);
        Assert.True(FixMessageTestData.HasValidChecksum(frame));
        Assert.True(FixMessageTestData.HasValidBodyLength(frame));
    }

    [Fact]
    public void TryRewriteForResend_WhenFrameAlreadyHasPossDup_KeepsASingleCopyOfEachTag()
    {
        byte[] plain = FixMessageTestData.CreateValidFrame(sequenceNumber: 5);
        byte[] first = new byte[plain.Length + PassThroughRewriter.MaxResendGrowthBytes];
        Assert.True(PassThroughRewriter.TryRewriteForResend(plain, first, 5, "20260102-03:04:05.678"u8, out int firstLength));
        byte[] second = new byte[firstLength + PassThroughRewriter.MaxResendGrowthBytes];
        Assert.True(PassThroughRewriter.TryRewriteForResend(first.AsSpan(0, firstLength), second, 5, "20260103-00:00:00.000"u8, out int secondLength));
        string text = Encoding.ASCII.GetString(second, 0, secondLength);
        Assert.Equal(1, text.Split("\u000143=Y\u0001").Length - 1);
        Assert.Equal(1, text.Split("\u0001122=").Length - 1);
        Assert.Contains("\u0001122=20260102-03:04:05.678\u0001", text);
        Assert.True(FixMessageTestData.HasValidChecksum(second.AsSpan(0, secondLength)));
        Assert.True(FixMessageTestData.HasValidBodyLength(second.AsSpan(0, secondLength)));
    }

    [Fact]
    public void TryRewriteForResend_WhenSequenceTagIsMissing_ReturnsFalse()
    {
        byte[] source = FixMessageTestData.CreateValidFrame(sequenceNumber: null);
        byte[] destination = new byte[source.Length + PassThroughRewriter.MaxResendGrowthBytes];
        bool rewritten = PassThroughRewriter.TryRewriteForResend(source, destination, 3, "20260102-03:04:05.678"u8, out int written);
        Assert.False(rewritten);
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData(12, 98)]
    [InlineData(12, 7)]
    [InlineData(12, 123)]
    [InlineData(9982, 37582)]
    [InlineData(1, 1234567890123456789)]
    [InlineData(12345, 1)]
    public void TryRewriteTo_WhenSequenceWidthDiffers_WritesExactDigitsAndValidLengths(long original, long newSeqNum)
    {
        byte[] source = FixMessageTestData.CreateValidFrame(sequenceNumber: original);
        byte[] destination = new byte[source.Length + PassThroughRewriter.MaxGrowthBytes];

        bool rewritten = PassThroughRewriter.TryRewriteTo(source, destination, newSeqNum, out int written);

        Assert.True(rewritten);
        byte[] frame = destination[..written];
        string text = Encoding.ASCII.GetString(frame);
        Assert.Contains($"\u000134={newSeqNum}\u0001", text);
        Assert.DoesNotContain("34= ", text);
        Assert.True(FixMessageTestData.HasValidChecksum(frame));
        Assert.True(FixMessageTestData.HasValidBodyLength(frame));
    }

    [Fact]
    public void TryRewriteTo_WhenBodyLengthGainsADigit_UpdatesTag9()
    {
        // Corpo de 99 bytes com seqnum de 1 dígito vira 100 com seqnum de 2 dígitos.
        byte[] source = BuildFrameWithBodyLength(99);
        byte[] destination = new byte[source.Length + PassThroughRewriter.MaxGrowthBytes];

        bool rewritten = PassThroughRewriter.TryRewriteTo(source, destination, 10, out int written);

        Assert.True(rewritten);
        byte[] frame = destination[..written];
        Assert.Contains("\u00019=100\u0001", Encoding.ASCII.GetString(frame));
        Assert.True(FixMessageTestData.HasValidBodyLength(frame));
        Assert.True(FixMessageTestData.HasValidChecksum(frame));
    }

    [Fact]
    public void TryRewriteTo_WhenSequenceHasTheSameWidth_KeepsFrameLength()
    {
        byte[] source = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        byte[] destination = new byte[source.Length];

        bool rewritten = PassThroughRewriter.TryRewriteTo(source, destination, 98, out int written);

        Assert.True(rewritten);
        Assert.Equal(source.Length, written);
    }

    [Fact]
    public void TryRewriteTo_WhenDestinationIsTooSmall_ReturnsFalse()
    {
        byte[] source = FixMessageTestData.CreateValidFrame(sequenceNumber: 1);
        byte[] destination = new byte[source.Length];

        bool rewritten = PassThroughRewriter.TryRewriteTo(source, destination, 123, out int written);

        Assert.False(rewritten);
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData("8=FIX.4.4\u00019=5\u000135=D\u000134=12\u000110=1\u0001")]
    [InlineData("8=FIX.4.4\u00019=5\u000135=D\u000134=12\u000110=123")]
    [InlineData("8=FIX.4.4\u000135=D\u000134=12\u000110=123\u0001")]
    public void TryRewriteTo_WhenFrameIsMalformed_ReturnsFalse(string malformedFrame)
    {
        byte[] source = Encoding.ASCII.GetBytes(malformedFrame);
        byte[] destination = new byte[source.Length + PassThroughRewriter.MaxGrowthBytes];

        Assert.False(PassThroughRewriter.TryRewriteTo(source, destination, 13, out int written));
        Assert.Equal(0, written);
    }

    private static byte[] BuildFrameWithBodyLength(int bodyLength)
    {
        // Corpo: "35=D|34=9|" + preenchimento em um campo 58 (Text), terminado por SOH.
        const string prefix = "35=D\u000134=9\u000158=";
        int fill = bodyLength - prefix.Length - 1;
        string body = prefix + new string('x', fill) + "\u0001";
        Assert.Equal(bodyLength, body.Length);
        string head = $"8=FIX.4.4\u00019={bodyLength}\u0001";
        int sum = 0;
        foreach (char c in head + body)
        {
            sum += c;
        }

        return Encoding.ASCII.GetBytes($"{head}{body}10={sum % 256:D3}\u0001");
    }

    [Fact]
    public void Rewrite_WhenSequenceTagIsAbsent_ReturnsFalseWithoutChangingFrame()
    {
        byte[] frame = FixMessageTestData.CreateValidFrame(sequenceNumber: null);
        byte[] original = frame.ToArray();

        bool rewritten = PassThroughRewriter.Rewrite(frame, newSeqNum: 1);

        Assert.False(rewritten);
        Assert.Equal(original, frame);
    }

    [Theory]
    [InlineData("8=FIX.4.4\u00019=5\u000135=D\u000134=12\u000110=1\u0001")]
    [InlineData("8=FIX.4.4\u00019=5\u000135=D\u000134=12\u000110=123")]
    public void Rewrite_WhenFrameIsMalformed_ReturnsFalseWithoutChangingBytes(string malformedFrame)
    {
        byte[] frame = Encoding.ASCII.GetBytes(malformedFrame);
        byte[] original = frame.ToArray();

        bool rewritten = PassThroughRewriter.Rewrite(frame, newSeqNum: 13);

        Assert.False(rewritten);
        Assert.Equal(original, frame);
    }
}
