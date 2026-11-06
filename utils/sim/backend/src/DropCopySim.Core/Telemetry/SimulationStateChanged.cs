using DropCopySim.Core.Generation;

namespace DropCopySim.Core.Telemetry;

/// <summary>Emitted when the simulation control FSM transitions.</summary>
public sealed record SimulationStateChanged : TelemetryEvent
{
    /// <summary>New simulation state.</summary>
    public required SimulationState State { get; init; }

    /// <summary>Optional reason (e.g. stop, limit reached, fault).</summary>
    public string? Reason { get; init; }
}
