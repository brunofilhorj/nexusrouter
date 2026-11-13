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
        Assert.Equal("8", root.GetProperty("msg_type").GetString());
        Assert.Equal(123, root.GetProperty("ts").GetInt64());
        JsonElement fields = root.GetProperty("fields");
        Assert.Equal("ORD1", fields.GetProperty("cl_ord_id").GetString());
        Assert.Equal("PETR4", fields.GetProperty("symbol").GetString());
        Assert.Equal("1", fields.GetProperty("side").GetString());
        Assert.False(fields.TryGetProperty("msg_type", out _));
        Assert.False(fields.TryGetProperty("begin_string", out _));
        Assert.False(fields.TryGetProperty("body_length", out _));
        Assert.False(fields.TryGetProperty("check_sum", out _));
    }

    [Fact]
    public void Write_RepeatedTagBecomesArray()
    {
        byte[] frame = Encoding.ASCII.GetBytes("35=8\u0001448=A\u0001448=B\u0001");
        var output = new ArrayBufferWriter<byte>();

        FixJsonProjector.Write(frame, 1, output);

        using JsonDocument doc = JsonDocument.Parse(output.WrittenMemory);
        JsonElement parties = doc.RootElement.GetProperty("fields").GetProperty("party_id");
        Assert.Equal(JsonValueKind.Array, parties.ValueKind);
        Assert.Equal(["A", "B"], parties.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Write_UnknownTag_UsesTagPrefix()
    {
        byte[] frame = Encoding.ASCII.GetBytes("35=8\u0001440=CLR\u0001");
        var output = new ArrayBufferWriter<byte>();

        FixJsonProjector.Write(frame, 1, output);

        using JsonDocument doc = JsonDocument.Parse(output.WrittenMemory);
        Assert.Equal("CLR", doc.RootElement.GetProperty("fields").GetProperty("tag_440").GetString());
    }

    [Fact]
    public void ToSnakeCase_Acronyms_KeepOneTokenAndSplitTrailingId()
    {
        Assert.Equal("cl_ord_id", FixFieldNames.ToSnakeCase("ClOrdID"));
        Assert.Equal("symbol", FixFieldNames.ToSnakeCase("Symbol"));
        Assert.Equal("ord_status", FixFieldNames.ToSnakeCase("OrdStatus"));
        Assert.Equal("security_id_source", FixFieldNames.ToSnakeCase("SecurityIDSource"));
        Assert.Equal("ioi_id", FixFieldNames.ToSnakeCase("IOIID"));
        Assert.Equal("no_party_ids", FixFieldNames.ToSnakeCase("NoPartyIDs"));
        Assert.Equal("order_qty2", FixFieldNames.ToSnakeCase("OrderQty2"));
        Assert.Equal("cfi_code", FixFieldNames.ToSnakeCase("CFICode"));
    }
}
