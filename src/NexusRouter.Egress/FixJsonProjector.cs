using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NexusRouter.Egress;

/// <summary>
/// Projeta um frame FIX bruto em JSON para os dispatchers de broker.
/// </summary>
/// <remarks>
/// Roda no consume loop do dispatcher, nunca no hot path. Formato:
/// <c>{"type":"exec","msg_type":"8","ts":638...,"fields":{"cl_ord_id":"ORD1","symbol":"PETR4"}}</c>.
/// A chave do campo é o nome FIX em snake_case, lido uma vez de <c>docs/FIX44Entrypoint.xml</c>. Tag ausente no
/// dicionário vira <c>tag_&lt;número&gt;</c>. Valores continuam a string ASCII do FIX. As tags 8, 9, 10
/// e 35 não entram em <c>fields</c> (a 35 vira <c>msg_type</c>). Tag repetida (grupo repetitivo) vira
/// array de strings, na ordem do wire.
/// </remarks>
public static class FixJsonProjector
{
    /// <summary>
    /// Escreve o envelope JSON do frame.
    /// </summary>
    /// <param name="frame">Frame FIX completo, com SOH como separador.</param>
    /// <param name="timestampTicks">Instante do envio, em ticks de <see cref="DateTime"/> UTC.</param>
    /// <param name="output">Destino do JSON UTF-8.</param>
    public static void Write(ReadOnlySpan<byte> frame, long timestampTicks, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string msgType = string.Empty;
        var order = new List<int>();
        var values = new Dictionary<int, List<string>>();

        ReadOnlySpan<byte> rest = frame;
        while (!rest.IsEmpty)
        {
            int end = rest.IndexOf((byte)0x01);
            ReadOnlySpan<byte> segment = end < 0 ? rest : rest[..end];
            rest = end < 0 ? default : rest[(end + 1)..];

            int eq = segment.IndexOf((byte)'=');
            if (eq <= 0 || !TryParseTag(segment[..eq], out int tag))
            {
                continue;
            }

            string value = Encoding.Latin1.GetString(segment[(eq + 1)..]);
            if (tag == 35)
            {
                msgType = value;
                continue;
            }

            if (tag is 8 or 9 or 10)
            {
                continue;
            }

            if (!values.TryGetValue(tag, out List<string>? list))
            {
                list = [];
                values[tag] = list;
                order.Add(tag);
            }

            list.Add(value);
        }

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("type", "exec");
        writer.WriteString("msg_type", msgType);
        writer.WriteNumber("ts", timestampTicks);
        writer.WriteStartObject("fields");
        foreach (int tag in order)
        {
            List<string> list = values[tag];
            string key = FixFieldNames.Name(tag);
            if (list.Count == 1)
            {
                writer.WriteString(key, list[0]);
                continue;
            }

            writer.WriteStartArray(key);
            foreach (string item in list)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static bool TryParseTag(ReadOnlySpan<byte> text, out int tag)
    {
        tag = 0;
        foreach (byte item in text)
        {
            if (item is < (byte)'0' or > (byte)'9' || tag > 100_000_000)
            {
                return false;
            }

            tag = (tag * 10) + (item - (byte)'0');
        }

        return true;
    }
}
