using System.Buffers;

using NexusRouter.Domain.Pooling;

namespace NexusRouter.Domain.Tests;

public sealed class ArrayPoolMemoryOwnerTests
{
    [Fact]
    public void Memory_ReturnsOnlyTheRequestedSlice()
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(8);
        var owner = new ArrayPoolMemoryOwner<byte>(array, 3);

        Assert.Equal(3, owner.Memory.Length);
    }

    [Fact]
    public void Dispose_ClearsAndReturnsTheArrayToThePool()
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(4);
        array.AsSpan(0, 4).Fill(42);
        var owner = new ArrayPoolMemoryOwner<byte>(array, 4);

        owner.Dispose();

        Assert.Equal(new byte[4], array.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Dispose_WhenCalledMoreThanOnce_IsIdempotent()
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(4);
        var owner = new ArrayPoolMemoryOwner<byte>(array, 4);

        owner.Dispose();
        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory);
    }
}
