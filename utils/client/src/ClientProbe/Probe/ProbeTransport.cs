namespace ClientProbe.Probe;

/// <summary>Egress transports the probe can consume. Names serialize as kafka, rabbit, fix.</summary>
public enum ProbeTransport
{
    /// <summary>Kafka topic consumer.</summary>
    Kafka,

    /// <summary>RabbitMQ exchange consumer.</summary>
    Rabbit,

    /// <summary>FIX 4.4 initiator session.</summary>
    Fix,
}
