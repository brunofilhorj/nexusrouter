using NexusRouter.Domain.Models;

namespace NexusRouter.Router;

/// <summary>
/// Quebra um frame FIX em pares tag/valor sem copiar os valores.
/// </summary>
/// <remarks>
/// Os valores de <see cref="FixField"/> apontam para <c>buffer</c>.
/// O span de campos do <see cref="FixMessage"/> aponta para <c>fieldBuffer</c>.
/// Não devolva o buffer alugado e não reuse <c>fieldBuffer</c> antes de copiar a mensagem,
/// por exemplo com <see cref="DispatchPayload.SetParsedMessage"/>.
/// </remarks>
public static class FixTokenizer
{
    private const byte Soh = 0x01;
    private const int MsgTypeTag = 35;

    /// <summary>
    /// Preenche <paramref name="fieldBuffer"/> com os campos do frame, na ordem em que aparecem.
    /// </summary>
    /// <param name="buffer">Frame FIX. Os valores retornados apontam para esta memória.</param>
    /// <param name="fieldBuffer">Destino fornecido pelo chamador, em geral <c>stackalloc</c>.</param>
    /// <param name="truncated">
    /// <see langword="true"/> quando ainda havia campo depois que <paramref name="fieldBuffer"/> encheu.
    /// </param>
    /// <returns>Mensagem com MsgType (tag 35), quando presente, e os campos gravados em <paramref name="fieldBuffer"/>.</returns>
    /// <remarks>
    /// Zero allocation. Seguro para chamada no hot path.
    /// A análise para no primeiro campo malformado e também quando o buffer de campos enche.
    /// </remarks>
    public static FixMessage Tokenize(ReadOnlyMemory<byte> buffer, Span<FixField> fieldBuffer, out bool truncated)
    {
        ReadOnlySpan<byte> bytes = buffer.Span;
        int index = 0;
        int count = 0;
        int msgTypeStart = -1;
        int msgTypeLength = 0;
        bool capacityReached = false;

        while (index < bytes.Length)
        {
            if (bytes[index] == Soh)
            {
                index++;
                continue;
            }

            if (count == fieldBuffer.Length)
            {
                capacityReached = true;
                break;
            }

            if (!TryReadTag(bytes, ref index, out int tag))
            {
                break;
            }

            int valueStart = index;
            int separator = bytes[index..].IndexOf(Soh);
            int valueLength;
            if (separator < 0)
            {
                valueLength = bytes.Length - valueStart;
                index = bytes.Length;
            }
            else
            {
                valueLength = separator;
                index = valueStart + valueLength + 1;
            }

            fieldBuffer[count] = new FixField(tag, buffer.Slice(valueStart, valueLength));
            count++;
            if (tag == MsgTypeTag && msgTypeStart < 0)
            {
                msgTypeStart = valueStart;
                msgTypeLength = valueLength;
            }
        }

        truncated = capacityReached && index < bytes.Length;
        ReadOnlySpan<byte> msgType = msgTypeStart < 0
            ? ReadOnlySpan<byte>.Empty
            : bytes.Slice(msgTypeStart, msgTypeLength);
        return new FixMessage(msgType, fieldBuffer[..count]);
    }

    private static bool TryReadTag(ReadOnlySpan<byte> bytes, ref int index, out int tag)
    {
        int start = index;
        tag = 0;
        while (index < bytes.Length)
        {
            byte digit = bytes[index];
            if (digit < (byte)'0' || digit > (byte)'9')
            {
                break;
            }

            int numeric = digit - (byte)'0';
            if (tag > (int.MaxValue - numeric) / 10)
            {
                tag = 0;
                return false;
            }

            tag = (tag * 10) + numeric;
            index++;
        }

        if (index == start || index >= bytes.Length || bytes[index] != (byte)'=')
        {
            tag = 0;
            return false;
        }

        index++;
        return true;
    }
}
