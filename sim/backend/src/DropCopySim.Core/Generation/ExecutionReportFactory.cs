using System.Buffers.Text;
using DropCopySim.Core.Fix;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Builds B3 EntryPoint FIX 4.4 ExecutionReport frames (MsgType 8).
/// </summary>
public sealed class ExecutionReportFactory : IFixMessageFactory
{
    private const string B3SecurityExchange = "BVMF";
    private const char B3SecurityIdSource = '8';
    private const char PartyIdSource = 'D';
    private const byte Soh = 0x01;

    private readonly string _senderCompId;
    private readonly string _targetCompId;
    private readonly bool _testMessageIndicator;

    /// <summary>
    /// Creates an ExecutionReport factory for the configured FIX session.
    /// </summary>
    /// <param name="senderCompId">SenderCompID for the simulated B3 side.</param>
    /// <param name="targetCompId">TargetCompID for the connected client.</param>
    /// <param name="testMessageIndicator">
    /// Whether to include <c>TestMessageIndicator (464)=Y</c> in generated reports.
    /// </param>
    /// <exception cref="ArgumentException">A CompID is empty or contains invalid FIX field data.</exception>
    public ExecutionReportFactory(
        string senderCompId,
        string targetCompId,
        bool testMessageIndicator = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderCompId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCompId);
        ValidateAsciiFieldValue(senderCompId, nameof(senderCompId));
        ValidateAsciiFieldValue(targetCompId, nameof(targetCompId));

        _senderCompId = senderCompId;
        _targetCompId = targetCompId;
        _testMessageIndicator = testMessageIndicator;
    }

    /// <summary>FIX MsgType for ExecutionReport.</summary>
    public const string MsgType = "8";

    /// <inheritdoc />
    public string Name => "ExecutionReport";

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The order context is incomplete, the ClOrdID is invalid, or the instrument is not BVMF.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A sequence number or SecurityID is negative or zero.</exception>
    /// <exception cref="InvalidOperationException">The destination is too small or a numeric field cannot be formatted.</exception>
    public int Build(Span<byte> destination, in OrderContext ctx, long seqNum, long businessSeqNum)
    {
        ValidateContext(ctx);

        DateTime utcNow = DateTime.UtcNow;
        var writer = new B3FixMessageWriter(destination);
        writer.WriteB3Header("8", _senderCompId, _targetCompId, seqNum, utcNow, businessSeqNum);

        if (ctx.MmProtectionReset is char protectionReset)
        {
            if (protectionReset is not ('Y' or 'N'))
            {
                throw new ArgumentException("MMProtectionReset must be Y or N.", nameof(ctx));
            }

            WriteCharField(ref writer, FixTags.MmProtectionReset, protectionReset);
        }

        if (_testMessageIndicator)
        {
            writer.WriteField(FixTags.TestMessageIndicator, "Y"u8);
        }

        writer.WriteField(FixTags.OrderId, ctx.OrderId);
        writer.WriteField(FixTags.ClOrdId, ctx.ClOrdId);
        writer.WriteMinimumParties(ctx.ClearingFirm, ctx.Account, PartyIdSource);
        writer.WriteField(FixTags.ExecId, ctx.ExecId);
        WriteCharField(ref writer, FixTags.ExecType, ctx.ExecType);
        WriteCharField(ref writer, FixTags.OrdStatus, ctx.OrdStatus);
        WriteCharField(
            ref writer,
            FixTags.BtsFinalTxOrdStatus,
            ctx.BtsFinalTxOrdStatus ?? ctx.OrdStatus);
        writer.WriteField(FixTags.Account, ctx.Account);

        writer.WriteField(FixTags.Symbol, ctx.Symbol);
        if (ctx.SecurityId > 0)
        {
            writer.WriteField(FixTags.SecurityId, ctx.SecurityId);
            WriteCharField(ref writer, FixTags.SecurityIdSource, B3SecurityIdSource);
        }

        writer.WriteField(FixTags.SecurityExchange, B3SecurityExchange);
        WriteCharField(ref writer, FixTags.Side, ctx.Side);
        WriteDecimalField(ref writer, FixTags.LastQty, ctx.LastQty);
        WriteDecimalField(ref writer, FixTags.LastPx, ctx.LastPx);
        WriteDecimalField(ref writer, FixTags.LeavesQty, ctx.LeavesQty);
        WriteDecimalField(ref writer, FixTags.CumQty, ctx.CumQty);
        WriteDecimalField(ref writer, FixTags.AvgPx, ctx.AvgPx);
        writer.WriteUtcTimestamp(FixTags.TransactTime, utcNow);

        return writer.Finish();
    }

    private static void ValidateContext(in OrderContext ctx)
    {
        ValidateAsciiFieldValue(ctx.OrderId, nameof(ctx.OrderId));
        ValidateClOrdId(ctx.ClOrdId);
        ValidateAsciiFieldValue(ctx.ExecId, nameof(ctx.ExecId));
        ValidateAsciiFieldValue(ctx.Symbol, nameof(ctx.Symbol));
        ValidateAsciiFieldValue(ctx.ClearingFirm, nameof(ctx.ClearingFirm));
        ValidateAsciiFieldValue(ctx.Account, nameof(ctx.Account));

        if (!string.Equals(ctx.SecurityExchange, B3SecurityExchange, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"ExecutionReport instruments must use SecurityExchange={B3SecurityExchange}.",
                nameof(ctx));
        }

        if (ctx.SecurityId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ctx), "SecurityID cannot be negative.");
        }

        if (ctx.ExecType == '\0' || ctx.OrdStatus == '\0' || ctx.Side == '\0')
        {
            throw new ArgumentException("ExecType, OrdStatus, and Side are required.", nameof(ctx));
        }
    }

    private static void ValidateClOrdId(string value)
    {
        ValidateAsciiFieldValue(value, nameof(OrderContext.ClOrdId));
        if (value.Length > 20)
        {
            throw new ArgumentException("ClOrdID cannot exceed 20 characters.", nameof(OrderContext.ClOrdId));
        }

        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                throw new ArgumentException(
                    "ClOrdID may contain only alphanumeric characters, hyphens, and underscores.",
                    nameof(OrderContext.ClOrdId));
            }
        }
    }

    private static void ValidateAsciiFieldValue(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F || value[i] == (char)Soh)
            {
                throw new ArgumentException("FIX field values must be ASCII and must not contain SOH.", paramName);
            }
        }
    }

    private static void WriteCharField(ref B3FixMessageWriter writer, int tag, char value)
    {
        if (value > 0x7F || value == '\0' || value == (char)Soh)
        {
            throw new ArgumentException($"FIX tag {tag} must be a non-empty ASCII character.", nameof(value));
        }

        Span<byte> formatted = stackalloc byte[1];
        formatted[0] = (byte)value;
        writer.WriteField(tag, formatted);
    }

    private static void WriteDecimalField(ref B3FixMessageWriter writer, int tag, decimal value)
    {
        Span<byte> formatted = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, formatted, out int written))
        {
            throw new InvalidOperationException($"Decimal field {tag} could not be formatted.");
        }

        writer.WriteField(tag, formatted[..written]);
    }
}
