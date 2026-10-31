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
        var dispatcher = new ProbeDispatcher();
        byte[] first = ArrayPool<byte>.Shared.Rent(1);
        byte[] second = ArrayPool<byte>.Shared.Rent(1);
        var firstOwner = new ArrayPoolMemoryOwner<byte>(first, 1);
        var secondOwner = new ArrayPoolMemoryOwner<byte>(second, 1);

        Assert.True(dispatcher.TryEnqueue(new DispatchPayload { RawBuffer = firstOwner }));
        Assert.False(dispatcher.TryEnqueue(new DispatchPayload { RawBuffer = secondOwner }));

        await dispatcher.StopAsync(CancellationToken.None);
        Assert.Throws<ObjectDisposedException>(() => _ = firstOwner.Memory);
        _ = secondOwner.Memory;
        secondOwner.Dispose();
    }

    [Fact]
    public async Task SendAsync_WhenPublishThrows_ReportsErrorAndDisposesBuffer()
    {
        var dispatcher = new ProbeDispatcher { Fail = true };
        Exception? reported = null;
        dispatcher.OnDispatchError += (_, exception) => reported = exception;

        byte[] rented = ArrayPool<byte>.Shared.Rent(4);
        var owner = new ArrayPoolMemoryOwner<byte>(rented, 4);
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.True(dispatcher.IsHealthy);
        Assert.True(dispatcher.TryEnqueue(new DispatchPayload { RawBuffer = owner }));

        await dispatcher.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dispatcher.IsHealthy);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.False(dispatcher.IsHealthy);
        Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory);
    }

    private sealed class ProbeDispatcher : DispatcherBase<ProbeOptions>
    {
        public ProbeDispatcher()
            : base("probe", new ProbeOptions(), channelCapacity: 1, NullLogger.Instance)
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
