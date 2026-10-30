using System.Buffers;

using NexusRouter.Domain.Models;

namespace NexusRouter.Ingress.Decoders;

/// <summary>
/// Acha o fim de uma mensagem FIX pelo trailer <c>10=</c>, sem alocar.
/// </summary>
/// <remarks>
/// O frame vai do BeginString até o SOH do checksum, inclusive.
/// O trailer reconhecido é SOH + <c>10=</c> + três dígitos + SOH.
/// <see cref="IngressFrame.SessionId"/> fica vazio: a sessão grava o identificador real.
/// </remarks>
internal static class FixFrameScanner
{
    internal enum BeginStringKind
    {
        Fix4,
        Fix50OrFixt,
    }

    private const byte Soh = 0x01;

    private static ReadOnlySpan<byte> Fix4Prefix => "8=FIX.4."u8;

    private static ReadOnlySpan<byte> Fix50Begin => "8=FIX.5.0\x01"u8;

    private static ReadOnlySpan<byte> FixtBegin => "8=FIXT.1.1\x01"u8;

    /// <summary>
    /// Tenta extrair um frame completo e avança <paramref name="buffer"/> além dos bytes consumidos.
    /// </summary>
    /// <param name="buffer">Bytes ainda não consumidos. Pode abranger vários segmentos.</param>
    /// <param name="kind">Família de BeginString aceita.</param>
    /// <param name="frame">Frame extraído, ou o valor default quando o método retorna <see langword="false"/>.</param>
    /// <returns><see langword="true"/> quando um frame completo foi separado.</returns>
    /// <remarks>Zero allocation. Seguro para chamada no hot path.</remarks>
    internal static bool TryDecode(ref ReadOnlySequence<byte> buffer, BeginStringKind kind, out IngressFrame frame)
    {
        frame = default;
        if (buffer.IsEmpty)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);
        while (reader.TryAdvanceTo((byte)'8', advancePastDelimiter: false))
        {
            long beginStart = reader.Consumed;
            if (TryMatchBegin(buffer.Slice(beginStart), kind, out int beginLength))
            {
                long searchStart = beginStart + beginLength;
                if (TryFindChecksumEnd(buffer.Slice(searchStart), out long checksumLength))
                {
                    long frameLength = beginLength + checksumLength;
                    if (frameLength > int.MaxValue)
                    {
                        buffer = buffer.Slice(beginStart + 1);
                        return false;
                    }

                    var raw = buffer.Slice(beginStart, frameLength);
                    frame = new IngressFrame(default, raw, Environment.TickCount64, (int)frameLength);
                    buffer = buffer.Slice(beginStart + frameLength);
                    return true;
                }

                buffer = buffer.Slice(beginStart);
                return false;
            }

            if (IsPartialBegin(buffer.Slice(beginStart), kind))
            {
                buffer = buffer.Slice(beginStart);
                return false;
            }

            reader.Advance(1);
        }

        buffer = buffer.Slice(buffer.End);
        return false;
    }

    private static bool TryMatchBegin(ReadOnlySequence<byte> tail, BeginStringKind kind, out int length)
    {
        length = 0;
        if (kind == BeginStringKind.Fix4)
        {
            if (tail.Length < Fix4Prefix.Length + 2)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[10];
            tail.Slice(0, header.Length).CopyTo(header);
            if (!header[..Fix4Prefix.Length].SequenceEqual(Fix4Prefix) || !IsDigit(header[8]) || header[9] != Soh)
            {
                return false;
            }

            length = header.Length;
            return true;
        }

        if (tail.Length >= Fix50Begin.Length)
        {
            Span<byte> fix50 = stackalloc byte[10];
            tail.Slice(0, fix50.Length).CopyTo(fix50);
            if (fix50.SequenceEqual(Fix50Begin))
            {
                length = fix50.Length;
                return true;
            }
        }

        if (tail.Length < FixtBegin.Length)
        {
            return false;
        }

        Span<byte> fixt = stackalloc byte[11];
        tail.Slice(0, fixt.Length).CopyTo(fixt);
        if (!fixt.SequenceEqual(FixtBegin))
        {
            return false;
        }

        length = fixt.Length;
        return true;
    }

    private static bool IsPartialBegin(ReadOnlySequence<byte> tail, BeginStringKind kind)
    {
        if (tail.IsEmpty)
        {
            return false;
        }

        if (kind == BeginStringKind.Fix4)
        {
            if (tail.Length >= 10)
            {
                return false;
            }

            Span<byte> head = stackalloc byte[9];
            int count = (int)tail.Length;
            tail.Slice(0, count).CopyTo(head);
            ReadOnlySpan<byte> sample = "8=FIX.4.0\x01"u8;
            if (count <= Fix4Prefix.Length)
            {
                return head[..count].SequenceEqual(sample[..count]);
            }

            return head[..Fix4Prefix.Length].SequenceEqual(Fix4Prefix) && IsDigit(head[8]);
        }

        if (tail.Length >= FixtBegin.Length)
        {
            return false;
        }

        if (tail.Length == Fix50Begin.Length)
        {
            Span<byte> ten = stackalloc byte[10];
            tail.Slice(0, ten.Length).CopyTo(ten);
            return ten.SequenceEqual(FixtBegin[..ten.Length]);
        }

        Span<byte> prefix = stackalloc byte[10];
        int prefixCount = (int)tail.Length;
        tail.Slice(0, prefixCount).CopyTo(prefix);
        return prefix[..prefixCount].SequenceEqual(Fix50Begin[..prefixCount])
            || prefix[..prefixCount].SequenceEqual(FixtBegin[..prefixCount]);
    }

    private static bool TryFindChecksumEnd(ReadOnlySequence<byte> tail, out long checksumLength)
    {
        checksumLength = 0;
        var reader = new SequenceReader<byte>(tail);
        Span<byte> window = stackalloc byte[8];
        while (reader.TryAdvanceTo(Soh, advancePastDelimiter: false))
        {
            if (reader.Remaining < window.Length)
            {
                return false;
            }

            var peek = reader;
            if (!peek.TryCopyTo(window))
            {
                return false;
            }

            if (window[1] == (byte)'1'
                && window[2] == (byte)'0'
                && window[3] == (byte)'='
                && IsDigit(window[4])
                && IsDigit(window[5])
                && IsDigit(window[6])
                && window[7] == Soh)
            {
                checksumLength = reader.Consumed + window.Length;
                return true;
            }

            reader.Advance(1);
        }

        return false;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}
