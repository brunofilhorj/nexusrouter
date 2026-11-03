using System.Text;

using NexusRouter.Domain.Models;
using NexusRouter.Router;

namespace NexusRouter.Router.Tests;

public sealed class FixTokenizerTests
{
    [Fact]
    public void Tokenize_WhenFieldBufferFits_ExtractsAllTenFields()
    {
        byte[] bytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        ReadOnlyMemory<byte> frame = bytes;
        var fields = new FixField[10];

        FixMessage message = FixTokenizer.Tokenize(frame, fields, out bool truncated);

        (int Tag, string Value)[] expected =
        [
            (8, "FIX.4.4"),
            (9, GetBodyLength(bytes)),
            (35, "D"),
            (34, "12"),
            (49, "CLIENT"),
            (56, "SERVER"),
            (52, "20260101-00:00:00"),
            (11, "ORDER-1"),
            (55, "IBM"),
            (10, GetChecksum(bytes)),
        ];

        Assert.False(truncated);
        Assert.Equal(expected.Length, message.Fields.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Tag, message.Fields[i].Tag);
            Assert.Equal(expected[i].Value, Encoding.ASCII.GetString(message.Fields[i].Value.Span));
        }

        Assert.Equal("D", Encoding.ASCII.GetString(message.MsgType));
    }

    [Fact]
    public void TryGetField_WhenTagExistsReturnsItsValue_AndMissingTagReturnsFalse()
    {
        byte[] bytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        var fields = new FixField[10];
        FixMessage message = FixTokenizer.Tokenize(bytes, fields, out _);

        Assert.True(message.TryGetField(11, out ReadOnlySpan<byte> value));
        Assert.Equal("ORDER-1", Encoding.ASCII.GetString(value));
        Assert.False(message.TryGetField(9999, out ReadOnlySpan<byte> missingValue));
        Assert.True(missingValue.IsEmpty);
    }

    [Fact]
    public void Tokenize_WhenFieldBufferFillsBeforeEnd_SetsTruncated()
    {
        byte[] bytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        var fields = new FixField[5];

        FixMessage message = FixTokenizer.Tokenize(bytes, fields, out bool truncated);

        Assert.True(truncated);
        Assert.Equal(5, message.Fields.Length);
    }

    [Fact]
    public void TokenizeAndLookup_WhenWarmedUp_DoNotAllocateOnTheCurrentThread()
    {
        byte[] bytes = FixMessageTestData.CreateValidFrame(sequenceNumber: 12);
        ReadOnlyMemory<byte> frame = bytes;
        var fields = new FixField[10];
        Span<FixField> fieldSpan = fields;

        for (int i = 0; i < 128; i++)
        {
            FixMessage warmupMessage = FixTokenizer.Tokenize(frame, fieldSpan, out _);
            _ = warmupMessage.TryGetField(11, out _);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            FixMessage message = FixTokenizer.Tokenize(frame, fieldSpan, out bool truncated);
            if (truncated || !message.TryGetField(11, out _))
            {
                throw new InvalidOperationException("O frame FIX de teste não foi tokenizado como esperado.");
            }
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }

    private static string GetBodyLength(ReadOnlySpan<byte> frame)
    {
        int tagStart = frame.IndexOf("9="u8);
        int start = tagStart + 2;
        int end = frame[start..].IndexOf((byte)'\x01');
        return Encoding.ASCII.GetString(frame.Slice(start, end));
    }

    private static string GetChecksum(ReadOnlySpan<byte> frame)
    {
        int tagStart = frame.LastIndexOf("\u000110="u8) + 1;
        int start = tagStart + 3;
        int end = frame[start..].IndexOf((byte)'\x01');
        return Encoding.ASCII.GetString(frame.Slice(start, end));
    }
}
