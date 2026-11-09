using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Opens streams, client indexes, and consumer state. The host registers one implementation.
/// </summary>
public interface ISessionLog
{
    /// <summary>
    /// Reader used by catch-up and resend.
    /// </summary>
    ISessionLogReader Reader { get; }

    /// <summary>
    /// Opens or reopens the ingress stream for a session id.
    /// </summary>
    /// <param name="sessionId">Ingress <c>SessionId</c>. Letters, digits, underscore, and hyphen only.</param>
    /// <returns>The single writer of that stream.</returns>
    ISessionLogStream OpenStream(string sessionId);

    /// <summary>
    /// Opens the index and state of a known client.
    /// </summary>
    /// <param name="dispatcherId">Dispatcher that owns the client.</param>
    /// <param name="clientKey">FIX SenderCompID or WebSocket client id.</param>
    /// <returns>The live index and the flusher state.</returns>
    ClientBinding OpenClient(string dispatcherId, string clientKey);

    /// <summary>
    /// Starts a new client epoch and sets the next outbound sequence to 1.
    /// </summary>
    /// <param name="dispatcherId">Dispatcher that owns the client.</param>
    /// <param name="clientKey">FIX SenderCompID or WebSocket client id.</param>
    /// <returns>The index of the new epoch.</returns>
    ClientBinding ResetClient(string dispatcherId, string clientKey);

    /// <summary>
    /// Opens the broker cursor file.
    /// </summary>
    /// <param name="dispatcherId">Broker dispatcher id.</param>
    /// <returns>The cursor state.</returns>
    IConsumerState OpenBroker(string dispatcherId);
}
