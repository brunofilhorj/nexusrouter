using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Host.Extensions;

using Npgsql;

using RabbitMQ.Client;

using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace NexusRouter.Host.Tests;

public sealed class FanOutEndToEndTests
{
    private const int MessageCount = 100;
    private const string SessionId = "FANOUT";
    private const string KafkaTopic = "fix.ordexec.raw";
    private const string KafkaDispatcherId = "kafka-fix-raw";
    private const string RabbitDispatcherId = "rabbitmq-fix-raw";
    private const string RabbitExchange = "fix.orders";
    private const string RabbitRoutingKey = "fix.raw";
    private const string RabbitQueue = "fix.raw.e2e";
    private const string PostgresUserKey = "NEXUS_E2E_PG_USER";
    private const string PostgresPasswordKey = "NEXUS_E2E_PG_PASSWORD";
    private const string RabbitUserKey = "NEXUS_E2E_RABBIT_USER";
    private const string RabbitPasswordKey = "NEXUS_E2E_RABBIT_PASSWORD";
    private const string BrokerUser = "nexus";
    private const string BrokerPassword = "nexus";

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private static readonly Regex AuditLine = new(
        """Auditoria FANOUT (\d+) 8 (E2E\d{3}) (\d+) "PassThrough" \[[^\]]+\] (\d+) false""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FanOut_HundredFixMessages_ReachesKafkaRabbitSerilogAndPostgres()
    {
        var kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();
        var rabbit = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
            .WithUsername(BrokerUser)
            .WithPassword(BrokerPassword)
            .Build();
        var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
            .WithDatabase("nexusrouter")
            .WithUsername(BrokerUser)
            .WithPassword(BrokerPassword)
            .Build();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        string tempDirectory = Directory.CreateTempSubdirectory("nexusrouter-fanout-").FullName;
        string auditPath = Path.Combine(tempDirectory, "audit.log");

        try
        {
            await Task.WhenAll(kafka.StartAsync(), rabbit.StartAsync(), postgres.StartAsync());
            await ApplyPostgresSchemaAsync(postgres.GetConnectionString());
            await CreateKafkaTopicAsync(kafka.GetBootstrapAddress());
            await BindRabbitQueueAsync(rabbit.Hostname, rabbit.GetMappedPublicPort(5672));

            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync().WaitAsync(StepTimeout);

            using IHost host = BuildHost(port, kafka.GetBootstrapAddress(), rabbit, postgres, auditPath);
            await host.StartAsync();
            using TcpClient peer = await accepted;
            NetworkStream stream = peer.GetStream();
            byte[] logon = await ReadFixFrameAsync(stream);
            Assert.Equal("A", GetFieldValue(logon, 35));
            await stream.WriteAsync(CreateLogonAcknowledgement());

            for (int i = 0; i < MessageCount; i++)
            {
                await stream.WriteAsync(CreateExecutionReport(i));
            }

            List<byte[]> kafkaMessages = await ReadKafkaAsync(kafka.GetBootstrapAddress());
            List<byte[]> rabbitMessages = await ReadRabbitAsync(rabbit.Hostname, rabbit.GetMappedPublicPort(5672));
            await host.StopAsync();

            AssertPayloads(kafkaMessages, "Kafka", json: true);
            AssertPayloads(rabbitMessages, "RabbitMQ", json: false);
            AssertSerilog(auditPath);
            await AssertPostgresAsync(postgres.GetConnectionString());
        }
        finally
        {
            listener.Stop();
            await postgres.DisposeAsync();
            await rabbit.DisposeAsync();
            await kafka.DisposeAsync();
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IHost BuildHost(
        int port,
        string kafkaBootstrap,
        RabbitMqContainer rabbit,
        PostgreSqlContainer postgres,
        string auditPath)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["NexusRouter:Sessions:0:SessionId"] = SessionId,
            ["NexusRouter:Sessions:0:Host"] = IPAddress.Loopback.ToString(),
            ["NexusRouter:Sessions:0:Port"] = port.ToString(),
            ["NexusRouter:Sessions:0:SenderCompId"] = "CLIENT",
            ["NexusRouter:Sessions:0:TargetCompId"] = "EXCHANGE",
            ["NexusRouter:Sessions:0:FrameDecoder"] = "Fix44",
            ["NexusRouter:Sessions:0:DeliveryMode"] = "PassThrough",
            ["NexusRouter:Sessions:0:HeartbeatIntervalSeconds"] = "30",
            ["NexusRouter:Sessions:0:ShutdownTimeoutSeconds"] = "2",
            ["NexusRouter:Sessions:0:Routes:0:DispatcherId"] = KafkaDispatcherId,
            ["NexusRouter:Sessions:0:Routes:1:DispatcherId"] = RabbitDispatcherId,
            ["NexusRouter:Dispatchers:0:Id"] = KafkaDispatcherId,
            ["NexusRouter:Dispatchers:0:Type"] = "Kafka",
            ["NexusRouter:Dispatchers:0:BootstrapServers"] = kafkaBootstrap,
            ["NexusRouter:Dispatchers:0:Topic"] = KafkaTopic,
            ["NexusRouter:Dispatchers:0:ClientId"] = "nexusrouter-e2e",
            ["NexusRouter:Dispatchers:0:Acks"] = "1",
            ["NexusRouter:Dispatchers:0:SecurityProtocol"] = "Plaintext",
            ["NexusRouter:Dispatchers:0:SaslMechanism"] = "None",
            ["NexusRouter:Dispatchers:0:ChannelCapacity"] = "1024",
            ["NexusRouter:Dispatchers:0:PartitionStrategy"] = "None",
            ["NexusRouter:Dispatchers:1:Id"] = RabbitDispatcherId,
            ["NexusRouter:Dispatchers:1:Type"] = "RabbitMQ",
            ["NexusRouter:Dispatchers:1:Host"] = rabbit.Hostname,
            ["NexusRouter:Dispatchers:1:Port"] = rabbit.GetMappedPublicPort(5672).ToString(),
            ["NexusRouter:Dispatchers:1:VirtualHost"] = "/",
            ["NexusRouter:Dispatchers:1:UsernameEnvVar"] = RabbitUserKey,
            ["NexusRouter:Dispatchers:1:PasswordEnvVar"] = RabbitPasswordKey,
            ["NexusRouter:Dispatchers:1:Exchange"] = RabbitExchange,
            ["NexusRouter:Dispatchers:1:RoutingKey"] = RabbitRoutingKey,
            ["NexusRouter:Dispatchers:1:ChannelCapacity"] = "1024",
            ["NexusRouter:Dispatchers:1:PayloadEncoding"] = "Raw",
            ["NexusRouter:SessionLog:Directory"] = Path.Combine(Path.GetDirectoryName(auditPath) ?? Path.GetTempPath(), "sessionlog"),
            ["NexusRouter:SessionLog:SegmentSizeMB"] = "1",
            ["NexusRouter:SessionLog:IndexSlotsPerSegment"] = "1000",
            ["NexusRouter:SessionLog:FlushIntervalMs"] = "60000",
            ["NexusRouter:Audit:ChannelCapacity"] = "1024",
            ["NexusRouter:Audit:OverflowMode"] = "DropOldest",
            ["NexusRouter:Audit:BatchSize"] = "100",
            ["NexusRouter:Audit:FlushIntervalMs"] = "50",
            ["NexusRouter:Audit:PersistMode"] = "Full",
            ["NexusRouter:Audit:SampleRate"] = "1.0",
            ["NexusRouter:Audit:Sinks:0:Type"] = "Serilog",
            ["NexusRouter:Audit:Sinks:0:FilePath"] = auditPath,
            ["NexusRouter:Audit:Sinks:0:RollingInterval"] = "Infinite",
            ["NexusRouter:Audit:Sinks:1:Type"] = "Postgres",
            ["NexusRouter:Audit:Sinks:1:Host"] = postgres.Hostname,
            ["NexusRouter:Audit:Sinks:1:Port"] = postgres.GetMappedPublicPort(5432).ToString(),
            ["NexusRouter:Audit:Sinks:1:Database"] = "nexusrouter",
            ["NexusRouter:Audit:Sinks:1:UsernameEnvVar"] = PostgresUserKey,
            ["NexusRouter:Audit:Sinks:1:PasswordEnvVar"] = PostgresPasswordKey,
            ["NexusRouter:Audit:Sinks:1:SslMode"] = "Disable",
            [PostgresUserKey] = BrokerUser,
            [PostgresPasswordKey] = BrokerPassword,
            [RabbitUserKey] = BrokerUser,
            [RabbitPasswordKey] = BrokerPassword,
        };

        HostApplicationBuilder builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = Path.GetDirectoryName(auditPath),
            EnvironmentName = Environments.Development,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services
            .AddNexusRouterAudit(builder.Configuration)
            .AddNexusRouterSessionLog(builder.Configuration)
            .AddNexusRouterEgress(builder.Configuration)
            .AddNexusRouterRouter(builder.Configuration)
            .AddNexusRouterIngress(builder.Configuration);
        return builder.Build();
    }

    private static async Task ApplyPostgresSchemaAsync(string connectionString)
    {
        string sql = File.ReadAllText(FindRepoFile(Path.Combine("docker", "postgres", "init.sql")));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateKafkaTopicAsync(string bootstrapServers)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification { Name = KafkaTopic, NumPartitions = 1, ReplicationFactor = 1 },
            ]);
        }
        catch (CreateTopicsException exception)
            when (exception.Results.Count == 1 && exception.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
        {
        }
    }

    private static async Task BindRabbitQueueAsync(string host, int port)
    {
        var factory = new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = BrokerUser,
            Password = BrokerPassword,
        };
        using IConnection connection = factory.CreateConnection();
        using IModel channel = connection.CreateModel();
        channel.ExchangeDeclare(RabbitExchange, ExchangeType.Direct, durable: false, autoDelete: false);
        channel.QueueDeclare(RabbitQueue, durable: false, exclusive: false, autoDelete: false);
        channel.QueueBind(RabbitQueue, RabbitExchange, RabbitRoutingKey);
        await Task.CompletedTask;
    }

    private static async Task<List<byte[]>> ReadKafkaAsync(string bootstrapServers)
    {
        var payloads = new List<byte[]>(MessageCount);
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "fanout-e2e",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(KafkaTopic);
        DateTime deadline = DateTime.UtcNow + StepTimeout;
        while (payloads.Count < MessageCount && DateTime.UtcNow < deadline)
        {
            ConsumeResult<Ignore, byte[]>? result = consumer.Consume(TimeSpan.FromMilliseconds(200));
            if (result?.Message?.Value is { Length: > 0 } value && IsExecutionReport(value))
            {
                payloads.Add(value);
            }

            await Task.Yield();
        }

        consumer.Close();
        if (payloads.Count < MessageCount)
        {
            throw new TimeoutException($"Kafka entregou {payloads.Count} de {MessageCount} mensagens.");
        }

        return payloads;
    }

    private static async Task<List<byte[]>> ReadRabbitAsync(string host, int port)
    {
        var payloads = new List<byte[]>(MessageCount);
        var factory = new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = BrokerUser,
            Password = BrokerPassword,
        };
        using IConnection connection = factory.CreateConnection();
        using IModel channel = connection.CreateModel();
        DateTime deadline = DateTime.UtcNow + StepTimeout;
        while (payloads.Count < MessageCount && DateTime.UtcNow < deadline)
        {
            BasicGetResult? delivery = channel.BasicGet(RabbitQueue, autoAck: true);
            if (delivery is null)
            {
                await Task.Delay(50);
                continue;
            }

            byte[] body = delivery.Body.ToArray();
            if (IsExecutionReport(body))
            {
                payloads.Add(body);
            }
        }

        if (payloads.Count < MessageCount)
        {
            throw new TimeoutException($"RabbitMQ entregou {payloads.Count} de {MessageCount} mensagens.");
        }

        return payloads;
    }

    private static bool IsExecutionReport(ReadOnlySpan<byte> frame)
    {
        string message = Encoding.ASCII.GetString(frame);
        if (message.StartsWith('{'))
        {
            // PayloadEncoding=Json: envelope com msgType.
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            return doc.RootElement.TryGetProperty("msgType", out var msgType) && msgType.GetString() == "8";
        }

        foreach (string field in message.Split('\x01', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("35=", StringComparison.Ordinal))
            {
                return field.Equals("35=8", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static void AssertPayloads(List<byte[]> payloads, string destination, bool json)
    {
        Assert.Equal(MessageCount, payloads.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (byte[] payload in payloads)
        {
            string clOrdId = json
                ? System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("fields").GetProperty("11").GetString()!
                : GetFieldValue(payload, 11);
            Assert.StartsWith("E2E", clOrdId, StringComparison.Ordinal);
            ids.Add(clOrdId);
        }

        Assert.Equal(MessageCount, ids.Count);
        Assert.Contains("E2E000", ids);
        Assert.Contains("E2E099", ids);
        _ = destination;
    }

    private static void AssertSerilog(string auditPath)
    {
        string text = File.ReadAllText(auditPath);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AuditLine.Matches(text))
        {
            long ingress = long.Parse(match.Groups[1].Value);
            long egress = long.Parse(match.Groups[4].Value);
            Assert.True(ingress > 0);
            Assert.True(egress >= ingress);
            ids.Add(match.Groups[2].Value);
            Assert.Equal(match.Groups[2].Value, $"E2E{int.Parse(match.Groups[3].Value) - 100:D3}");
        }

        Assert.True(
            ids.Count == MessageCount,
            text.Length == 0 ? "O arquivo de auditoria está vazio." : text[..Math.Min(text.Length, 1200)]);
    }

    private static async Task AssertPostgresAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM ingress_fix_messages
            WHERE session_id = @session
              AND msg_type = '8'
              AND raw_fix IS NOT NULL
              AND ingress_ts IS NOT NULL
            """,
            connection);
        command.Parameters.AddWithValue("session", SessionId);
        long rows = (long)(await command.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(MessageCount, rows);
    }

