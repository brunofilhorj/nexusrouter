using System.Globalization;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Creates coherent FIX order-event sequences according to a configured outcome mix.
/// </summary>
public static class OrderLifecycleModel
{
    private const char NewExecType = '0';
    private const char PartialFillExecType = '1';
    private const char FillExecType = '2';
    private const char CanceledExecType = '4';
    private const char RejectedExecType = '8';

    private const char NewOrdStatus = '0';
    private const char PartiallyFilledOrdStatus = '1';
    private const char FilledOrdStatus = '2';
    private const char CanceledOrdStatus = '4';
    private const char RejectedOrdStatus = '8';

    /// <summary>
    /// Creates a New event followed by the terminal events selected by <paramref name="outcome"/>.
    /// A PartialFill outcome includes one or more partial executions followed by a full fill.
    /// </summary>
    /// <param name="order">Resolved order data used as the basis for every event.</param>
    /// <param name="outcome">Percentage weights for the lifecycle terminal outcomes.</param>
    /// <param name="partialFillsMax">Maximum number of partial executions for a PartialFill outcome.</param>
    /// <param name="random">Random source used to select the outcome and partial-fill count.</param>
    /// <returns>The ordered events for one order, including the initial New event.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> or <paramref name="random"/> is null.</exception>
    /// <exception cref="ArgumentException">The order data or outcome percentages are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partialFillsMax"/> is less than one.</exception>
    public static IReadOnlyList<OrderContext> CreateLifecycle(
        in OrderContext order,
        OutcomeMix outcome,
        int partialFillsMax,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(random);
        ValidateOrder(order);
        ValidateOutcome(outcome);
        ArgumentOutOfRangeException.ThrowIfLessThan(partialFillsMax, 1);

        var outcomeRoll = random.Next(100);
        var fillBoundary = outcome.FillPct;
        var partialFillBoundary = fillBoundary + outcome.PartialFillPct;
        var canceledBoundary = partialFillBoundary + outcome.CanceledPct;

        if (outcomeRoll < fillBoundary)
        {
            return CreateFilledLifecycle(order, partialFillCount: 0);
        }

        if (outcomeRoll < partialFillBoundary)
        {
            var partialFillCount = random.Next(1, partialFillsMax + 1);
            return CreateFilledLifecycle(order, partialFillCount);
        }

        var events = new List<OrderContext>(2)
        {
            CreateEvent(order, NewExecType, lastQty: 0, cumQty: 0, leavesQty: order.OrderQty, eventNumber: 1),
        };
        var terminalExecType = outcomeRoll < canceledBoundary ? CanceledExecType : RejectedExecType;
        events.Add(CreateEvent(order, terminalExecType, lastQty: 0, cumQty: 0, leavesQty: 0, eventNumber: 2));
        return events;
    }

    /// <summary>
    /// Derives the current order status from the execution event and remaining quantity.
    /// </summary>
    /// <param name="execType">Execution event type (FIX tag 150).</param>
    /// <param name="leavesQty">Remaining order quantity (FIX tag 151).</param>
    /// <returns>The corresponding current order status (FIX tag 39).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="leavesQty"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="execType"/> is not supported by this lifecycle model.</exception>
    public static char GetOrdStatus(char execType, decimal leavesQty)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leavesQty);

        return execType switch
        {
            CanceledExecType => CanceledOrdStatus,
            RejectedExecType => RejectedOrdStatus,
            NewExecType when leavesQty > 0 => NewOrdStatus,
            NewExecType or PartialFillExecType or FillExecType or 'F'
                when leavesQty == 0 => FilledOrdStatus,
            PartialFillExecType or FillExecType or 'F' => PartiallyFilledOrdStatus,
            _ => throw new ArgumentException($"Unsupported ExecType '{execType}'.", nameof(execType)),
        };
    }

    private static IReadOnlyList<OrderContext> CreateFilledLifecycle(in OrderContext order, int partialFillCount)
    {
        var events = new List<OrderContext>(partialFillCount + 2)
        {
            CreateEvent(order, NewExecType, lastQty: 0, cumQty: 0, leavesQty: order.OrderQty, eventNumber: 1),
        };

        if (partialFillCount == 0)
        {
            events.Add(CreateEvent(
                order,
                FillExecType,
                lastQty: order.OrderQty,
                cumQty: order.OrderQty,
                leavesQty: 0,
                eventNumber: 2));
            return events;
        }

        var partialQty = order.OrderQty / (partialFillCount + 1);
        if (partialQty <= 0)
        {
            throw new ArgumentException(
                "OrderQty is too small to represent the requested partial fills.",
                nameof(order));
        }

        decimal cumQty = 0;
        for (var i = 0; i < partialFillCount; i++)
        {
            cumQty += partialQty;
            events.Add(CreateEvent(
                order,
                PartialFillExecType,
                lastQty: partialQty,
                cumQty,
                leavesQty: order.OrderQty - cumQty,
                eventNumber: i + 2));
        }

        var finalQty = order.OrderQty - cumQty;
        events.Add(CreateEvent(
            order,
            FillExecType,
            lastQty: finalQty,
            cumQty: order.OrderQty,
            leavesQty: 0,
            eventNumber: partialFillCount + 2));
        return events;
    }

    private static OrderContext CreateEvent(
        in OrderContext order,
        char execType,
        decimal lastQty,
        decimal cumQty,
        decimal leavesQty,
        int eventNumber)
    {
        return order with
        {
            ExecId = string.Concat(order.ExecId, "-", eventNumber.ToString(CultureInfo.InvariantCulture)),
            ExecType = execType,
            OrdStatus = GetOrdStatus(execType, leavesQty),
            LastQty = lastQty,
            LeavesQty = leavesQty,
            CumQty = cumQty,
            LastPx = lastQty > 0 ? order.Price : 0,
            AvgPx = cumQty > 0 ? order.Price : 0,
        };
    }

    private static void ValidateOrder(in OrderContext order)
    {
        if (string.IsNullOrWhiteSpace(order.ClOrdId)
            || string.IsNullOrWhiteSpace(order.OrderId)
            || string.IsNullOrWhiteSpace(order.ExecId)
            || string.IsNullOrWhiteSpace(order.Symbol)
            || string.IsNullOrWhiteSpace(order.SecurityExchange)
            || string.IsNullOrWhiteSpace(order.ClearingFirm)
            || string.IsNullOrWhiteSpace(order.Account))
        {
            throw new ArgumentException("Order identifiers, instrument, clearing firm, and account are required.", nameof(order));
        }

        if (order.OrderQty <= 0)
        {
            throw new ArgumentException("OrderQty must be greater than zero.", nameof(order));
        }

        if (order.Price <= 0)
        {
            throw new ArgumentException("Price must be greater than zero.", nameof(order));
        }
    }

    private static void ValidateOutcome(OutcomeMix outcome)
    {
        if (outcome.FillPct is < 0 or > 100
            || outcome.PartialFillPct is < 0 or > 100
            || outcome.CanceledPct is < 0 or > 100
            || outcome.RejectedPct is < 0 or > 100)
        {
            throw new ArgumentException("Outcome percentages must be between 0 and 100.", nameof(outcome));
        }

        if (outcome.FillPct + outcome.PartialFillPct + outcome.CanceledPct + outcome.RejectedPct != 100)
        {
            throw new ArgumentException("Outcome percentages must sum to 100.", nameof(outcome));
        }
    }
}
