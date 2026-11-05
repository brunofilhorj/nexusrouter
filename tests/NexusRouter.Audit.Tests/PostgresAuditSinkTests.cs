using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Audit;
using NexusRouter.Audit.Sinks;
using NexusRouter.Domain.Secrets;

namespace NexusRouter.Audit.Tests;

public sealed class PostgresAuditSinkTests
{
    [Fact]
    public async Task StartAsync_WhenCredentialKeyIsMissing_FailsBeforeOpeningTheSocket()
    {
        ISecretResolver resolver = new ConfigurationSecretResolver(
            new ConfigurationBuilder().Build());

        var sink = new PostgresAuditSink(
            new PostgresAuditSinkOptions
            {
                Host = "127.0.0.1",
                Database = "nexusrouter",
                UsernameEnvVar = "NEXUS_TEST_PG_USER_MISSING",
                PasswordEnvVar = "NEXUS_TEST_PG_PASSWORD_MISSING",
            },
            new AuditOptions(),
            NullLogger<PostgresAuditSink>.Instance,
            resolver);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.StartAsync(CancellationToken.None).AsTask());

        Assert.Contains("NEXUS_TEST_PG_USER_MISSING", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationSecretResolver_ResolvesKeyDefinedAsProcessEnvironmentVariable()
    {
        // Invariante B1 (fail-closed): uma credencial definida como variável de ambiente do processo
        // — como no docker-compose e no CI — continua resolvível através do IConfiguration, porque o
        // provider de variáveis de ambiente do .NET expõe cada variável sob o seu nome plano.
        string key = "NEXUS_TEST_PG_PASSWORD_" + Guid.NewGuid().ToString("N");
        const string expected = "s3nha-do-processo";
        Environment.SetEnvironmentVariable(key, expected);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            ISecretResolver resolver = new ConfigurationSecretResolver(configuration);

            string resolved = resolver.ResolveRequired(key);

            Assert.Equal(expected, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
