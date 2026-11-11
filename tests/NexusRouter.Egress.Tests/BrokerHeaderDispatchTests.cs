using System.Buffers;
using System.Text;
using System.Threading.Channels;

using Confluent.Kafka;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Domain.Secrets;
using NexusRouter.Egress;
using NexusRouter.Egress.Kafka;
using NexusRouter.Egress.RabbitMQ;

namespace NexusRouter.Egress.Tests;

public sealed class BrokerHeaderDispatchTests
{
    [Fact]
    public void ReadValues_ParsedPayload_UsesParsedFields()
    {
        var payload = new DispatchPayload
        {
            RawBuffer = new PayloadBuffer([]),
        };
        byte[] account = "PARSED"u8.ToArray();
        byte[] symbol = "IBM"u8.ToArray();
        FixField[] fields =
        [
            new FixField(1, account),
            new FixField(55, symbol),
        ];
        payload.SetParsedMessage("D"u8, fields);

        ReadOnlyMemory<byte>?[] values = BrokerHeaderFields.ReadValues(payload, [1, 55, 11]);

        Assert.Equal("PARSED", Decode(values[0]));
        Assert.Equal("IBM", Decode(values[1]));
        Assert.Null(values[2]);
    }

    [Fact]
    public async Task RabbitPublish_PresentAndMissingTags_UsesRoutingKeyAndHeaders()
    {
        var published = Channel.CreateUnbounded<RabbitResult>();
        var pool = new DispatchPayloadPool();
        var dispatcher = new RabbitMQMessageDispatcher(
            "rabbit-headers",
            new RabbitMQDispatcherOptions
            {
                Host = "localhost",
                UsernameEnvVar = "RABBIT_USER",
                PasswordEnvVar = "RABBIT_PASS",
                Exchange = "fix",
                HeaderFields = [1, 55],
                RoutingKeyTemplate = "fix.<1>.<55>",
                PayloadEncoding = BrokerPayloadEncoding.Raw,
            },
            NullLogger<RabbitMQMessageDispatcher>.Instance,
            pool,
            TestSecretResolver.Instance,
            (body, identity, routingKey, headers, _) =>
            {
                published.Writer.TryWrite(new RabbitResult(identity, routingKey, headers));
                return ValueTask.CompletedTask;
            },
            tracker: null,
            catchUp: null);
        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(dispatcher.TryEnqueue(Payload(
                pool,
                "8=FIX.4.4\u000135=D\u00011=ACCT1\u000155=IBM\u0001",
                new LogRef(1, 0, 1))));
            Assert.True(dispatcher.TryEnqueue(Payload(
                pool,
                "8=FIX.4.4\u000135=D\u000155=IBM\u0001",
                new LogRef(1, 0, 2))));

            RabbitResult present = await published.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("fix.ACCT1.IBM", present.RoutingKey);
            Assert.Equal("ACCT1", HeaderValue(present.Headers, "FIX-1"));
            Assert.Equal("IBM", HeaderValue(present.Headers, "FIX-55"));
            Assert.Equal("1:0:1", HeaderValue(present.Headers, BrokerIdentity.HeaderName));

            RabbitResult missing = await published.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("fix._missing.IBM", missing.RoutingKey);
            Assert.False(missing.Headers!.ContainsKey("FIX-1"));
            Assert.Equal("IBM", HeaderValue(missing.Headers, "FIX-55"));
            Assert.Equal("1:0:2", HeaderValue(missing.Headers, BrokerIdentity.HeaderName));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KafkaPublish_ConfiguredTags_AddsRecordHeadersAndPreservesAckCursor()
    {
        var published = Channel.CreateUnbounded<KafkaResult>();
        var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new MemoryState();
        var tracker = new BrokerCursorTracker(state, CursorAckMode.OnBrokerAck);
        var pool = new DispatchPayloadPool();
        var dispatcher = new KafkaMessageDispatcher(
            "kafka-headers",
            new KafkaDispatcherOptions
            {
                BootstrapServers = "localhost:9092",
                Topic = "fix",
                ClientId = "nexusrouter-tests",
                Acks = 1,
                HeaderFields = [1, 55],
                PayloadEncoding = BrokerPayloadEncoding.Raw,
            },
            NullLogger<KafkaMessageDispatcher>.Instance,
            pool,
            TestSecretResolver.Instance,
            (topic, partition, value, identity, headers, cancellationToken) =>
            {
                published.Writer.TryWrite(new KafkaResult(partition, identity, headers));
                return new ValueTask(releasePublish.Task.WaitAsync(cancellationToken));
            },
            tracker,
            catchUp: null,
            partitionStrategy: new FixedPartitionStrategy(2));
        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(dispatcher.TryEnqueue(Payload(
                pool,
                "8=FIX.4.4\u000135=D\u00011=ACCT1\u000155=IBM\u0001",
                new LogRef(1, 0, 1))));

            KafkaResult result = await published.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, result.Partition);
            Assert.Equal("1:0:1", result.Identity);
            Assert.Equal("ACCT1", KafkaHeaderValue(result.Headers, "FIX-1"));
            Assert.Equal("IBM", KafkaHeaderValue(result.Headers, "FIX-55"));
            Assert.Equal("1:0:1", KafkaHeaderValue(result.Headers, BrokerIdentity.HeaderName));
            Assert.Equal(0, state.Cursor(1));

            releasePublish.TrySetResult();
            await dispatcher.StopAsync(CancellationToken.None);
            Assert.Equal(1, state.Cursor(1));
        }
        finally
        {
            releasePublish.TrySetResult();
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private static DispatchPayload Payload(DispatchPayloadPool pool, string frame, LogRef logRef)
    {
        DispatchPayload payload = pool.Get();
        payload.RawBuffer = new PayloadBuffer(Encoding.ASCII.GetBytes(frame));
        payload.LogRef = logRef;
        return payload;
    }

    private static string? Decode(ReadOnlyMemory<byte>? value) =>
        value is ReadOnlyMemory<byte> bytes ? Encoding.UTF8.GetString(bytes.Span) : null;

    private static string HeaderValue(IReadOnlyDictionary<string, object>? headers, string key) =>
        Encoding.UTF8.GetString((byte[])headers![key]);

    private static string KafkaHeaderValue(Headers? headers, string key)
    {
        Assert.True(headers!.TryGetLastBytes(key, out byte[]? value));
        return Encoding.UTF8.GetString(value!);
    }

    private sealed record RabbitResult(string? Identity, string RoutingKey, Dictionary<string, object>? Headers);

    private sealed record KafkaResult(int? Partition, string? Identity, Headers? Headers);

    private sealed class PayloadBuffer(byte[] bytes) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => bytes;

        public void Dispose()
        {
        }
    }

    private sealed class TestSecretResolver : ISecretResolver
    {
        public static readonly TestSecretResolver Instance = new();

        public string ResolveRequired(string key) =>
            throw new InvalidOperationException($"Unexpected secret resolution for '{key}'.");
    }

    private sealed class FixedPartitionStrategy(int partition) : IKafkaPartitionStrategy
    {
        public int? GetPartition(DispatchPayload payload) => partition;
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
