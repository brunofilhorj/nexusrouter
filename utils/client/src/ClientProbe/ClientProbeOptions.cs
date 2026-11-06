namespace ClientProbe;

/// <summary>
/// Root options bound from the <c>ClientProbe</c> configuration section.
/// Credential values are not stored here; Rabbit names an environment variable.
/// </summary>
public sealed class ClientProbeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ClientProbe";

    /// <summary>Default public origin for the page, the API, and telemetry.</summary>
    public const string DefaultUrls = "http://localhost:8100";

    /// <summary>Kestrel listen URLs. The browser uses this origin only.</summary>
    public string Urls { get; set; } = DefaultUrls;

    /// <summary>Telemetry channel and sampling defaults.</summary>
    public TelemetryOptions Telemetry { get; set; } = new();

    /// <summary>Kafka consumer defaults.</summary>
    public KafkaOptions Kafka { get; set; } = new();

    /// <summary>Rabbit consumer defaults. User and password are environment-variable names.</summary>
    public RabbitOptions Rabbit { get; set; } = new();

    /// <summary>FIX initiator defaults.</summary>
    public FixOptions Fix { get; set; } = new();

    /// <summary>WebSocket screen defaults. An empty URL means the acceptor is not configured yet.</summary>
    public WebSocketOptions WebSocket { get; set; } = new();

    /// <summary>Bounded channel and preview limits for probe telemetry.</summary>
    public sealed class TelemetryOptions
    {
        /// <summary>Capacity of the telemetry channel. A failed write increments dropped.</summary>
        public int ChannelCapacity { get; set; } = 1024;

        /// <summary>Event rate above which the hub samples instead of forwarding every event.</summary>
        public int SampleAbovePerSecond { get; set; } = 50;

        /// <summary>Maximum characters kept in a raw frame preview.</summary>
        public int RawPreviewLength { get; set; } = 240;

        /// <summary>Number of recent samples retained for the page.</summary>
        public int RingSize { get; set; } = 500;
    }

    /// <summary>Default Kafka profile applied until the operator connects with another one.</summary>
    public sealed class KafkaOptions
    {
        /// <summary>Broker address, including port.</summary>
        public string BootstrapServers { get; set; } = "127.0.0.1:19092";

        /// <summary>Topic that carries the raw FIX frame.</summary>
        public string Topic { get; set; } = "fix.ordexec.raw";

        /// <summary>Consumer group id.</summary>
        public string GroupId { get; set; } = "probe-local";

        /// <summary>Offset reset policy. Default is latest.</summary>
        public string AutoOffsetReset { get; set; } = "Latest";
    }

    /// <summary>Default Rabbit profile. Password material stays in the named environment variable.</summary>
    public sealed class RabbitOptions
    {
        /// <summary>Broker host.</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>Broker port.</summary>
        public int Port { get; set; } = 5672;

        /// <summary>Virtual host.</summary>
        public string VirtualHost { get; set; } = "/";

        /// <summary>Exchange the router publishes to.</summary>
        public string Exchange { get; set; } = "fix.orders";

        /// <summary>Routing key the router publishes with.</summary>
        public string RoutingKey { get; set; } = "fix.raw";

        /// <summary>Name of the environment variable that holds the user name.</summary>
        public string UsernameEnvVar { get; set; } = "RABBITMQ_DEFAULT_USER";

        /// <summary>Name of the environment variable that holds the password.</summary>
        public string PasswordEnvVar { get; set; } = "RABBITMQ_DEFAULT_PASS";
    }

    /// <summary>Default FIX initiator profile.</summary>
    public sealed class FixOptions
    {
        /// <summary>Acceptor host.</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>Acceptor port.</summary>
        public int Port { get; set; } = 6001;

        /// <summary>SenderCompID sent by the probe.</summary>
        public string SenderCompId { get; set; } = "PROBE";

        /// <summary>TargetCompID expected by the acceptor.</summary>
        public string TargetCompId { get; set; } = "NEXUSROUTER";

        /// <summary>Heartbeat interval, in seconds.</summary>
        public int HeartBtInt { get; set; } = 20;
    }

    /// <summary>Defaults for the egress WebSocket screen. The page opens that socket itself.</summary>
    public sealed class WebSocketOptions
    {
        /// <summary>Acceptor URL. Empty until the operator fills host and port.</summary>
        public string Url { get; set; } = "";

        /// <summary>Subprotocol the page offers on the egress socket.</summary>
        public string Subprotocol { get; set; } = "nexusrouter.v1";
    }
}
