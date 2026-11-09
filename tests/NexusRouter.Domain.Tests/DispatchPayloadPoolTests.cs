using System.Buffers;

using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;

namespace NexusRouter.Domain.Tests;

public sealed class DispatchPayloadPoolTests
{
    [Fact]
    public void Return_ResetsAllPayloadFieldsWithoutDisposingRawBuffer()
    {
        var pool = new DispatchPayloadPool();
        var rawBuffer = new CountingMemoryOwner();
        DispatchPayload payload = pool.Get();
        payload.SessionId = new SessionId("session-a");
        payload.DeliveryMode = DeliveryMode.Parse;
        payload.RawBuffer = rawBuffer;
        payload.IngressTimestampTicks = 10;
        payload.RoutingSequence = 20;
        payload.EgressTimestampTicks = 30;
        payload.LogRef = new LogRef(1, 2, 3);
        byte[] msgType = "D"u8.ToArray();
        var fields = new[] { new FixField(11, "client-order"u8.ToArray()) };
        payload.SetParsedMessage(msgType, fields);

        pool.Return(payload);

        Assert.Equal(default, payload.SessionId);
        Assert.Equal(default, payload.DeliveryMode);
        Assert.Null(payload.RawBuffer);
        Assert.Equal(0, payload.IngressTimestampTicks);
        Assert.Equal(0, payload.RoutingSequence);
        Assert.Equal(0, payload.EgressTimestampTicks);
        Assert.Equal(default, payload.LogRef);
        Assert.False(payload.HasParsedMessage);
        Assert.False(payload.TryGetParsedMessage(out _));
        Assert.Equal(0, rawBuffer.DisposeCount);
    }

    [Fact]
    public void Prewarm_CreatesReusablePayloadsUpToMaximumRetained()
    {
        var pool = new DispatchPayloadPool(maximumRetained: 2);

        pool.Prewarm(2);
        DispatchPayload first = pool.Get();
        DispatchPayload second = pool.Get();

        Assert.NotSame(first, second);
        Assert.Equal(2, pool.MaximumRetained);

        pool.Return(first);
        pool.Return(second);
        DispatchPayload reusedFirst = pool.Get();
        DispatchPayload reusedSecond = pool.Get();

        Assert.Contains(reusedFirst, new[] { first, second });
        Assert.Contains(reusedSecond, new[] { first, second });
        Assert.NotSame(reusedFirst, reusedSecond);
    }

    [Fact]
    public void Return_WhenRetentionLimitIsReached_DoesNotRetainExtraPayloads()
    {
        var pool = new DispatchPayloadPool(maximumRetained: 1);
        DispatchPayload first = pool.Get();
        DispatchPayload second = pool.Get();

        pool.Return(first);
        pool.Return(second);
        DispatchPayload retained = pool.Get();
        DispatchPayload notRetained = pool.Get();

        Assert.NotSame(retained, notRetained);
        Assert.Contains(retained, new[] { first, second });
        Assert.DoesNotContain(notRetained, new[] { first, second });
    }

    private sealed class CountingMemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _buffer = new byte[1];

        public int DisposeCount { get; private set; }

        public Memory<byte> Memory => _buffer;

        public void Dispose() => DisposeCount++;
    }
}
