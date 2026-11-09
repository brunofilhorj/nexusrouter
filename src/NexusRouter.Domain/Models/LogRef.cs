namespace NexusRouter.Domain.Models;

/// <summary>
/// Points at one ingress record in the session log.
/// </summary>
/// <param name="Stream">Ingress stream id.</param>
/// <param name="Epoch">Epoch of that stream.</param>
/// <param name="InSeq">Ingress sequence number.</param>
public readonly record struct LogRef(ushort Stream, int Epoch, long InSeq);
