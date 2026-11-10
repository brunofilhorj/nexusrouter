using System.Buffers;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Egress;

namespace NexusRouter.Egress.Tests;

public sealed class StreamCatchUpTests
{
    [Fact]
    public async Task Consume_WhenChannelIsFullThenIdle_ReplaysTheMissingSequence()
    {
        var reader = new MemoryReader();
        reader.Add(1, epoch: 0, sequence: 1, [1]);
        reader.Add(1, epoch: 0, sequence: 2, [2]);
        var pool = new DispatchPayloadPool();
        var catchUp = new StreamCatchUp(reader, new MemoryState(), pool, catchUpIdleMs: 40);
        await using var dispatcher = new ProbeDispatcher(pool, catchUp, blockFirst: true);
        await dispatcher.StartAsync(CancellationToken.None);

        DispatchPayload first = Payload(pool, new LogRef(1, 0, 1));
        Assert.True(dispatcher.TryEnqueue(first));
        await dispatcher.FirstEntered.WaitAsync(TimeSpan.FromSeconds(2));

        DispatchPayload filler = Payload(pool, default);
        Assert.True(dispatcher.TryEnqueue(filler));
        DispatchPayload refused = Payload(pool, new LogRef(1, 0, 2));
        Assert.False(dispatcher.TryEnqueue(refused));
        refused.RawBuffer?.Dispose();
        refused.RawBuffer = null;
        pool.Return(refused);

        dispatcher.ReleaseFirst();
        await WaitForAsync(dispatcher, new LogRef(1, 0, 2));
        Assert.Equal(1, Count(dispatcher, new LogRef(1, 0, 2)));
    }

