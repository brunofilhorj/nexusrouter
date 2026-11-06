using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Generation;

namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Maps the read-only control-plane state endpoint (<c>GET /v1/state</c>, FR-API-01).
/// </summary>
public static class StateEndpoints
{
    /// <summary>
    /// Registers <c>GET /v1/state</c>, which returns the consolidated FIX session, simulation and
    /// configuration snapshot as JSON.
    /// </summary>
    /// <param name="routes">The route builder to register the endpoint on.</param>
    /// <returns>The same <paramref name="routes"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is null.</exception>
    public static IEndpointRouteBuilder MapStateEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/v1/state", (SimulationController controller, FixAcceptorService acceptor) =>
            Results.Ok(BuildState(controller, acceptor)));

        return routes;
    }

    /// <summary>
    /// Assembles the <see cref="StateResponse"/> from the simulation controller and FIX acceptor.
    /// </summary>
    /// <param name="controller">The simulation controller owning the FSM, counters and config.</param>
    /// <param name="acceptor">The FIX acceptor service exposing the active session, if any.</param>
    /// <returns>A consolidated state snapshot.</returns>
    internal static StateResponse BuildState(SimulationController controller, FixAcceptorService acceptor)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(acceptor);

        var counters = controller.Counters;

        return new StateResponse
        {
            Session = BuildSessionView(acceptor.ActiveSession),
            Simulation = new SimulationStateView
            {
                State = controller.State,
                TotalSent = counters.TotalSent,
                Errors = counters.Errors,
                ByExecType = counters.SnapshotByExecType(),
                ElapsedMs = counters.ElapsedMs(DateTimeOffset.UtcNow),
            },
            Config = controller.Config,
        };
    }

    private static SessionStateView? BuildSessionView(AcceptorSession? session)
    {
        if (session is null)
        {
            return null;
        }

        return new SessionStateView
        {
            State = session.Fsm.State.ToString(),
            SenderCompId = session.Options.SenderCompId,
            TargetCompId = session.Options.TargetCompId,
            NextOutboundMsgSeqNum = session.NextOutboundMsgSeqNum,
            NextExpectedInboundMsgSeqNum = session.NextExpectedInboundMsgSeqNum,
            LastHeartbeatUtc = session.LastActivityUtc,
        };
    }
}
