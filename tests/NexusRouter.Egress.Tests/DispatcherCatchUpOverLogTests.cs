using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.SessionLog;

namespace NexusRouter.Egress.Tests;

/// <summary>
/// T-48 (passos 5 e 8): o cursor alcança o log real depois de o dispatcher ficar offline ou lento, sem buraco e sem descarte.
/// </summary>
public sealed class DispatcherCatchUpOverLogTests
{
    [Fact]
    public async Task Broker_AfterDayRollover_DrainsPreviousEpochBeforeDeliveringNewDay()
    {
        string directory = FixWire.TempDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 12, 2, 59, 30, TimeSpan.Zero));
        await using SessionLogManager log = await StartLogAsync(directory, clock);
        try
        {
            ISessionLogStream stream = log.OpenStream("B3");
            IConsumerState state = log.OpenBroker("kafka-main");
            var tracker = new BrokerCursorTracker(state, CursorAckMode.OnEnqueue);
            var pool = new DispatchPayloadPool();
            LogRef[] previousDay = Append(stream, 2);
            var broker = new ProbeDispatcher(pool, new StreamCatchUp(log.Reader, tracker, pool, catchUpIdleMs: 40), capacity: 8);
            await broker.StartAsync(CancellationToken.None);
            broker.Enqueue(pool, previousDay[0], FixWire.ExecutionReport("M1", 1));
            await broker.WaitForAsync(1);

            var rollover = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            log.DayRolloverCompleted += () => rollover.TrySetResult();
            clock.Advance(TimeSpan.FromMinutes(2));
            await rollover.Task.WaitAsync(TimeSpan.FromSeconds(3));

            LogRef currentDay = Append(stream, 1)[0];
            Assert.Equal(1, currentDay.Epoch);
            broker.Enqueue(pool, currentDay, FixWire.ExecutionReport("M1-NEXT-DAY", 1));
            await broker.WaitForAsync(3);

            Assert.Equal([previousDay[0], previousDay[1], currentDay], broker.SentRefs());
            Assert.Equal(1, state.Epoch);
            Assert.Equal(2, state.GetExpected(currentDay.Stream));
            await broker.StopAsync(CancellationToken.None);
        }
        finally
        {
            await log.StopAsync(CancellationToken.None);
            FixWire.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Dispatcher_WhenOfflineForPartOfTheDay_CatchesUpFromThePersistedCursorWithoutHole()
    {
        string directory = FixWire.TempDirectory();
        await using SessionLogManager log = await StartLogAsync(directory);
        try
        {
            ISessionLogStream stream = log.OpenStream("B3");
            IConsumerState state = log.OpenBroker("kafka-main");
            var pool = new DispatchPayloadPool();
            LogRef[] refs = Append(stream, 6);

            var online = new ProbeDispatcher(pool, new StreamCatchUp(log.Reader, state, pool, catchUpIdleMs: 40), capacity: 8);
            await online.StartAsync(CancellationToken.None);
            online.Enqueue(pool, refs[0], FixWire.ExecutionReport("M1", 1));
            online.Enqueue(pool, refs[1], FixWire.ExecutionReport("M2", 2));
            await online.WaitForAsync(2);
            await online.StopAsync(CancellationToken.None);
            Assert.Equal([1L, 2L], online.SentSequences());
            Assert.Equal(3, state.GetExpected(refs[0].Stream));

            // O dispatcher fica fora enquanto 3..5 entram no log. Ao voltar, um cursor novo parte do estado persistido.
            var back = new ProbeDispatcher(pool, new StreamCatchUp(log.Reader, state, pool, catchUpIdleMs: 40), capacity: 8);
            await back.StartAsync(CancellationToken.None);
            back.Enqueue(pool, refs[5], FixWire.ExecutionReport("M6", 6));
            await back.WaitForAsync(4);
            Assert.Equal([3L, 4L, 5L, 6L], back.SentSequences());

            // 7 e 8 entram no log depois e nunca chegam pelo channel. O timer de ociosidade as entrega.
            Append(stream, 2, firstSeq: 7);
            await back.WaitForAsync(6);
            Assert.Equal([3L, 4L, 5L, 6L, 7L, 8L], back.SentSequences());
            Assert.Equal(9, state.GetExpected(refs[0].Stream));
            await back.StopAsync(CancellationToken.None);
        }
        finally
        {
            await log.StopAsync(CancellationToken.None);
            FixWire.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Dispatcher_WhenConsumerIsSlow_DoesNotBlockTheProducerAndRecoversEveryMessageFromTheLog()
    {
        const int total = 60;
        string directory = FixWire.TempDirectory();
        await using SessionLogManager log = await StartLogAsync(directory);
        try
        {
            ISessionLogStream stream = log.OpenStream("B3");
            IConsumerState state = log.OpenBroker("kafka-main");
            var pool = new DispatchPayloadPool();
            LogRef[] refs = Append(stream, total);
            var catchUp = new StreamCatchUp(log.Reader, state, pool, catchUpIdleMs: 40);
            var dispatcher = new ProbeDispatcher(pool, catchUp, capacity: 1, blockFirstSend: true);
            await dispatcher.StartAsync(CancellationToken.None);

            // O consumo trava no primeiro envio. O produtor segue: o que o channel recusa fica só no log.
            int refused = 0;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < total; i++)
            {
                if (!dispatcher.TryEnqueueOrRelease(pool, refs[i], FixWire.ExecutionReport("M" + (i + 1), i + 1)))
                {
                    refused++;
                }

                if (i == 0)
                {
                    await dispatcher.FirstEntered.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }

            watch.Stop();
            Assert.True(refused > 0, "O channel de capacidade 1 deveria ter recusado alguma mensagem.");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"O produtor esperou o consumidor lento: {watch.ElapsedMilliseconds} ms.");
            Assert.True(catchUp.Lag > 0, "A mensagem recusada deveria aparecer como lag.");

            dispatcher.ReleaseFirstSend();
            await dispatcher.WaitForAsync(total);

            Assert.Equal(Enumerable.Range(1, total).Select(static i => (long)i), dispatcher.SentSequences());
            Assert.Equal(0, catchUp.Lag);
            await dispatcher.StopAsync(CancellationToken.None);
        }
        finally
        {
            await log.StopAsync(CancellationToken.None);
            FixWire.DeleteDirectory(directory);
        }
    }

    private static async Task<SessionLogManager> StartLogAsync(string directory, TimeProvider? timeProvider = null)
    {
        var log = new SessionLogManager(
            new SessionLogOptions
            {
                Directory = directory,
                SegmentSizeMB = 1,
                IndexSlotsPerSegment = 100_000,
                FlushIntervalMs = 60_000,
                RetentionTradingDays = 5,
                CatchUpIdleMs = 20,
            },
            NullLogger<SessionLogManager>.Instance,
            timeProvider);
        await log.StartAsync(CancellationToken.None);
        return log;
    }

    private static LogRef[] Append(ISessionLogStream stream, int count, int firstSeq = 1)
    {
        var refs = new LogRef[count];
        for (int i = 0; i < count; i++)
        {
            AppendResult appended = stream.Append(FixWire.ExecutionReport("M" + (firstSeq + i), firstSeq + i), inSeq: firstSeq + i, ingressTs: 1);
            Assert.True(appended.IsOk);
            refs[i] = appended.Ref;
        }

        return refs;
    }

    private sealed class ProbeDispatcher : DispatcherBase<ProbeOptions>
    {
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstSend;
        private readonly ConcurrentQueue<LogRef> _sent = new();
        private int _calls;

        public ProbeDispatcher(DispatchPayloadPool pool, StreamCatchUp catchUp, int capacity, bool blockFirstSend = false)
            : base("probe", new ProbeOptions(), capacity, pool, NullLogger.Instance, catchUp)
        {
            _blockFirstSend = blockFirstSend;
        }

        public Task FirstEntered => _firstEntered.Task;

        public void ReleaseFirstSend() => _release.TrySetResult();

        public long[] SentSequences() => _sent.Select(static logRef => logRef.InSeq).ToArray();

        public LogRef[] SentRefs() => [.. _sent];

        public void Enqueue(DispatchPayloadPool pool, LogRef logRef, byte[] frame) =>
            Assert.True(TryEnqueueOrRelease(pool, logRef, frame));

        public bool TryEnqueueOrRelease(DispatchPayloadPool pool, LogRef logRef, byte[] frame)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(frame.Length);
            frame.CopyTo(rented, 0);
            DispatchPayload payload = pool.Get();
            payload.LogRef = logRef;
            payload.RawBuffer = new ArrayPoolMemoryOwner<byte>(rented, frame.Length);
            if (TryEnqueue(payload))
            {
                return true;
            }

            payload.RawBuffer.Dispose();
            payload.RawBuffer = null;
            pool.Return(payload);
            return false;
        }

        public async Task WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (_sent.Count >= count)
                {
                    return;
                }

                await Task.Delay(15);
            }

            throw new TimeoutException($"Chegaram {_sent.Count} de {count} mensagens: [{string.Join(',', _sent)}].");
        }

        protected override ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        protected override async ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
        {
            _sent.Enqueue(payload.LogRef);
            if (_blockFirstSend && Interlocked.Increment(ref _calls) == 1)
            {
                _firstEntered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class ProbeOptions;

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _utcTicks;

        public AdjustableTimeProvider(DateTimeOffset utcNow) => _utcTicks = utcNow.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }
}
