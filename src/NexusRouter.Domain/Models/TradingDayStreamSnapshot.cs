namespace NexusRouter.Domain.Models;

/// <summary>
/// Committed ingress sequence range for one stream epoch in the current trading day.
/// </summary>
/// <param name="Stream">Ingress stream id.</param>
/// <param name="Epoch">Ingress stream epoch.</param>
/// <param name="FirstSequence">First committed sequence in this range.</param>
/// <param name="Committed">Last committed sequence in this range.</param>
public readonly record struct TradingDayStreamSnapshot(
    ushort Stream,
    int Epoch,
    long FirstSequence,
    long Committed);
