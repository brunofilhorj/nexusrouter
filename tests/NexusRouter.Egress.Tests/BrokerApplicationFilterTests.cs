using System.Buffers;
using System.Text;
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

public sealed class BrokerApplicationFilterTests
{
    [Theory]
    [InlineData(BrokerKind.Kafka, BrokerPayloadEncoding.Json)]
    [InlineData(BrokerKind.Kafka, BrokerPayloadEncoding.Raw)]
    [InlineData(BrokerKind.Rabbit, BrokerPayloadEncoding.Json)]
    [InlineData(BrokerKind.Rabbit, BrokerPayloadEncoding.Raw)]
    public async Task AdminFrame_IsNotPublished_ExecutionReportIs_AndCursorPassesTheAdmin(
        BrokerKind broker,
        BrokerPayloadEncoding encoding)
    {
        byte[] heartbeat = FixWire.ClientMessage("0", "EXCH", msgSeqNum: 1, extra: string.Empty);
        byte[] report = FixWire.ExecutionReport("ORD1", seq: 2);
        var reader = new MemoryReader();
        reader.Add(1, epoch: 0, sequence: 1, heartbeat);
        reader.Add(1, epoch: 0, sequence: 2, report);
        var state = new MemoryState();
        var pool = new DispatchPayloadPool();
        var published = Channel.CreateUnbounded<byte[]>();

        IMessageDispatcher dispatcher = Start(broker, encoding, reader, state, pool, published);
        try
        {
            Assert.True(dispatcher.TryEnqueue(Payload(pool, heartbeat, new LogRef(1, 0, 1))));
            Assert.True(dispatcher.TryEnqueue(Payload(pool, report, new LogRef(1, 0, 2))));

            byte[] body = await published.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await dispatcher.StopAsync(CancellationToken.None);

            var bodies = new List<byte[]> { body };
            while (published.Reader.TryRead(out byte[]? extra))
            {
                bodies.Add(extra);
            }

            byte[] only = Assert.Single(bodies);
            AssertExecutionReport(encoding, only, "ORD1");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, state.Cursor(1));
        Assert.Equal(3, state.GetExpected(1));

        byte[] next = FixWire.ExecutionReport("ORD2", seq: 3);
        reader.Add(1, epoch: 0, sequence: 3, next);
        var replayed = Channel.CreateUnbounded<byte[]>();
        IMessageDispatcher restarted = Start(broker, encoding, reader, state, pool, replayed);
        try
        {
            await restarted.StopAsync(CancellationToken.None);
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
        }

        byte[] caughtUp = Assert.Single(ReadAll(replayed));
        AssertExecutionReport(encoding, caughtUp, "ORD2");
        Assert.Equal(3, state.Cursor(1));
    }

    private static List<byte[]> ReadAll(Channel<byte[]> published)
    {
        var bodies = new List<byte[]>();
        while (published.Reader.TryRead(out byte[]? body))
        {
            bodies.Add(body);
        }

        return bodies;
    }

    private static void AssertExecutionReport(BrokerPayloadEncoding encoding, byte[] body, string clOrdId)
    {
        string text = Encoding.ASCII.GetString(body);
        if (encoding == BrokerPayloadEncoding.Json)
        {
            Assert.Contains("\"msgType\":\"8\"", text, StringComparison.Ordinal);
            Assert.Contains($"\"11\":\"{clOrdId}\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"msgType\":\"0\"", text, StringComparison.Ordinal);
            return;
        }

        Assert.Contains("35=8", text, StringComparison.Ordinal);
        Assert.Contains($"11={clOrdId}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("35=0", text, StringComparison.Ordinal);
    }

    private static IMessageDispatcher Start(
        BrokerKind broker,
        BrokerPayloadEncoding encoding,
        MemoryReader reader,
        MemoryState state,
        DispatchPayloadPool pool,
        Channel<byte[]> published)
    {
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnBrokerAck);
        var catchUp = new StreamCatchUp(reader, tracker, pool, catchUpIdleMs: 60_000);
        if (broker == BrokerKind.Kafka)
        {
            var dispatcher = new KafkaMessageDispatcher(
                "kafka-app",
                new KafkaDispatcherOptions
                {
                    BootstrapServers = "localhost:19092",
                    Topic = "fix.raw",
                    ClientId = "nexusrouter",
                    Acks = 1,
                    CursorAckMode = CursorAckMode.OnBrokerAck,
                    PayloadEncoding = encoding,
                },
                NullLogger<KafkaMessageDispatcher>.Instance,
                pool,
                ThrowingSecretResolver.Instance,
                (_, _, value, _, _, _) =>
                {
                    published.Writer.TryWrite(value);
                    return ValueTask.CompletedTask;
                },
                tracker,
                catchUp);
            dispatcher.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return dispatcher;
        }

        var rabbit = new RabbitMQMessageDispatcher(
            "rabbit-app",
            new RabbitMQDispatcherOptions
            {
                Host = "127.0.0.1",
                UsernameEnvVar = "RABBIT_USER",
                PasswordEnvVar = "RABBIT_PASS",
                Exchange = "fix.raw",
                CursorAckMode = CursorAckMode.OnBrokerAck,
                PayloadEncoding = encoding,
            },
            NullLogger<RabbitMQMessageDispatcher>.Instance,
            pool,
            ThrowingSecretResolver.Instance,
            (body, _, _, _, _) =>
            {
                published.Writer.TryWrite(body.ToArray());
                return ValueTask.CompletedTask;
            },
            tracker,
            catchUp);
        rabbit.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return rabbit;
    }

    private static DispatchPayload Payload(DispatchPayloadPool pool, byte[] frame, LogRef logRef)
    {
        DispatchPayload payload = pool.Get();
        payload.SessionId = new SessionId("EXCH");
        payload.LogRef = logRef;
        payload.RawBuffer = new Bytes(frame);
        return payload;
    }

    public enum BrokerKind
    {
        Kafka,
        Rabbit,
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

        public bool TryRead(
            ushort stream,
            int epoch,
            long inSeq,
            IBufferWriter<byte> destination,
            out long ingressTimestampTicks)
        {
            ingressTimestampTicks = inSeq;
            return TryRead(stream, epoch, inSeq, destination);
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
