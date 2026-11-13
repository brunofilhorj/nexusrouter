using System.Text;
using System.Text.Json;
using ClientProbe.Probe;

namespace ClientProbe.Telemetry;

/// <summary>
/// Single scan of an ASCII FIX frame that fills the short fields (tags 35, 11, 55, 54, 38, 39)
/// and a truncated raw preview. Shared by the three adapters.
/// </summary>
public static class ShortFieldExtractor
{
    private const byte Soh = 0x01;
    private static readonly string[] FieldTags = ["11", "55", "54", "38", "39"];

    /// <summary>
    /// Broker JSON names for the short fields. The probe event still keys them by tag number.
    /// </summary>
    private static readonly (string Name, string Tag)[] JsonFields =
    [
        ("cl_ord_id", "11"),
        ("symbol", "55"),
        ("side", "54"),
        ("order_qty", "38"),
        ("ord_status", "39"),
    ];

    /// <summary>Extracts the short fields from one frame.</summary>
    /// <param name="transport">Transport that received the frame.</param>
    /// <param name="frame">ASCII bytes of one FIX frame.</param>
    /// <param name="receivedAt">Receive time.</param>
    /// <param name="previewLength">Maximum characters kept in the raw preview.</param>
    /// <returns>The event. <see cref="ProbeEvent.MsgType"/> is null when tag 35 is missing.</returns>
    public static ProbeEvent Extract(ProbeTransport transport, ReadOnlySpan<byte> frame, DateTimeOffset receivedAt, int previewLength)
    {
        if (!frame.IsEmpty && frame[0] == (byte)'{')
        {
            return ExtractJson(transport, frame, receivedAt, previewLength);
        }

        string? msgType = null;
        Dictionary<string, string>? fields = null;
        var rest = frame;
        while (!rest.IsEmpty)
        {
            var end = rest.IndexOf(Soh);
            var segment = end < 0 ? rest : rest[..end];
            rest = end < 0 ? default : rest[(end + 1)..];

            var eq = segment.IndexOf((byte)'=');
            if (eq <= 0)
            {
                continue;
            }

            var tag = segment[..eq];
            var value = segment[(eq + 1)..];
            if (tag.SequenceEqual("35"u8))
            {
                msgType ??= Encoding.ASCII.GetString(value);
                continue;
            }

            foreach (var wanted in FieldTags)
            {
                if (IsTag(tag, wanted))
                {
                    (fields ??= [])[wanted] = Encoding.ASCII.GetString(value);
                    break;
                }
            }
        }

        return new ProbeEvent(
            transport,
            receivedAt,
            msgType,
            fields ?? (IReadOnlyDictionary<string, string>)System.Collections.Frozen.FrozenDictionary<string, string>.Empty,
            Preview(frame, previewLength));
    }

    /// <summary>
    /// Reads the router JSON envelope (<c>PayloadEncoding=Json</c>):
    /// <c>{"type":"exec","msg_type":"8","ts":...,"fields":{"cl_ord_id":"..."}}</c>.
    /// Invalid JSON yields an event with a null <see cref="ProbeEvent.MsgType"/> (parse failure).
    /// </summary>
    private static ProbeEvent ExtractJson(ProbeTransport transport, ReadOnlySpan<byte> frame, DateTimeOffset receivedAt, int previewLength)
    {
        string? msgType = null;
        Dictionary<string, string>? fields = null;
        try
        {
            var reader = new Utf8JsonReader(frame);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("msg_type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    msgType = type.GetString();
                }

                if (root.TryGetProperty("fields", out var values) && values.ValueKind == JsonValueKind.Object)
                {
                    foreach (var (name, tag) in JsonFields)
                    {
                        if (values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            (fields ??= [])[tag] = value.GetString() ?? "";
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            msgType = null;
        }

        return new ProbeEvent(
            transport,
            receivedAt,
            msgType,
            fields ?? (IReadOnlyDictionary<string, string>)System.Collections.Frozen.FrozenDictionary<string, string>.Empty,
            Preview(frame, previewLength));
    }

    private static bool IsTag(ReadOnlySpan<byte> tag, string wanted)
    {
        if (tag.Length != wanted.Length)
        {
            return false;
        }

        for (var i = 0; i < tag.Length; i++)
        {
            if (tag[i] != wanted[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string Preview(ReadOnlySpan<byte> frame, int length)
    {
        var take = Math.Min(Math.Max(length, 0), frame.Length);
        return string.Create(take, frame[..take].ToArray(), static (span, bytes) =>
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                span[i] = bytes[i] switch
                {
                    Soh => '|',
                    >= 0x20 and < 0x7F => (char)bytes[i],
                    _ => '?',
                };
            }
        });
    }
}
