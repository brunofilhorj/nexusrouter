using System.Collections.Immutable;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Tests;

public sealed class RouteContextTests
{
    [Fact]
    public void Dispatchers_EnumerateInConfiguredOrder()
    {
        var first = new StubDispatcher("first");
        var second = new StubDispatcher("second");
        var route = new RouteContext(
            new SessionId("session-a"),
            DeliveryMode.PassThrough,
            ImmutableArray.Create<IMessageDispatcher>(first, second));

        Assert.Equal(new IMessageDispatcher[] { first, second }, route.Dispatchers);
    }

    [Fact]
    public void Constructor_WhenDispatchersAreDefault_NormalizesToEmptyArray()
    {
        var route = new RouteContext(default, default, default);

        Assert.True(route.Dispatchers.IsEmpty);
    }

    private sealed class StubDispatcher(string dispatcherId) : IMessageDispatcher
    {
        public string DispatcherId { get; } = dispatcherId;

        public bool IsHealthy => true;

        public event Action<string, Exception>? OnDispatchError
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool TryEnqueue(DispatchPayload payload) => true;
    }
}
