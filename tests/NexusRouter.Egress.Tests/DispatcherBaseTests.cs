using System.Buffers;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Models;
using NexusRouter.Domain.Pooling;
using NexusRouter.Egress;

namespace NexusRouter.Egress.Tests;

public sealed class DispatcherBaseTests
{
    [Fact]
    public async Task TryEnqueue_WhenChannelIsFull_ReturnsFalseAndKeepsCallerOwnership()
    {
        var payloadPool = new DispatchPayloadPool();
        var dispatcher = new ProbeDispatcher(payloadPool);
        byte[] first = ArrayPool<byte>.Shared.Rent(1);
        byte[] second = ArrayPool<byte>.Shared.Rent(1);
        var firstOwner = new ArrayPoolMemoryOwner<byte>(first, 1);
        var secondOwner = new ArrayPoolMemoryOwner<byte>(second, 1);
        DispatchPayload firstPayload = payloadPool.Get();
        firstPayload.RawBuffer = firstOwner;
        DispatchPayload secondPayload = payloadPool.Get();
        secondPayload.RawBuffer = secondOwner;

        Assert.True(dispatcher.TryEnqueue(firstPayload));
        Assert.False(dispatcher.TryEnqueue(secondPayload));

        await dispatcher.StopAsync(CancellationToken.None);
        Assert.Throws<ObjectDisposedException>(() => _ = firstOwner.Memory);
        Assert.Same(firstPayload, payloadPool.Get());
        _ = secondOwner.Memory;
        secondOwner.Dispose();
        secondPayload.RawBuffer = null;
        payloadPool.Return(secondPayload);
    }

    [Fact]
    public async Task SendAsync_WhenPublishSucceeds_ReturnsPayloadToPool()
    {
        var payloadPool = new DispatchPayloadPool();
        var dispatcher = new ProbeDispatcher(payloadPool);
        DispatchPayload payload = payloadPool.Get();
        await dispatcher.StartAsync(CancellationToken.None);

        Assert.True(dispatcher.TryEnqueue(payload));

        await dispatcher.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Same(payload, payloadPool.Get());
        Assert.Null(payload.RawBuffer);
        Assert.Equal(0, payload.IngressTimestampTicks);
    }

    [Fact]
    public async Task SendAsync_WhenPublishThrows_ReportsErrorAndDisposesBuffer()
    {
        var payloadPool = new DispatchPayloadPool();
        var dispatcher = new ProbeDispatcher(payloadPool) { Fail = true };
        Exception? reported = null;
        dispatcher.OnDispatchError += (_, exception) => reported = exception;

        byte[] rented = ArrayPool<byte>.Shared.Rent(4);
        var owner = new ArrayPoolMemoryOwner<byte>(rented, 4);
        DispatchPayload payload = payloadPool.Get();
        payload.RawBuffer = owner;
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.True(dispatcher.IsHealthy);
        Assert.True(dispatcher.TryEnqueue(payload));

        await dispatcher.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dispatcher.IsHealthy);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.False(dispatcher.IsHealthy);
        Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory);
        Assert.Same(payload, payloadPool.Get());
    }

    private sealed class ProbeDispatcher : DispatcherBase<ProbeOptions>
    {
        public ProbeDispatcher(DispatchPayloadPool payloadPool)
            : base("probe", new ProbeOptions(), channelCapacity: 1, payloadPool, NullLogger.Instance)
        {
        }

        public bool Fail { get; init; }

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        protected override ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken)
        {
            Sent.TrySetResult();
            if (Fail)
            {
                throw new InvalidOperationException("falha de publicação");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeOptions;
}
