using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.Tests;

public sealed class ClientRegistryTests
{
    private static readonly Guid First = Guid.Parse("6f1e0b3a-9c2d-4a7e-8b15-2d4c0a9e7f31");
    private static readonly Guid Second = Guid.Parse("c1d2e3f4-a5b6-4789-8abc-def012345678");

    [Fact]
    public void TryGet_RegisteredClient_ReturnsTokenVariableName()
    {
        var registry = new ClientRegistry([new RegisteredClient(First, "ws-1", "NEXUS_WS_TOKEN")]);

        bool found = registry.TryGet("ws-1", First, out RegisteredClient? client);

        Assert.True(found);
        Assert.Equal("NEXUS_WS_TOKEN", client!.TokenEnvVar);
        Assert.Equal(ClientKey.ForWebSocket(First), client.Key);
    }

    [Fact]
    public void TryGet_ClientOfAnotherDispatcher_ReturnsFalse()
    {
        var registry = new ClientRegistry([new RegisteredClient(First, "ws-1", "NEXUS_WS_TOKEN")]);

        Assert.False(registry.TryGet("ws-2", First, out RegisteredClient? client));
        Assert.Null(client);
    }

    [Fact]
    public void TryGet_UnknownUuid_ReturnsFalse()
    {
        var registry = new ClientRegistry([new RegisteredClient(First, "ws-1", "NEXUS_WS_TOKEN")]);

        Assert.False(registry.TryGet("ws-1", Second, out _));
    }

    [Fact]
    public void TryGet_NullDispatcher_ReturnsFalse()
    {
        var registry = new ClientRegistry([]);

        Assert.False(registry.TryGet(null!, First, out _));
    }

    [Fact]
    public void Constructor_SameClientTwiceInOneDispatcher_Throws()
    {
        RegisteredClient[] clients =
        [
            new RegisteredClient(First, "ws-1", "A"),
            new RegisteredClient(First, "ws-1", "B"),
        ];

        Assert.Throws<ArgumentException>(() => new ClientRegistry(clients));
    }

    [Fact]
    public void Constructor_SameClientInTwoDispatchers_Registers()
    {
        var registry = new ClientRegistry(
        [
            new RegisteredClient(First, "ws-1", "A"),
            new RegisteredClient(First, "ws-2", "B"),
        ]);

        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Constructor_EmptyUuid_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ClientRegistry([new RegisteredClient(Guid.Empty, "ws-1", "A")]));
    }
}
