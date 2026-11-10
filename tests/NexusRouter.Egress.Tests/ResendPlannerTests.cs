using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Egress.Fix;

namespace NexusRouter.Egress.Tests;

public sealed class ResendPlannerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void TryNext_WhenAdministrativeSlotsAreNeighbours_GroupsThemInOneGapFill()
    {
        var index = new FakeIndex();
        index.Admin(1);
        index.Admin(2);
        index.App(3);
        index.Admin(4);
        index.Admin(5);
        index.App(6);
        var planner = new ResendPlanner(index, 1, 6, 100, Timeout);

        List<ResendAction> actions = Drain(planner);

        Assert.Equal(
            [
                (ResendActionKind.GapFill, 1L, 3L),
                (ResendActionKind.Application, 3L, 4L),
                (ResendActionKind.GapFill, 4L, 6L),
                (ResendActionKind.Application, 6L, 7L),
            ],
            actions.Select(a => (a.Kind, a.FromSeq, a.ToSeq)).ToArray());
    }

    [Fact]
    public void TryNext_WhenEndSeqNoIsBeforeTheLastSlot_StopsAtEndSeqNo()
    {
        var index = new FakeIndex();
        index.App(1);
        index.App(2);
        index.App(3);
        var planner = new ResendPlanner(index, 1, 2, 100, Timeout);

        List<ResendAction> actions = Drain(planner);

        Assert.Equal([1L, 2L], actions.Select(a => a.FromSeq).ToArray());
    }

    [Fact]
    public void TryNext_WhenEndSeqNoIsPastNextOut_StopsAtLastNumberedSeq()
    {
        var index = new FakeIndex();
        index.App(1);
        index.App(2);
        var planner = new ResendPlanner(index, 1, 50, 100, Timeout);

        Assert.Equal(2, Drain(planner).Count);
    }

    [Fact]
    public void TryNext_WhenRequestExceedsTheLimit_ReplaysTheLimitAndClosesWithPossMissingApplMsg()
    {
        var index = new FakeIndex();
        for (int i = 1; i <= 7; i++)
        {
            index.App(i);
        }

        var planner = new ResendPlanner(index, 1, 7, 3, Timeout);
        List<ResendAction> actions = Drain(planner);

        Assert.Equal(4, actions.Count);
        Assert.All(actions.Take(3), a => Assert.Equal(ResendActionKind.Application, a.Kind));
        Assert.Equal(ResendActionKind.GapFill, actions[3].Kind);
        Assert.Equal(4, actions[3].FromSeq);
        Assert.Equal(8, actions[3].ToSeq);
        Assert.True(actions[3].PossMissingApplMsg);
    }

    [Fact]
    public void TryNext_WhenLimitIsReachedInsideAGap_ExtendsTheGapToTheEndWithPossMissingApplMsg()
    {
        var index = new FakeIndex();
        index.Admin(1);
        index.Admin(2);
        index.Admin(3);
        index.App(4);
        var planner = new ResendPlanner(index, 1, 4, 3, Timeout);

        List<ResendAction> actions = Drain(planner);

        ResendAction only = Assert.Single(actions);
        Assert.Equal(1, only.FromSeq);
        Assert.Equal(5, only.ToSeq);
        Assert.True(only.PossMissingApplMsg);
    }

    [Fact]
    public void TryNext_WhenSlotIsNotYetPublished_WaitsAndThenReplaysItOnceItIsPublished()
    {
        var index = new FakeIndex();
        index.App(1);
        index.Reserve(2);
        var planner = new ResendPlanner(index, 1, 2, 100, Timeout);

        Assert.True(planner.TryNext(out ResendAction first));
        Assert.True(planner.TryNext(out ResendAction wait));
        Assert.Equal(ResendActionKind.Application, first.Kind);
        Assert.Equal(ResendActionKind.Wait, wait.Kind);
        Assert.Equal(2, wait.FromSeq);

        index.App(2);

        Assert.True(planner.TryNext(out ResendAction second));
        Assert.Equal(ResendActionKind.Application, second.Kind);
        Assert.Equal(2, second.FromSeq);
        Assert.False(planner.TryNext(out _));
    }

    [Fact]
    public void TryNext_WhenSlotStaysUnpublishedPastTheTimeout_BecomesGap()
    {
        var index = new FakeIndex();
        index.App(1);
        index.Reserve(2);
        index.App(3);
        var clock = new ManualTime();
        var planner = new ResendPlanner(index, 1, 3, 100, Timeout, clock);

        Assert.True(planner.TryNext(out ResendAction app));
        Assert.Equal(ResendActionKind.Application, app.Kind);
        Assert.True(planner.TryNext(out ResendAction wait));
        Assert.Equal(ResendActionKind.Wait, wait.Kind);

        clock.Advance(TimeSpan.FromMilliseconds(600));

        Assert.True(planner.TryNext(out ResendAction gap));
        Assert.Equal((ResendActionKind.GapFill, 2L, 3L), (gap.Kind, gap.FromSeq, gap.ToSeq));
        Assert.True(planner.TryNext(out ResendAction last));
        Assert.Equal((ResendActionKind.Application, 3L), (last.Kind, last.FromSeq));
        Assert.False(planner.TryNext(out _));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("3", true)]
    [InlineData("4", true)]
    [InlineData("5", true)]
    [InlineData("A", true)]
    [InlineData("BD", true)]
    [InlineData("8", false)]
    [InlineData("D", false)]
    [InlineData("BY", false)]
    public void IsAdmin_ForEachMessageType_FollowsTheDictionary(string msgType, bool expected) =>
        Assert.Equal(expected, FixAdminMessageTypes.IsAdmin(System.Text.Encoding.ASCII.GetBytes(msgType)));

    private static List<ResendAction> Drain(ResendPlanner planner)
    {
        var actions = new List<ResendAction>();
        while (planner.TryNext(out ResendAction action))
        {
            actions.Add(action);
        }

        return actions;
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan span) => _ticks += (long)span.TotalMilliseconds;
    }

    private sealed class FakeIndex : IClientIndex
    {
        private readonly Dictionary<long, IndexEntry?> _slots = [];
        private long _next = 1;

        public string ClientKey => "fake";

        public long NextOut => _next;

        public void App(long seq)
        {
            _slots[seq] = new IndexEntry(1, 0, 1, 0, (ulong)seq);
            _next = Math.Max(_next, seq + 1);
        }

        public void Admin(long seq)
        {
            _slots[seq] = new IndexEntry(2, 0, 0, 0, 0);
            _next = Math.Max(_next, seq + 1);
        }

        public void Reserve(long seq)
        {
            _slots[seq] = null;
            _next = Math.Max(_next, seq + 1);
        }

        public long ReserveSeq() => _next++;

        public void PublishApp(long seq, LogRef logRef) => App(seq);

        public void PublishAdmin(long seq) => Admin(seq);

        public IndexReadResult TryGet(long seq, out IndexEntry entry)
        {
            entry = default;
            if (!_slots.TryGetValue(seq, out IndexEntry? slot))
            {
                return IndexReadResult.Missing;
            }

            if (slot is null)
            {
                return IndexReadResult.NotYet;
            }

            entry = slot.Value;
            return IndexReadResult.Published;
        }
    }
}
