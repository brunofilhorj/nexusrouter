using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Audit;
using NexusRouter.Audit.Sinks;

namespace NexusRouter.Audit.Tests;

public sealed class PostgresAuditSinkTests
{
    [Fact]
    public async Task StartAsync_WhenCredentialVariableIsMissing_FailsBeforeOpeningTheSocket()
    {
        var sink = new PostgresAuditSink(
            new PostgresAuditSinkOptions
            {
                Host = "127.0.0.1",
                Database = "nexusrouter",
                UsernameEnvVar = "NEXUS_TEST_PG_USER_MISSING",
                PasswordEnvVar = "NEXUS_TEST_PG_PASSWORD_MISSING",
            },
            new AuditOptions(),
            NullLogger<PostgresAuditSink>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.StartAsync(CancellationToken.None).AsTask());

        Assert.Contains("NEXUS_TEST_PG_USER_MISSING", exception.Message, StringComparison.Ordinal);
    }
}
