namespace DropCopySim.Core.Telemetry;

/// <summary>Sampled application-message summary for the UI message feed.</summary>
public sealed record MessageSample : TelemetryEvent
{
    /// <summary>FIX MsgType (e.g. <c>8</c>).</summary>
    public required string MsgType { get; init; }

    /// <summary>Client order id (ClOrdID), when present.</summary>
    public string? ClOrdId { get; init; }

    /// <summary>Exchange order id (OrderID), when present.</summary>
    public string? OrderId { get; init; }

    /// <summary>ExecType (tag 150) as a single-character string.</summary>
    public string? ExecType { get; init; }

    /// <summary>OrdStatus (tag 39) as a single-character string.</summary>
    public string? OrdStatus { get; init; }

    /// <summary>Side (tag 54) as a single-character string.</summary>
    public string? Side { get; init; }

    /// <summary>Instrument symbol for display.</summary>
    public string? Instrument { get; init; }

    /// <summary>B3 BusinessSeqNum (tag 10016), when present.</summary>
    public long? BusinessSeqNum { get; init; }

    /// <summary>Message TransactTime / sample time in UTC.</summary>
    public required DateTimeOffset TsUtc { get; init; }
}
