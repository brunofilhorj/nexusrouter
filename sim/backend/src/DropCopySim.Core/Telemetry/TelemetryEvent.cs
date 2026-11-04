using System.Text.Json.Serialization;

namespace DropCopySim.Core.Telemetry;

/// <summary>
/// Discriminated telemetry event published on the control-plane WebSocket feed.
/// Serialized with a <c>type</c> discriminator for System.Text.Json polymorphism.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStateChanged), "SessionStateChanged")]
[JsonDerivedType(typeof(SimulationStateChanged), "SimulationStateChanged")]
[JsonDerivedType(typeof(CountersSnapshot), "CountersSnapshot")]
[JsonDerivedType(typeof(MessageSample), "MessageSample")]
public abstract record TelemetryEvent
{
    /// <summary>UTC timestamp when the event was produced.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }
}
