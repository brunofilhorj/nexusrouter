using System.Buffers;
using System.Globalization;
using System.Threading.Channels;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;
using NexusRouter.Egress;
using NexusRouter.Egress.Kafka;
using NexusRouter.Egress.RabbitMQ;

namespace NexusRouter.Egress.Tests;

public sealed class BrokerRedeliveryTests
{
    [Fact]
    public async Task RestartBeforeAck_KafkaRedeliversTheSameHeader() =>
        await RestartBeforeAckAsync(BrokerKind.Kafka);

    [Fact]
    public async Task RestartBeforeAck_RabbitRedeliversTheSameHeader() =>
        await RestartBeforeAckAsync(BrokerKind.Rabbit);

    private static async Task RestartBeforeAckAsync(BrokerKind broker)
    {
        var reader = new MemoryReader();
        reader.Add(1, epoch: 0, sequence: 1, [1]);
        reader.Add(1, epoch: 0, sequence: 2, [2]);
        var state = new MemoryState();
        var pool = new DispatchPayloadPool();
        var firstGate = new PublishGate();
        IMessageDispatcher first = Start(broker, reader, state, pool, firstGate, blockOnSecond: true);
        string blocked = string.Empty;
        try
        {
            Assert.True(first.TryEnqueue(Payload(pool, new LogRef(1, 0, 1))));
            Assert.Equal("1:0:1", await firstGate.ReadAsync());

            Assert.True(first.TryEnqueue(Payload(pool, new LogRef(1, 0, 2))));
            blocked = await firstGate.ReadAsync();
            Assert.Equal("1:0:2", blocked);

            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await first.StopAsync(stop.Token);
            await firstGate.BlockedFinished.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await first.StopAsync(stop.Token);
        }

        Assert.Equal(1, state.Cursor(1));
        Assert.Equal(2, state.GetExpected(1));

        var secondGate = new PublishGate();
        IMessageDispatcher second = Start(broker, reader, state, pool, secondGate, blockOnSecond: false);
        try
        {
            string replayed = await secondGate.ReadAsync();
            Assert.Equal(blocked, replayed);
            var consumer = new SessionDedup();
            Assert.True(consumer.Accept("1:0:1"));
            Assert.True(consumer.Accept(blocked));
            Assert.False(consumer.Accept(replayed));
        }
        finally
        {
            await second.StopAsync(CancellationToken.None);
        }
    }

    private static IMessageDispatcher Start(
        BrokerKind broker,
        MemoryReader reader,
        MemoryState state,
        DispatchPayloadPool pool,
        PublishGate gate,
        bool blockOnSecond)
    {
        if (broker == BrokerKind.Kafka)
        {
            return StartKafka(reader, state, pool, gate, blockOnSecond);
        }

        return StartRabbit(reader, state, pool, gate, blockOnSecond);
    }

    private static KafkaMessageDispatcher StartKafka(
        MemoryReader reader,
        MemoryState state,
        DispatchPayloadPool pool,
        PublishGate gate,
        bool blockOnSecond)
    {
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnBrokerAck);
        var dispatcher = new KafkaMessageDispatcher(
            "kafka-main",
            new KafkaDispatcherOptions
            {
                BootstrapServers = "localhost:19092",
                Topic = "fix.raw",
                ClientId = "nexusrouter",
                Acks = 1,
            },
            NullLogger<KafkaMessageDispatcher>.Instance,
            pool,
            ThrowingSecretResolver.Instance,
            (topic, partition, value, identity, _, cancellationToken) =>
                gate.PublishAsync(identity, blockOnSecond, cancellationToken),
            tracker,
            new StreamCatchUp(reader, tracker, pool, catchUpIdleMs: 1000));
        dispatcher.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return dispatcher;
    }

    private static RabbitMQMessageDispatcher StartRabbit(
        MemoryReader reader,
        MemoryState state,
        DispatchPayloadPool pool,
        PublishGate gate,
        bool blockOnSecond)
    {
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnBrokerAck);
        var dispatcher = new RabbitMQMessageDispatcher(
            "rabbit-main",
            new RabbitMQDispatcherOptions
            {
                Host = "127.0.0.1",
                UsernameEnvVar = "RABBIT_USER",
                PasswordEnvVar = "RABBIT_PASS",
                Exchange = "fix.raw",
            },
            NullLogger<RabbitMQMessageDispatcher>.Instance,
            pool,
            ThrowingSecretResolver.Instance,
            (body, identity, routingKey, headers, cancellationToken) =>
                gate.PublishAsync(identity, blockOnSecond, cancellationToken),
            tracker,
            new StreamCatchUp(reader, tracker, pool, catchUpIdleMs: 1000));
        dispatcher.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return dispatcher;
    }

    private static DispatchPayload Payload(DispatchPayloadPool pool, LogRef logRef)
    {
        DispatchPayload payload = pool.Get();
        payload.SessionId = new SessionId("EXCH");
        payload.LogRef = logRef;
        payload.RawBuffer = new Bytes([1]);
        return payload;
    }

    private enum BrokerKind
    {
        Kafka,
        Rabbit,
    }

    private sealed class PublishGate
    {
        private readonly Channel<string> _headers = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _blockedFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task BlockedFinished => _blockedFinished.Task;

        public async Task<string> ReadAsync() =>
            await _headers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public async ValueTask PublishAsync(string? identity, bool blockOnSecond, CancellationToken cancellationToken)
        {
            if (identity is not null)
            {
                _headers.Writer.TryWrite(identity);
            }

            int seen = Interlocked.Increment(ref _seen);
            if (!blockOnSecond || seen != 2)
            {
                return;
            }

            try
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _blockedFinished.TrySetResult();
            }
        }
    }

    private sealed class SessionDedup
    {
        private readonly Dictionary<(string Session, int Epoch), long> _last = [];

        public bool Accept(string header)
        {
            string[] parts = header.Split(':');
            var key = (parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture));
            long seq = long.Parse(parts[2], CultureInfo.InvariantCulture);
            if (_last.TryGetValue(key, out long seen) && seq <= seen)
            {
                return false;
            }

            _last[key] = seq;
            return true;
        }
    }

    private sealed class Bytes(byte[] data) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => data;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingSecretResolver : ISecretResolver
    {
        public static readonly ThrowingSecretResolver Instance = new();

        public string ResolveRequired(string key) =>
            throw new InvalidOperationException($"O resolvedor não deveria ser chamado (chave '{key}').");
    }

    private sealed class MemoryReader : ISessionLogReader
    {
        private readonly Dictionary<(ushort Stream, int Epoch, long Sequence), byte[]> _frames = [];
        private readonly Dictionary<(ushort Stream, int Epoch), long> _committed = [];

        public void Add(ushort stream, int epoch, long sequence, byte[] frame)
        {
            _frames[(stream, epoch, sequence)] = frame;
            if (sequence > GetCommitted(stream, epoch))
            {
                _committed[(stream, epoch)] = sequence;
            }
        }

        public bool TryReadAt(ushort stream, uint segment, ulong offset, IBufferWriter<byte> destination) => false;

        public bool TryRead(ushort stream, int epoch, long inSeq, IBufferWriter<byte> destination)
        {
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
