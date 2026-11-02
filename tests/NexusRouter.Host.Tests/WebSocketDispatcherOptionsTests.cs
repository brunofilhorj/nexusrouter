using Microsoft.Extensions.Options;

using NexusRouter.Host.Configuration;

namespace NexusRouter.Host.Tests;

public sealed class WebSocketDispatcherOptionsTests
{
    [Fact]
    public void Validate_ValidOptions_Succeeds()
    {
        WebSocketDispatcherOptions options = Valid();

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenListenAddressIsHostname_Fails()
    {
        var options = new WebSocketDispatcherOptions
        {
            Id = "ws-1",
            ListenAddress = "app.local",
            ListenPort = 6002,
            SequenceStoreDirectory = "/var/lib/seq",
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ListenAddress deve ser um endereço IP"));
    }

    [Fact]
    public void Validate_WhenListenPortIsZero_Fails()
    {
        var options = new WebSocketDispatcherOptions
        {
            Id = "ws-1",
            ListenAddress = "0.0.0.0",
            ListenPort = 0,
            SequenceStoreDirectory = "/var/lib/seq",
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ListenPort"));
    }

    [Fact]
    public void Validate_WhenAllowedOriginIsInvalid_Fails()
    {
        var options = new WebSocketDispatcherOptions
        {
            Id = "ws-1",
            ListenAddress = "0.0.0.0",
            ListenPort = 6002,
            AllowedOrigins = ["invalid-origin-no-scheme"],
            SequenceStoreDirectory = "/var/lib/seq",
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("AllowedOrigins[0] deve ser uma origem absoluta"));
    }

    [Fact]
    public void Validate_WhenTlsEnabledWithoutCertificatePath_Fails()
    {
        var options = new WebSocketDispatcherOptions
        {
            Id = "ws-1",
            ListenAddress = "0.0.0.0",
            ListenPort = 6002,
            SequenceStoreDirectory = "/var/lib/seq",
            Tls = new TlsOptions
            {
                Enabled = true,
                CertificatePath = null,
            },
        };

        ValidateOptionsResult result = options.Validate(null);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("CertificatePath é obrigatório quando Enabled é true"));
    }

    private static WebSocketDispatcherOptions Valid() =>
        new()
        {
            Id = "ws-dropcopy",
            ListenAddress = "0.0.0.0",
            ListenPort = 6002,
            AllowedOrigins = ["https://app.example.com"],
            SequenceStoreDirectory = "/var/lib/nexusrouter/seq",
            HeartbeatIntervalSeconds = 30,
            MissedHeartbeatLimit = 2,
            LogonTimeoutSeconds = 5,
            MaxControlFrameBytes = 4096,
            MaxFields = 64,
            MaxOutboundFrameBytes = 65536,
            SessionChannelCapacity = 1024,
            ChannelCapacity = 8192,
        };
}
