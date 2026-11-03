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

    [Fact]
    public void Rewrite_WhenSequenceHasFewerDigits_PadsTheFieldAndKeepsFrameLengthsValid()
    {
        byte[] frame = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);

        bool rewritten = PassThroughRewriter.Rewrite(frame, newSeqNum: 7);

        Assert.True(rewritten);
        Assert.Contains("\u000134= 7\u0001", Encoding.ASCII.GetString(frame));
        Assert.True(FixMessageTestData.HasValidChecksum(frame));
        Assert.True(FixMessageTestData.HasValidBodyLength(frame));
    }

    [Fact]
    public void Rewrite_WhenSequenceNeedsMoreDigits_ReturnsFalseWithoutChangingFrame()
    {
        byte[] frame = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        byte[] original = frame.ToArray();

        bool rewritten = PassThroughRewriter.Rewrite(frame, newSeqNum: 123);

        Assert.False(rewritten);
        Assert.Equal(original, frame);
        Assert.True(FixMessageTestData.HasValidChecksum(frame));
        Assert.True(FixMessageTestData.HasValidBodyLength(frame));
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
