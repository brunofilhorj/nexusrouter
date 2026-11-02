using Microsoft.Extensions.Options;

using NexusRouter.Audit;
using NexusRouter.Host.Configuration;

using AuditOptions = NexusRouter.Host.Configuration.AuditOptions;
using PostgresAuditSinkOptions = NexusRouter.Host.Configuration.PostgresAuditSinkOptions;
using SerilogAuditSinkOptions = NexusRouter.Host.Configuration.SerilogAuditSinkOptions;

namespace NexusRouter.Host.Tests;

public sealed class AuditOptionsTests
{
    [Fact]
    public void Validate_ValidOptions_Succeeds()
    {
        var options = new AuditOptions
        {
            ChannelCapacity = 65536,
            OverflowMode = AuditOverflowMode.DropOldest,
            BatchSize = 1000,
            FlushIntervalMs = 100,
            PersistMode = AuditPersistMode.Full,
            SampleRate = 1.0,
            Sinks =
            [
                new SerilogAuditSinkOptions
                {
                    FilePath = "/var/log/audit-.log",
                    RollingInterval = AuditFileRollingInterval.Hour,
                },
                new PostgresAuditSinkOptions
                {
                    Host = "localhost",
                    Port = 5432,
                    Database = "nexusrouter",
                    UsernameEnvVar = "NEXUS_PG_USER",
                    PasswordEnvVar = "NEXUS_PG_PASSWORD",
                    SslMode = PostgresSslMode.Disable,
                },
            ],
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenSampleRateOutsideRange_Fails()
    {
        var options = new AuditOptions
        {
            SampleRate = 1.5,
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("SampleRate deve estar entre 0.0 e 1.0"));
    }

    [Fact]
    public void Validate_WhenSinkHasInvalidField_FailsWithPrefixedPath()
    {
        var options = new AuditOptions
        {
            Sinks =
            [
                new PostgresAuditSinkOptions
                {
                    Host = "localhost",
                    Port = 99999, // invalid port
                    Database = "nexusrouter",
                    UsernameEnvVar = "NEXUS_PG_USER",
                    PasswordEnvVar = "NEXUS_PG_PASSWORD",
                },
            ],
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("Sinks[0]: Port deve estar entre 1 e 65535"));
    }
}
