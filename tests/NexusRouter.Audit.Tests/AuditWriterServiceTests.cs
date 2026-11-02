using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Audit;
using NexusRouter.Audit.Sinks;
using NexusRouter.Domain.Models;

namespace NexusRouter.Audit.Tests;

public sealed class AuditWriterServiceTests
{
    [Fact]
    public async Task StartAsync_WhenBatchIsFull_WritesBeforeTheFlushInterval()
    {
        var options = new AuditOptions
        {
            ChannelCapacity = 8,
            BatchSize = 2,
            FlushIntervalMs = 60_000,
        };
        var channel = new AuditChannelSingleton(options);
        var sink = new CollectingSink();
        var service = NewService(channel, options, sink);

        await service.StartAsync(CancellationToken.None);
        Assert.True(new AuditChannelWriter(channel).TryWrite(new AuditRecord { MsgType = "D" }));
        Assert.True(new AuditChannelWriter(channel).TryWrite(new AuditRecord { MsgType = "8" }));

        int count = await sink.BatchWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WhenBatchIsPartial_DrainsTheChannel()
    {
        var options = new AuditOptions
        {
            ChannelCapacity = 8,
            BatchSize = 10,
            FlushIntervalMs = 60_000,
        };
        var channel = new AuditChannelSingleton(options);
        var sink = new CollectingSink();
        var service = NewService(channel, options, sink);

        await service.StartAsync(CancellationToken.None);
        Assert.True(new AuditChannelWriter(channel).TryWrite(new AuditRecord { MsgType = "D" }));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("D", Assert.Single(sink.Records).MsgType);
    }

    [Fact]
    public async Task WriteBatchAsync_WhenOneSinkThrows_StillCallsTheNextSink()
    {
        var options = new AuditOptions
        {
            ChannelCapacity = 8,
            BatchSize = 1,
            FlushIntervalMs = 60_000,
        };
        var channel = new AuditChannelSingleton(options);
        var failing = new ThrowingSink();
        var sink = new CollectingSink();
        var service = NewService(channel, options, failing, sink);

        await service.StartAsync(CancellationToken.None);
        Assert.True(new AuditChannelWriter(channel).TryWrite(new AuditRecord { MsgType = "8" }));
        await sink.BatchWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, failing.Calls);
        Assert.Equal("8", Assert.Single(sink.Records).MsgType);
    }

    private static AuditWriterService NewService(
        AuditChannelSingleton channel,
        AuditOptions options,
        params IAuditSink[] sinks)
    {
        return new AuditWriterService(
            channel.Instance,
            sinks,
            NullLogger<AuditWriterService>.Instance,
            options);
    }

    private sealed class CollectingSink : IAuditSink
    {
        public string Name => "collect";

        public List<AuditRecord> Records { get; } = [];

        public TaskCompletionSource<int> BatchWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken cancellationToken)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                Records.Add(batch.Span[i]);
            }

            BatchWritten.TrySetResult(batch.Length);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAuditSink
    {
        public string Name => "fail";

        public int Calls { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("sink indisponível");
        }
    }
}
