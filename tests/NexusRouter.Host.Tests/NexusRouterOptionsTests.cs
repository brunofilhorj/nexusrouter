using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using NexusRouter.Audit;
using NexusRouter.Domain.Enums;
using NexusRouter.Egress;
using NexusRouter.Egress.Kafka;
using NexusRouter.Host.Configuration;
using NexusRouter.SessionLog;

using AuditOptions = NexusRouter.Host.Configuration.AuditOptions;
using KafkaDispatcherOptions = NexusRouter.Host.Configuration.KafkaDispatcherOptions;
using PostgresAuditSinkOptions = NexusRouter.Host.Configuration.PostgresAuditSinkOptions;
using SerilogAuditSinkOptions = NexusRouter.Host.Configuration.SerilogAuditSinkOptions;

namespace NexusRouter.Host.Tests;

public sealed class NexusRouterOptionsTests
{
    [Fact]
    public void Validate_ValidConfiguration_Succeeds()
    {
        NexusRouterOptions options = CreateValidOptions();

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenNoSessions_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options = new NexusRouterOptions
        {
            Sessions = [],
            Dispatchers = options.Dispatchers,
            Clients = options.Clients,
            SessionLog = options.SessionLog,
            Audit = options.Audit,
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("Sessions: informe ao menos uma sessão"));
    }

