namespace DropCopySim.Core.Telemetry;

/// <summary>
/// A connected telemetry consumer (typically a WebSocket) registered with the <see cref="TelemetryHub"/>.
/// </summary>
/// <remarks>
/// This abstraction keeps <c>DropCopySim.Core</c> free of an ASP.NET Core dependency: the control-tier
/// host supplies an adapter that writes the serialized frame to a real <c>WebSocket</c>. The hub calls
/// <see cref="SendAsync"/> from a single background consumer, so implementations need not be
/// safe for concurrent sends from the hub.
/// </remarks>
public interface ITelemetryClient
{
    /// <summary>Stable identity used to register and remove the client.</summary>
    string Id { get; }

    /// <summary>
    /// Sends one serialized telemetry frame (UTF-8 JSON) to the client.
    /// </summary>
    /// <param name="utf8Json">The serialized <see cref="TelemetryEvent"/> as UTF-8 JSON.</param>
    /// <param name="cancellationToken">Cancels the send when the hub is shutting down.</param>
    /// <returns>A task that completes when the frame has been handed to the transport.</returns>
    ValueTask SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken);
}