    private static async Task<byte[]> ReadFixFrameAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(StepTimeout);
        var bytes = new List<byte>(256);
        var single = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(single, timeout.Token);
            if (read == 0)
            {
                throw new EndOfStreamException("O peer fechou o socket antes do frame FIX.");
            }

            bytes.Add(single[0]);
            if (HasChecksumTerminator(bytes))
            {
                return bytes.ToArray();
            }
        }
    }

    private static bool HasChecksumTerminator(List<byte> bytes)
    {
        int start = bytes.Count - 8;
        return start >= 0
            && bytes[start] == 0x01
            && bytes[start + 1] == (byte)'1'
            && bytes[start + 2] == (byte)'0'
            && bytes[start + 3] == (byte)'='
            && bytes[start + 4] is >= (byte)'0' and <= (byte)'9'
            && bytes[start + 5] is >= (byte)'0' and <= (byte)'9'
            && bytes[start + 6] is >= (byte)'0' and <= (byte)'9'
            && bytes[start + 7] == 0x01;
    }

    // A sessão valida SendingTime (52) contra o relógio local (TD-06); um valor fixo cairia fora da tolerância.
    private static string SendingTimeNow() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] CreateLogonAcknowledgement() =>
        CreateFixFrame("35=A", "49=EXCHANGE", "56=CLIENT", "34=1", $"52={SendingTimeNow()}", "98=0", "108=30");

    private static byte[] CreateExecutionReport(int index)
    {
        int sequence = 100 + index;
        return CreateFixFrame(
            "35=8",
            "49=EXCHANGE",
            "56=CLIENT",
            $"34={sequence}",
            $"52={SendingTimeNow()}",
            $"11=E2E{index:D3}",
            "150=0",
            "39=0",
            "55=PETR4");
    }

    private static byte[] CreateFixFrame(params string[] fields)
    {
        var bodyBuilder = new StringBuilder();
        foreach (string field in fields)
        {
            bodyBuilder.Append(field).Append('\x01');
        }

        string body = bodyBuilder.ToString();
        int bodyLength = Encoding.ASCII.GetByteCount(body);
        string prefix = $"8=FIX.4.4\u00019={bodyLength}\u0001{body}";
        byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);
        int checksum = 0;
        foreach (byte value in prefixBytes)
        {
            checksum += value;
        }

        return Encoding.ASCII.GetBytes($"{prefix}10={checksum & 0xFF:D3}\u0001");
    }

    private static string GetFieldValue(ReadOnlySpan<byte> frame, int tag)
    {
        string prefix = $"{tag}=";
        string message = Encoding.ASCII.GetString(frame);
        foreach (string field in message.Split('\x01', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal))
            {
                return field[prefix.Length..];
            }
        }

        throw new InvalidDataException($"O campo FIX {tag} não foi encontrado.");
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Arquivo não encontrado a partir do diretório de teste: {relativePath}");
    }
}