    [Fact]
    public void Validate_WhenNoDispatchers_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options = new NexusRouterOptions
        {
            Sessions = options.Sessions,
            Dispatchers = [],
            Clients = options.Clients,
            SessionLog = options.SessionLog,
            Audit = options.Audit,
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("Dispatchers: informe ao menos um dispatcher"));
    }

    [Fact]
    public void Validate_WhenRoutePointsToMissingDispatcher_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options.Sessions[0].Routes.Add(new SessionRouteOptions { DispatcherId = "missing-dispatcher" });

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("DispatcherId 'missing-dispatcher' não existe em Dispatchers"));
    }

    [Fact]
    public void Validate_WhenDuplicateSessionId_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options.Sessions.Add(new SessionOptions
        {
            SessionId = options.Sessions[0].SessionId,
            Host = "host2.example.com",
            Port = 9877,
            SenderCompId = "CLIENT2",
            TargetCompId = "EXCHANGE_A",
            Routes = [new SessionRouteOptions { DispatcherId = "kafka-main" }],
        });

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("SessionId 'EXCH_A_ORDEXEC' está duplicado"));
    }

    [Fact]
    public void Validate_WhenDuplicateDispatcherId_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options.Dispatchers.Add(new KafkaDispatcherOptions
        {
            Id = options.Dispatchers[0].Id,
            BootstrapServers = "localhost:19092",
            Topic = "fix.raw",
            ClientId = "nexusrouter2",
            Acks = 1,
        });

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("Id 'kafka-main' está duplicado"));
    }

    [Fact]
    public void Validate_WhenKafkaAcksZeroUsesOnBrokerAck_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options.Dispatchers[0] = new KafkaDispatcherOptions
        {
            Id = "kafka-main",
            BootstrapServers = "localhost:19092",
            Topic = "fix.ordexec.raw",
            ClientId = "nexusrouter",
            Acks = 0,
            CursorAckMode = CursorAckMode.OnBrokerAck,
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("OnEnqueue"));
    }

    [Fact]
    public void Validate_WhenFixClientOverrideReplayModeIsUnknown_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        FixOutboundDispatcherOptions current = Assert.IsType<FixOutboundDispatcherOptions>(options.Dispatchers[1]);
        options.Dispatchers[1] = new FixOutboundDispatcherOptions
        {
            Id = current.Id,
            ListenAddress = current.ListenAddress,
            ListenPort = current.ListenPort,
            SenderCompId = current.SenderCompId,
            BeginString = current.BeginString,
            ClientOverrides =
            [
                new NexusRouter.Egress.Fix.AcceptorClientOverride
                {
                    SenderCompId = "OMS1",
                    ReplayMode = (NexusRouter.Egress.Fix.ReplayMode)99,
                },
            ],
        };

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ReplayMode"));
    }

    [Fact]
    public void Validate_WhenFixClientLacksSenderCompId_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options.Clients.Add(new ClientOptions
        {
            ClientId = Guid.NewGuid().ToString(),
            DispatcherId = "fix-out-client1",
            FixSenderCompId = null,
        });

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("FixSenderCompId é obrigatório para clientes do dispatcher FIX"));
    }

    [Fact]
    public void Validate_WhenWebSocketClientLacksTokenEnvVar_Fails()
    {
        NexusRouterOptions options = CreateValidOptions();
        options.Clients.Add(new ClientOptions
        {
            ClientId = Guid.NewGuid().ToString(),
            DispatcherId = "ws-dropcopy",
            TokenEnvVar = null,
        });

        ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("TokenEnvVar é obrigatório para clientes do dispatcher WebSocket"));
    }

    [Fact]
    public void FromConfiguration_BindsPolymorphicDispatchersAndSinks()
    {
        Dictionary<string, string?> configData = new()
        {
            ["NexusRouter:Sessions:0:SessionId"] = "EXCH_A",
            ["NexusRouter:Sessions:0:Host"] = "gateway.example.com",
            ["NexusRouter:Sessions:0:Port"] = "9876",
            ["NexusRouter:Sessions:0:SenderCompId"] = "SENDER",
            ["NexusRouter:Sessions:0:TargetCompId"] = "TARGET",
            ["NexusRouter:Sessions:0:Routes:0:DispatcherId"] = "kafka-1",
            ["NexusRouter:Sessions:0:Routes:1:DispatcherId"] = "ws-1",

            ["NexusRouter:Dispatchers:0:Id"] = "kafka-1",
            ["NexusRouter:Dispatchers:0:Type"] = "Kafka",
            ["NexusRouter:Dispatchers:0:BootstrapServers"] = "localhost:19092",
            ["NexusRouter:Dispatchers:0:Topic"] = "orders",
            ["NexusRouter:Dispatchers:0:ClientId"] = "nexus",
            ["NexusRouter:Dispatchers:0:Acks"] = "1",

            ["NexusRouter:Dispatchers:1:Id"] = "ws-1",
            ["NexusRouter:Dispatchers:1:Type"] = "WebSocket",
            ["NexusRouter:Dispatchers:1:ListenAddress"] = "0.0.0.0",
            ["NexusRouter:Dispatchers:1:ListenPort"] = "6002",

            ["NexusRouter:Dispatchers:2:Id"] = "fix-1",
            ["NexusRouter:Dispatchers:2:Type"] = "FixOutbound",
            ["NexusRouter:Dispatchers:2:ListenAddress"] = "127.0.0.1",
            ["NexusRouter:Dispatchers:2:ListenPort"] = "6001",
            ["NexusRouter:Dispatchers:2:SenderCompId"] = "NEXUS",
            ["NexusRouter:Dispatchers:2:BeginString"] = "FIX.4.4",
            ["NexusRouter:Dispatchers:2:Recovery:ReplayMode"] = "StartOfDay",
            ["NexusRouter:Dispatchers:2:ClientOverrides:0:SenderCompId"] = "OMS1",
            ["NexusRouter:Dispatchers:2:ClientOverrides:0:ReplayMode"] = "FromLogon",
            ["NexusRouter:SessionLog:Directory"] = "data/sessionlog",

            ["NexusRouter:Audit:ChannelCapacity"] = "32768",
            ["NexusRouter:Audit:Sinks:0:Type"] = "Serilog",
            ["NexusRouter:Audit:Sinks:0:FilePath"] = "/var/log/audit.log",
            ["NexusRouter:Audit:Sinks:1:Type"] = "Postgres",
            ["NexusRouter:Audit:Sinks:1:Host"] = "localhost",
            ["NexusRouter:Audit:Sinks:1:Port"] = "5432",
            ["NexusRouter:Audit:Sinks:1:Database"] = "nexusrouter",
            ["NexusRouter:Audit:Sinks:1:UsernameEnvVar"] = "PG_USER",
            ["NexusRouter:Audit:Sinks:1:PasswordEnvVar"] = "PG_PASS",
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        NexusRouterOptions options = NexusRouterOptions.FromConfiguration(config);

        Assert.Single(options.Sessions);
        Assert.Equal(3, options.Dispatchers.Count);
        Assert.IsType<KafkaDispatcherOptions>(options.Dispatchers[0]);
        Assert.IsType<WebSocketDispatcherOptions>(options.Dispatchers[1]);
        FixOutboundDispatcherOptions fixOptions = Assert.IsType<FixOutboundDispatcherOptions>(options.Dispatchers[2]);
        Assert.Equal(NexusRouter.Egress.Fix.ReplayMode.StartOfDay, fixOptions.Recovery.ReplayMode);
        Assert.Equal(NexusRouter.Egress.Fix.ReplayMode.FromLogon, Assert.Single(fixOptions.ClientOverrides).ReplayMode);
        Assert.Equal(32768, options.Audit.ChannelCapacity);
        Assert.Equal(2, options.Audit.Sinks.Count);
        Assert.IsType<SerilogAuditSinkOptions>(options.Audit.Sinks[0]);
        Assert.IsType<PostgresAuditSinkOptions>(options.Audit.Sinks[1]);

        ValidateOptionsResult result = options.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    private static NexusRouterOptions CreateValidOptions() =>
        new()
        {
            Sessions =
            [
                new SessionOptions
                {
                    SessionId = "EXCH_A_ORDEXEC",
                    Host = "fix-gateway-a.exchange.com",
                    Port = 9876,
                    SenderCompId = "CLIENT1",
                    TargetCompId = "EXCHANGE_A",
                    HeartbeatIntervalSeconds = 30,
                    DeliveryMode = DeliveryMode.PassThrough,
                    FrameDecoder = FrameDecoderKind.Fix44,
                    Routes =
                    [
                        new SessionRouteOptions { DispatcherId = "kafka-main" },
                        new SessionRouteOptions { DispatcherId = "fix-out-client1" },
                        new SessionRouteOptions { DispatcherId = "rabbitmq-dropcopy" },
                        new SessionRouteOptions { DispatcherId = "ws-dropcopy" },
                    ],
                },
            ],
            Dispatchers =
            [
                new KafkaDispatcherOptions
                {
                    Id = "kafka-main",
                    BootstrapServers = "localhost:19092",
                    Topic = "fix.ordexec.raw",
                    ClientId = "nexusrouter",
                    Acks = 0,
                    CursorAckMode = CursorAckMode.OnEnqueue,
                    SecurityProtocol = KafkaSecurityProtocol.Plaintext,
                    SaslMechanism = KafkaSaslMechanism.None,
                    PartitionStrategy = KafkaPartitionStrategy.None,
                    ChannelCapacity = 16384,
                },
                new FixOutboundDispatcherOptions
                {
                    Id = "fix-out-client1",
                    ListenAddress = "0.0.0.0",
                    ListenPort = 6001,
                    SenderCompId = "NEXUS",
                    BeginString = "FIX.4.4",
                    ChannelCapacity = 8192,
                },
                new RabbitMQDispatcherOptions
                {
                    Id = "rabbitmq-dropcopy",
                    Host = "rabbitmq.internal",
                    Port = 5672,
                    VirtualHost = "/trading",
                    UsernameEnvVar = "RABBITMQ_USER",
                    PasswordEnvVar = "RABBITMQ_PASS",
                    Exchange = "dropcopy.fanout",
                    ChannelCapacity = 8192,
                },
                new WebSocketDispatcherOptions
                {
                    Id = "ws-dropcopy",
                    ListenAddress = "0.0.0.0",
                    ListenPort = 6002,
                    AllowedOrigins = ["https://app.example.com"],
                    HeartbeatIntervalSeconds = 30,
                    MissedHeartbeatLimit = 2,
                    LogonTimeoutSeconds = 5,
                    MaxControlFrameBytes = 4096,
                    MaxFields = 64,
                    MaxOutboundFrameBytes = 65536,
                    SessionChannelCapacity = 1024,
                    ChannelCapacity = 8192,
                },
            ],
            Clients =
            [
                new ClientOptions
                {
                    ClientId = "6f1e0b3a-9c2d-4a7e-8b15-2d4c0a9e7f31",
                    DispatcherId = "fix-out-client1",
                    FixSenderCompId = "OMS1",
                },
                new ClientOptions
                {
                    ClientId = "a4c8e2d1-1b6f-4e90-9a33-7c5d2e8b0146",
                    DispatcherId = "kafka-main",
                },
                new ClientOptions
                {
                    ClientId = "c1d2e3f4-a5b6-4789-8abc-def012345678",
                    DispatcherId = "ws-dropcopy",
                    TokenEnvVar = "NEXUS_WS_CLIENT_TOKEN",
                },
            ],
            SessionLog = new SessionLogOptions
            {
                Directory = "data/sessionlog",
            },
            Audit = new AuditOptions
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
                        FilePath = "/var/log/nexusrouter/audit-.log",
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
                        CommandTimeoutSeconds = 15,
                    },
                ],
            },
        };
}
