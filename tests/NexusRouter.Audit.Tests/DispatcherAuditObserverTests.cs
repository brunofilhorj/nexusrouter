using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Audit;
using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Audit.Tests;

public sealed class DispatcherAuditObserverTests
{
    [Fact]
    public async Task OnDispatchError_WritesDroppedRecordWithDispatcherId()
    {
        var channel = new AuditChannelSingleton(new AuditOptions { ChannelCapacity = 4 });
        var dispatcher = new StubDispatcher("kafka-main");
        var observer = new DispatcherAuditObserver(
            new AuditChannelWriter(channel),
            [dispatcher],
            NullLogger<DispatcherAuditObserver>.Instance);

        await observer.StartAsync(CancellationToken.None);
        dispatcher.Raise(new InvalidOperationException("broker fechado"));
        await observer.StopAsync(CancellationToken.None);
        dispatcher.Raise(new InvalidOperationException("depois do stop"));

        Assert.True(channel.Instance.Reader.TryRead(out AuditRecord? record));
        Assert.NotNull(record);
        Assert.True(record.WasDropped);
        Assert.Equal("broker fechado", record.DropReason);
        Assert.Equal("kafka-main", Assert.Single(record.DispatcherIds));
        Assert.False(channel.Instance.Reader.TryRead(out _));
    }

    private sealed class StubDispatcher : IMessageDispatcher
    {
        public StubDispatcher(string dispatcherId) => DispatcherId = dispatcherId;

        public string DispatcherId { get; }

        public bool IsHealthy => true;

        public event Action<string, Exception>? OnDispatchError;

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool TryEnqueue(DispatchPayload payload) => true;

        public void Raise(Exception exception) => OnDispatchError?.Invoke(DispatcherId, exception);
    }
}
