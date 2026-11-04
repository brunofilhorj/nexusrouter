namespace DropCopySim.Core.Generation;

/// <summary>
/// High-level simulation control FSM: Idle → Running → Paused.
/// </summary>
public enum SimulationState
{
    /// <summary>No active round; generator is stopped and counters may be reset.</summary>
    Idle = 0,

    /// <summary>Order-flow generator is actively emitting application messages.</summary>
    Running = 1,

    /// <summary>Generator is suspended; session may remain connected.</summary>
    Paused = 2,
}
