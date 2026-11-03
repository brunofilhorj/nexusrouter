using System.Buffers;

using NexusRouter.Domain.Pooling;

namespace NexusRouter.Domain.Tests;

public sealed class RefCountedMemoryOwnerTests
{
    [Fact]
    public void Release_DisposesInnerOwnerOnlyAfterTheLastReference()
    {
        var inner = new CountingMemoryOwner();
        var owner = new RefCountedMemoryOwner(inner, initialRefCount: 3);

        owner.Release();
        Assert.Equal(0, inner.DisposeCount);

        owner.Release();
        Assert.Equal(0, inner.DisposeCount);

        owner.Release();
        Assert.Equal(1, inner.DisposeCount);
    }

    private sealed class CountingMemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _buffer = new byte[1];

        public int DisposeCount { get; private set; }

        public Memory<byte> Memory => _buffer;

        public void Dispose() => DisposeCount++;
    }
}
