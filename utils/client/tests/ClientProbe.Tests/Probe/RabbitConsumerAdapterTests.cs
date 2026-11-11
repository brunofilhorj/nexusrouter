using ClientProbe.Probe;
using ClientProbe.Rabbit;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;
using RabbitMQ.Client.Exceptions;

namespace ClientProbe.Tests.Probe;

public sealed class RabbitConsumerAdapterTests
{
    private sealed class NullPublisher : ITelemetryPublisher
    {
        public bool TryPublish(ProbeEvent probeEvent, ProbeCounters counters) => true;
    }

    [Fact]
    public async Task Connect_WithoutCredentials_FaultsWithClearMessage_AndDoesNotDial()
    {
        var adapter = new RabbitConsumerAdapter(new NullPublisher(), Options.Create(new ClientProbeOptions()), TimeProvider.System);
        var profile = new RabbitProfile(
            "127.0.0.1", 1, "/", "fix.orders", "fix.raw",
            "PROBE_TEST_MISSING_USER_" + Guid.NewGuid().ToString("N"),
            "PROBE_TEST_MISSING_PASS_" + Guid.NewGuid().ToString("N"));

        await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.Equal(ProbeState.Faulted, adapter.State);
        Assert.Contains("Credenciais RabbitMQ ausentes", adapter.LastError);
    }

    [Fact]
    public void Describe_SurfacesAuthenticationCause()
    {
        var ex = new BrokerUnreachableException(new AuthenticationFailureException("ACCESS_REFUSED"));

        var message = RabbitConsumerAdapter.Describe(ex);

        Assert.Contains("autenticação recusada", message);
        Assert.Contains("ACCESS_REFUSED", message);
    }

    [Fact]
    public void RabbitProfile_ToString_DoesNotLeakPassword()
    {
        var profile = new RabbitProfile("h", 1, "/", "x", "k", "U", "P", Password: "s3cr3t", Username: "nexus");

        Assert.DoesNotContain("s3cr3t", profile.ToString());
        Assert.Contains("nexus", profile.ToString());
    }
}
