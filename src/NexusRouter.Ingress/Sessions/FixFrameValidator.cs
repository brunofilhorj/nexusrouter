using System.Buffers.Text;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Valida BeginString, BodyLength, CheckSum, comp IDs e SendingTime de um frame FIX completo.
/// </summary>
/// <remarks>
/// O frame vem do scanner: começa em <c>8=</c> e termina no SOH do trailer <c>10=nnn</c>.
/// Zero allocation. As verificações seguem a ordem do header: 8, 9, 10, 49, 56, 52.
/// </remarks>
internal static class FixFrameValidator
{
    private const byte Soh = 0x01;
    private const int TrailerLength = 7;

    /// <summary>
    /// Aplica as regras de sessão ao frame.
    /// </summary>
    /// <param name="frame">Frame completo, do BeginString ao SOH do checksum.</param>
    /// <param name="options">BeginString e comp IDs esperados. O counterpart troca Sender e Target.</param>
    /// <param name="utcNow">Relógio usado na checagem de SendingTime.</param>
    /// <returns>Resultado válido, ou o motivo da rejeição.</returns>
    internal static FixFrameValidation Validate(ReadOnlySpan<byte> frame, SessionOptions options, DateTimeOffset utcNow)
    {
        long refSeqNum = TryFindField(frame, 34, out ReadOnlySpan<byte> seqValue) && TryParseLong(seqValue, out long parsed) ? parsed : 0;

        if (!TryReadFirstField(frame, 8, out ReadOnlySpan<byte> beginString, out int afterBegin))
        {
            return FixFrameValidation.Reject(FixFrameValidation.RequiredTagMissing, 8, refSeqNum, "BeginString (8) ausente");
        }

        if (!AsciiEquals(beginString, options.BeginString))
        {
            return FixFrameValidation.Reject(FixFrameValidation.ValueIncorrect, 8, refSeqNum, "BeginString (8) diferente da sessao");
        }

        if (!TryReadFirstField(frame[afterBegin..], 9, out ReadOnlySpan<byte> bodyLengthValue, out int afterLength))
        {
            return FixFrameValidation.Reject(FixFrameValidation.RequiredTagMissing, 9, refSeqNum, "BodyLength (9) ausente ou fora de ordem");
        }

        if (!TryParseLong(bodyLengthValue, out long declaredLength) || declaredLength < 0)
        {
            return FixFrameValidation.Reject(FixFrameValidation.IncorrectDataFormat, 9, refSeqNum, "BodyLength (9) nao numerico");
        }

        int bodyStart = afterBegin + afterLength;
        int checksumStart = frame.Length - TrailerLength;
        if (checksumStart < bodyStart || declaredLength != checksumStart - bodyStart)
        {
            return FixFrameValidation.Reject(FixFrameValidation.ValueIncorrect, 9, refSeqNum, "BodyLength (9) nao confere com o corpo");
        }

        if (!ChecksumMatches(frame, checksumStart))
        {
            return FixFrameValidation.Reject(FixFrameValidation.ValueIncorrect, 10, refSeqNum, "CheckSum (10) nao confere");
        }

        ReadOnlySpan<byte> body = frame[bodyStart..checksumStart];
        if (!TryFindField(body, 49, out ReadOnlySpan<byte> sender))
        {
            return FixFrameValidation.Reject(FixFrameValidation.RequiredTagMissing, 49, refSeqNum, "SenderCompID (49) ausente");
        }

        if (!AsciiEquals(sender, options.TargetCompId))
        {
            return FixFrameValidation.Reject(FixFrameValidation.CompIdProblem, 49, refSeqNum, "SenderCompID (49) inesperado");
        }

        if (!TryFindField(body, 56, out ReadOnlySpan<byte> target))
        {
            return FixFrameValidation.Reject(FixFrameValidation.RequiredTagMissing, 56, refSeqNum, "TargetCompID (56) ausente");
        }

        if (!AsciiEquals(target, options.SenderCompId))
        {
            return FixFrameValidation.Reject(FixFrameValidation.CompIdProblem, 56, refSeqNum, "TargetCompID (56) inesperado");
        }

        if (!TryFindField(body, 52, out ReadOnlySpan<byte> sendingTime))
        {
            return FixFrameValidation.Reject(FixFrameValidation.RequiredTagMissing, 52, refSeqNum, "SendingTime (52) ausente");
        }

        if (!TryParseSendingTime(sendingTime, out DateTime sentUtc))
        {
            return FixFrameValidation.Reject(FixFrameValidation.IncorrectDataFormat, 52, refSeqNum, "SendingTime (52) em formato invalido");
        }

        if (options.SendingTimeTolerance > TimeSpan.Zero
            && (utcNow.UtcDateTime - sentUtc).Duration() > options.SendingTimeTolerance)
        {
            return FixFrameValidation.Reject(FixFrameValidation.SendingTimeAccuracyProblem, 52, refSeqNum, "SendingTime (52) fora da tolerancia");
        }

        return FixFrameValidation.Valid;
    }