    [Fact]
    public async Task Accept_WhenPayloadSequenceIsBehind_DoesNotDeliverItAgain()
    {
        var reader = new MemoryReader();
        reader.Add(1, epoch: 0, sequence: 1, [1]);
        reader.Add(1, epoch: 0, sequence: 2, [2]);
        var pool = new DispatchPayloadPool();
        var catchUp = new StreamCatchUp(reader, new MemoryState(), pool, catchUpIdleMs: 1000);
        await using var dispatcher = new ProbeDispatcher(pool, catchUp, blockFirst: false);
        await dispatcher.StartAsync(CancellationToken.None);

        DispatchPayload ahead = Payload(pool, new LogRef(1, 0, 2));
        Assert.True(dispatcher.TryEnqueue(ahead));
        await WaitForAsync(dispatcher, new LogRef(1, 0, 2));

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchPayload behind = Payload(pool, new LogRef(1, 0, 1), released);
        Assert.True(dispatcher.TryEnqueue(behind));
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Count(dispatcher, new LogRef(1, 0, 1)));
        Assert.Equal(1, Count(dispatcher, new LogRef(1, 0, 2)));
    }

    [Fact]
    public async Task Accept_WhenEpochChanges_DrainsThePreviousEpochFirst()
    {
        var reader = new MemoryReader();
        reader.Add(1, epoch: 0, sequence: 1, [1]);
        reader.Add(1, epoch: 0, sequence: 2, [2]);
        reader.Add(1, epoch: 1, sequence: 1, [3]);
        var pool = new DispatchPayloadPool();
        var catchUp = new StreamCatchUp(reader, new MemoryState(), pool, catchUpIdleMs: 1000);
        await using var dispatcher = new ProbeDispatcher(pool, catchUp, blockFirst: false);
        await dispatcher.StartAsync(CancellationToken.None);

        Assert.True(dispatcher.TryEnqueue(Payload(pool, new LogRef(1, 0, 1))));
        await WaitForAsync(dispatcher, new LogRef(1, 0, 1));
        Assert.True(dispatcher.TryEnqueue(Payload(pool, new LogRef(1, 1, 1))));
        await WaitForAsync(dispatcher, new LogRef(1, 1, 1));

        Assert.Equal(
            new[] { new LogRef(1, 0, 1), new LogRef(1, 0, 2), new LogRef(1, 1, 1) },
            dispatcher.Sent.ToArray());
    }

    private static async Task WaitForAsync(ProbeDispatcher dispatcher, LogRef logRef)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (Count(dispatcher, logRef) > 0)
            {
                return;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException($"O cursor não entregou {logRef}.");
    }

    private static int Count(ProbeDispatcher dispatcher, LogRef logRef)
    {
        int count = 0;
        foreach (LogRef sent in dispatcher.Sent)
        {
            if (sent == logRef)
            {
                count++;
            }
        }

        return count;
    }

    private static DispatchPayload Payload(DispatchPayloadPool pool, LogRef logRef, TaskCompletionSource? released = null)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(1);
        DispatchPayload payload = pool.Get();
        payload.LogRef = logRef;
        payload.RawBuffer = new SignalingOwner(rented, released);
        return payload;
    }

    private sealed class ProbeDispatcher : DispatcherBase<ProbeOptions>, IAsyncDisposable
    {
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public ProbeDispatcher(DispatchPayloadPool pool, StreamCatchUp catchUp, bool blockFirst)
            : base("probe", new ProbeOptions(), channelCapacity: 1, pool, NullLogger.Instance, catchUp)
        {
            BlockFirst = blockFirst;
        }

        public bool BlockFirst { get; }

        public ConcurrentQueue<LogRef> Sent { get; } = new();

        public Task FirstEntered => _firstEntered.Task;

        public void ReleaseFirst() => _release.TrySetResult();

        public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

        protected override ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        protected override async ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
        {
            Sent.Enqueue(payload.LogRef);
            if (BlockFirst && Interlocked.Increment(ref _calls) == 1)
            {
                _firstEntered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class ProbeOptions;

    private sealed class SignalingOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _array;
        private readonly TaskCompletionSource? _released;
        private int _disposed;

        public SignalingOwner(byte[] array, TaskCompletionSource? released)
        {
            _array = array;
            _released = released;
        }

        public Memory<byte> Memory => _array.AsMemory(0, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_array);
            _released?.TrySetResult();
        }
    }

    private sealed class MemoryReader : ISessionLogReader
    {
        private readonly Dictionary<(ushort Stream, int Epoch, long Sequence), byte[]> _frames = [];
        private readonly Dictionary<(ushort Stream, int Epoch), long> _committed = [];

        public void Add(ushort stream, int epoch, long sequence, byte[] frame)
        {
            _frames[(stream, epoch, sequence)] = frame;
            long committed = GetCommitted(stream, epoch);
            if (sequence > committed)
            {
                _committed[(stream, epoch)] = sequence;
            }
        }

        public bool TryReadAt(ushort stream, uint segment, ulong offset, IBufferWriter<byte> destination) => false;

        public bool TryRead(ushort stream, int epoch, long inSeq, IBufferWriter<byte> destination)
        {
            if (inSeq <= 0 || inSeq > GetCommitted(stream, epoch))
            {
                return false;
            }

            if (!_frames.TryGetValue((stream, epoch, inSeq), out byte[]? frame))
            {
                return false;
            }

            frame.CopyTo(destination.GetSpan(frame.Length));
            destination.Advance(frame.Length);
            return true;
        }

        public long GetCommitted(ushort stream, int epoch) =>
            _committed.TryGetValue((stream, epoch), out long committed) ? committed : 0;
    }

    private sealed class MemoryState : IConsumerState
    {
        private readonly long[] _expected = new long[256];

        public long NextOut { get; } = 1;

        public long NextInExpected { get; set; } = 1;

        public long Epoch { get; set; }

        public long GetExpected(ushort stream) => _expected[stream];

        public void SetExpected(ushort stream, long inSeq) => _expected[stream] = inSeq;

        public long Cursor(ushort stream) => 0;

        public void SetCursor(ushort stream, long value)
        {
            _ = stream;
            _ = value;
        }
    }
}
