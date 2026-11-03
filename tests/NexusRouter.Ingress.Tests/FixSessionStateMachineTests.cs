using NexusRouter.Domain.Enums;
using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Ingress.Tests;

public sealed class FixSessionStateMachineTests
{
    [Theory]
    [InlineData(SessionState.Disconnected, SessionState.Connecting)]
    [InlineData(SessionState.Connecting, SessionState.LogonSent)]
    [InlineData(SessionState.Connecting, SessionState.Disconnected)]
    [InlineData(SessionState.LogonSent, SessionState.Active)]
    [InlineData(SessionState.LogonSent, SessionState.Disconnected)]
    [InlineData(SessionState.Active, SessionState.LogoutSent)]
    [InlineData(SessionState.Active, SessionState.Disconnected)]
    [InlineData(SessionState.LogoutSent, SessionState.Disconnected)]
    public void TransitionTo_WhenTransitionIsAllowed_UpdatesState(
        SessionState initialState,
        SessionState targetState)
    {
        var stateMachine = new FixSessionStateMachine(new ManualTimeProvider());
        MoveToState(stateMachine, initialState);

        TransitionTo(stateMachine, targetState);

        Assert.Equal(targetState, stateMachine.State);
    }

    [Theory]
    [InlineData(SessionState.Disconnected, SessionState.Disconnected)]
    [InlineData(SessionState.Disconnected, SessionState.LogonSent)]
    [InlineData(SessionState.Disconnected, SessionState.Active)]
    [InlineData(SessionState.Disconnected, SessionState.LogoutSent)]
    [InlineData(SessionState.Connecting, SessionState.Connecting)]
    [InlineData(SessionState.Connecting, SessionState.Active)]
    [InlineData(SessionState.Connecting, SessionState.LogoutSent)]
    [InlineData(SessionState.LogonSent, SessionState.Connecting)]
    [InlineData(SessionState.LogonSent, SessionState.LogonSent)]
    [InlineData(SessionState.LogonSent, SessionState.LogoutSent)]
    [InlineData(SessionState.Active, SessionState.Connecting)]
    [InlineData(SessionState.Active, SessionState.LogonSent)]
    [InlineData(SessionState.Active, SessionState.Active)]
    [InlineData(SessionState.LogoutSent, SessionState.Connecting)]
    [InlineData(SessionState.LogoutSent, SessionState.LogonSent)]
    [InlineData(SessionState.LogoutSent, SessionState.Active)]
    [InlineData(SessionState.LogoutSent, SessionState.LogoutSent)]
    public void TransitionTo_WhenTransitionIsNotAllowed_ThrowsWithActualAndAttemptedStates(
        SessionState initialState,
        SessionState targetState)
    {
        var stateMachine = new FixSessionStateMachine(new ManualTimeProvider());
        MoveToState(stateMachine, initialState);

        InvalidSessionStateException exception = Assert.Throws<InvalidSessionStateException>(
            () => TransitionTo(stateMachine, targetState));

        Assert.Equal(initialState, exception.Actual);
        Assert.Equal(targetState, exception.Attempted);
    }

    [Theory]
    [InlineData(9_999, false)]
    [InlineData(10_000, true)]
    [InlineData(10_001, true)]
    public void IsHeartbeatStale_AtHeartbeatThresholdBoundary_ReturnsExpectedResult(
        long elapsedMilliseconds,
        bool expected)
    {
        var timeProvider = new ManualTimeProvider();
        var stateMachine = new FixSessionStateMachine(timeProvider);
        timeProvider.AdvanceMilliseconds(elapsedMilliseconds);

        bool isStale = stateMachine.IsHeartbeatStale(
            missedHeartbeatThreshold: 2,
            heartbeatIntervalSeconds: 5);

        Assert.Equal(expected, isStale);
    }

    [Fact]
    public async Task TransitionToConnecting_WhenCalledConcurrently_AllowsExactlyOneTransition()
    {
        const int ConcurrentCallCount = 16;
        var stateMachine = new FixSessionStateMachine(new ManualTimeProvider());
        using var startBarrier = new Barrier(ConcurrentCallCount);
        Task<bool>[] transitions = new Task<bool>[ConcurrentCallCount];

        for (int i = 0; i < transitions.Length; i++)
        {
            transitions[i] = Task.Factory.StartNew(
                () =>
                {
                    if (!startBarrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("As chamadas concorrentes não alcançaram a barreira de início.");
                    }

                    try
                    {
                        stateMachine.TransitionToConnecting();
                        return true;
                    }
                    catch (InvalidSessionStateException)
                    {
                        return false;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        bool[] results = await Task.WhenAll(transitions).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(SessionState.Connecting, stateMachine.State);
    }

    private static void MoveToState(FixSessionStateMachine stateMachine, SessionState targetState)
    {
        if (targetState >= SessionState.Connecting)
        {
            stateMachine.TransitionToConnecting();
        }

        if (targetState >= SessionState.LogonSent)
        {
            stateMachine.TransitionToLogonSent();
        }

        if (targetState >= SessionState.Active)
        {
            stateMachine.TransitionToActive();
        }

        if (targetState >= SessionState.LogoutSent)
        {
            stateMachine.TransitionToLogoutSent();
        }
    }

    private static void TransitionTo(FixSessionStateMachine stateMachine, SessionState targetState)
    {
        switch (targetState)
        {
            case SessionState.Disconnected:
                stateMachine.TransitionToDisconnected();
                break;
            case SessionState.Connecting:
                stateMachine.TransitionToConnecting();
                break;
            case SessionState.LogonSent:
                stateMachine.TransitionToLogonSent();
                break;
            case SessionState.Active:
                stateMachine.TransitionToActive();
                break;
            case SessionState.LogoutSent:
                stateMachine.TransitionToLogoutSent();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetState), targetState, null);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void AdvanceMilliseconds(long milliseconds) =>
            Interlocked.Add(ref _timestamp, milliseconds);
    }
}
