namespace DropCopySim.Core.Generation;

/// <summary>
/// Immutable, resolved data for one order event in a FIX application message.
/// </summary>
public readonly record struct OrderContext
{
    /// <summary>Client-assigned order identifier (FIX tag 11).</summary>
    public required string ClOrdId { get; init; }

    /// <summary>Venue-assigned order identifier (FIX tag 37).</summary>
    public required string OrderId { get; init; }

    /// <summary>Execution-event identifier (FIX tag 17).</summary>
    public required string ExecId { get; init; }

    /// <summary>Execution event type (FIX tag 150).</summary>
    public char ExecType { get; init; }

    /// <summary>Current order status (FIX tag 39).</summary>
    public char OrdStatus { get; init; }

    /// <summary>Optional B3 final transaction order status (FIX tag 10029).</summary>
    public char? BtsFinalTxOrdStatus { get; init; }

    /// <summary>Optional market-maker protection reset indicator (FIX tag 9773).</summary>
    public char? MmProtectionReset { get; init; }

    /// <summary>Order side (FIX tag 54).</summary>
    public char Side { get; init; }

    /// <summary>Numeric B3 security identifier (FIX tag 48).</summary>
    public long SecurityId { get; init; }

    /// <summary>Instrument trading symbol (FIX tag 55).</summary>
    public required string Symbol { get; init; }

    /// <summary>Instrument market identifier (FIX tag 207).</summary>
    public required string SecurityExchange { get; init; }

    /// <summary>Original order quantity.</summary>
    public decimal OrderQty { get; init; }

    /// <summary>Quantity executed by this event (FIX tag 32).</summary>
    public decimal LastQty { get; init; }

    /// <summary>Quantity remaining on the order (FIX tag 151).</summary>
    public decimal LeavesQty { get; init; }

    /// <summary>Cumulative executed quantity (FIX tag 14).</summary>
    public decimal CumQty { get; init; }

    /// <summary>Price used for executions in this order lifecycle.</summary>
    public decimal Price { get; init; }

    /// <summary>Price of this event's execution (FIX tag 31).</summary>
    public decimal LastPx { get; init; }

    /// <summary>Average execution price for the order (FIX tag 6).</summary>
    public decimal AvgPx { get; init; }

    /// <summary>Clearing-firm identifier used in the Parties component.</summary>
    public required string ClearingFirm { get; init; }

    /// <summary>Client/account identifier (FIX tag 1 and PartyRole 3).</summary>
    public required string Account { get; init; }
}
