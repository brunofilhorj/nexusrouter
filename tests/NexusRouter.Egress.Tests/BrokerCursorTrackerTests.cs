using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Egress;

namespace NexusRouter.Egress.Tests;

public sealed class BrokerCursorTrackerTests
{
    [Fact]
    public void Confirm_OutOfOrder_AdvancesOnlyThroughTheContiguousWatermark()
    {
        var state = new MemoryState();
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnBrokerAck);
        var first = new LogRef(1, 0, 1);
        var second = new LogRef(1, 0, 2);

        tracker.Track(first);
        tracker.Track(second);
        Assert.Equal(1, state.GetExpected(1));

        Assert.Equal(0, tracker.Confirm(second));
        Assert.Equal(0, state.Cursor(1));
        Assert.Equal(1, state.GetExpected(1));

        Assert.Equal(2, tracker.Confirm(first));
        Assert.Equal(2, state.Cursor(1));
        Assert.Equal(3, state.GetExpected(1));
    }

    [Fact]
    public void SetExpected_OnEnqueue_AdvancesWhenTheSendReturns()
    {
        var state = new MemoryState();
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnEnqueue);

        tracker.SetExpected(1, 2);

        Assert.Equal(1, state.Cursor(1));
        Assert.Equal(2, state.GetExpected(1));
    }

    [Fact]
    public void Confirm_Duplicate_DoesNotMoveTheWatermarkBackward()
    {
        var state = new MemoryState();
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnBrokerAck);
        var first = new LogRef(1, 0, 1);
        tracker.Track(first);
        tracker.Confirm(first);

        Assert.Equal(1, tracker.Confirm(first));
        Assert.Equal(1, state.Cursor(1));
        Assert.Equal(2, state.GetExpected(1));
    }

    private sealed class MemoryState : IConsumerState
    {
        private readonly long[] _expected = new long[256];
        private readonly long[] _cursors = new long[256];

        public long NextOut { get; } = 1;

        public long NextInExpected { get; set; } = 1;

        public long Epoch { get; set; }

        public long GetExpected(ushort stream) => _expected[stream];

        public void SetExpected(ushort stream, long inSeq) => _expected[stream] = inSeq;

        public long Cursor(ushort stream) => _cursors[stream];

        public void SetCursor(ushort stream, long value) => _cursors[stream] = value;
    }
}
