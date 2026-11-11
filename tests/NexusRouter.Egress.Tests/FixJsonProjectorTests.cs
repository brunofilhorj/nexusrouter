using System.Buffers;
using System.Text;
using System.Text.Json;

using NexusRouter.Egress;

namespace NexusRouter.Egress.Tests;

public sealed class FixJsonProjectorTests
{
    [Fact]
    public void Write_ProjectsEnvelopeAndFields()
    {
        byte[] frame = Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=40\u000135=8\u000111=ORD1\u000155=PETR4\u000154=1\u000110=000\u0001");
        var output = new ArrayBufferWriter<byte>();

        FixJsonProjector.Write(frame, 123, output);

        using JsonDocument doc = JsonDocument.Parse(output.WrittenMemory);
        JsonElement root = doc.RootElement;
        Assert.Equal("exec", root.GetProperty("type").GetString());
        Assert.Equal("8", root.GetProperty("msgType").GetString());
        Assert.Equal(123, root.GetProperty("ts").GetInt64());
        JsonElement fields = root.GetProperty("fields");
        Assert.Equal("ORD1", fields.GetProperty("11").GetString());
        Assert.Equal("PETR4", fields.GetProperty("55").GetString());
        Assert.False(fields.TryGetProperty("35", out _));
        Assert.False(fields.TryGetProperty("10", out _));
    }

    [Fact]
    public void Write_RepeatedTagBecomesArray()
    {
        byte[] frame = Encoding.ASCII.GetBytes("35=8\u0001448=A\u0001448=B\u0001");
        var output = new ArrayBufferWriter<byte>();

        FixJsonProjector.Write(frame, 1, output);

        using JsonDocument doc = JsonDocument.Parse(output.WrittenMemory);
        JsonElement parties = doc.RootElement.GetProperty("fields").GetProperty("448");
        Assert.Equal(JsonValueKind.Array, parties.ValueKind);
        Assert.Equal(["A", "B"], parties.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }
}
