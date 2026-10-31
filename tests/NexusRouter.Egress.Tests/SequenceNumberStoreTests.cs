using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Egress.Fix;

namespace NexusRouter.Egress.Tests;

public sealed class SequenceNumberStoreTests
{
    [Fact]
    public async Task Next_AfterRestart_ContinuesFromLastFlush()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexusrouter-seq-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var store = new SequenceNumberStore(directory, NullLogger.Instance))
            {
                await store.LoadAsync("fix-out-1", CancellationToken.None);
                Assert.Equal(1, store.Next());
                Assert.Equal(2, store.Next());
            }

            await using var reloaded = new SequenceNumberStore(directory, NullLogger.Instance);
            await reloaded.LoadAsync("fix-out-1", CancellationToken.None);
            Assert.Equal(3, reloaded.Next());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResetAsync_Next_StartsAtOne()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexusrouter-seq-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new SequenceNumberStore(directory, NullLogger.Instance);
            await store.LoadAsync("fix-acceptor_OMS.1", CancellationToken.None);
            Assert.Equal(1, store.Next());
            Assert.Equal(2, store.Next());
            await store.ResetAsync(CancellationToken.None);
            Assert.Equal(1, store.Next());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_WhenDispatcherIdHasPathCharacter_Throws()
    {
        var store = new SequenceNumberStore(Path.GetTempPath(), NullLogger.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../out", CancellationToken.None).AsTask());
        await store.DisposeAsync();
    }
}