    private static bool ChecksumMatches(ReadOnlySpan<byte> frame, int checksumStart)
    {
        int sum = 0;
        for (int i = 0; i < checksumStart; i++)
        {
            sum += frame[i];
        }

        sum &= 0xFF;
        ReadOnlySpan<byte> digits = frame.Slice(checksumStart + 3, 3);
        int declared = ((digits[0] - '0') * 100) + ((digits[1] - '0') * 10) + (digits[2] - '0');
        return declared == sum;
    }

    /// <summary>
    /// Lê o primeiro campo, que precisa ter a tag pedida. Devolve o valor e o total de bytes até depois do SOH.
    /// </summary>
    private static bool TryReadFirstField(ReadOnlySpan<byte> source, int tag, out ReadOnlySpan<byte> value, out int consumed)
    {
        value = default;
        consumed = 0;
        int equals = source.IndexOf((byte)'=');
        if (equals <= 0 || !TryParseLong(source[..equals], out long found) || found != tag)
        {
            return false;
        }

        int soh = source.IndexOf(Soh);
        if (soh < equals)
        {
            return false;
        }

        value = source[(equals + 1)..soh];
        consumed = soh + 1;
        return true;
    }

    /// <summary>
    /// Procura a primeira ocorrência da tag em qualquer posição de campo.
    /// </summary>
    private static bool TryFindField(ReadOnlySpan<byte> source, int tag, out ReadOnlySpan<byte> value)
    {
        value = default;
        int position = 0;
        while (position < source.Length)
        {
            ReadOnlySpan<byte> rest = source[position..];
            int soh = rest.IndexOf(Soh);
            ReadOnlySpan<byte> field = soh < 0 ? rest : rest[..soh];
            int equals = field.IndexOf((byte)'=');
            if (equals > 0 && TryParseLong(field[..equals], out long found) && found == tag)
            {
                value = field[(equals + 1)..];
                return value.Length > 0;
            }

            if (soh < 0)
            {
                return false;
            }

            position += soh + 1;
        }

        return false;
    }

    private static bool TryParseLong(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        return !digits.IsEmpty
            && digits.Length <= 18
            && Utf8Parser.TryParse(digits, out value, out int consumed)
            && consumed == digits.Length
            && digits[0] != (byte)'-'
            && digits[0] != (byte)'+';
    }

    private static bool AsciiEquals(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lê <c>yyyyMMdd-HH:mm:ss</c> com fração opcional de 3 ou 6 dígitos.
    /// </summary>
    private static bool TryParseSendingTime(ReadOnlySpan<byte> value, out DateTime utc)
    {
        utc = default;
        if (value.Length is not (17 or 21 or 24) || value[8] != (byte)'-' || value[11] != (byte)':' || value[14] != (byte)':')
        {
            return false;
        }

        if (!TryDigits(value, 0, 4, out int year)
            || !TryDigits(value, 4, 2, out int month)
            || !TryDigits(value, 6, 2, out int day)
            || !TryDigits(value, 9, 2, out int hour)
            || !TryDigits(value, 12, 2, out int minute)
            || !TryDigits(value, 15, 2, out int second))
        {
            return false;
        }

        long fractionTicks = 0;
        if (value.Length > 17)
        {
            int fractionDigits = value.Length - 18;
            if (value[17] != (byte)'.' || !TryDigits(value, 18, fractionDigits, out int fraction))
            {
                return false;
            }

            fractionTicks = fractionDigits == 3 ? fraction * TimeSpan.TicksPerMillisecond : fraction * 10L;
        }

        if (month is < 1 or > 12 || day < 1 || year < 1 || hour > 23 || minute > 59 || second > 59 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(fractionTicks);
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<byte> value, int start, int count, out int result)
    {
        result = 0;
        for (int i = start; i < start + count; i++)
        {
            int digit = value[i] - '0';
            if ((uint)digit > 9)
            {
                return false;
            }

            result = (result * 10) + digit;
        }

        return true;
    }
}
