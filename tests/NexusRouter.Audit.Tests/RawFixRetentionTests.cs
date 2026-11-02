using NexusRouter.Audit;
using NexusRouter.Domain.Models;

namespace NexusRouter.Audit.Tests;

public sealed class RawFixRetentionTests
{
    [Fact]
    public void ShouldStore_WhenModeIsMetadataOnly_OmitsRawFix()
    {
        var retention = new RawFixRetention(new AuditOptions { PersistMode = AuditPersistMode.MetadataOnly });
        var record = new AuditRecord { RawFix = [1, 2, 3] };

        Assert.False(retention.ShouldStore(record));
    }

    [Fact]
    public void ShouldStore_WhenSampleRateIsOneHalf_StoresEverySecondPayload()
    {
        var retention = new RawFixRetention(new AuditOptions
        {
            PersistMode = AuditPersistMode.Sampled,
            SampleRate = 0.5,
        });
        var record = new AuditRecord { RawFix = [1] };

        Assert.False(retention.ShouldStore(record));
        Assert.True(retention.ShouldStore(record));
        Assert.False(retention.ShouldStore(record));
    }
}
