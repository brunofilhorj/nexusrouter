namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Mecanismo SASL do producer. <see cref="AwsMskIam"/> usa OAUTHBEARER no cliente Kafka.
/// </summary>
public enum KafkaSaslMechanism
{
    /// <summary>
    /// Sem SASL.
    /// </summary>
    None = 0,

    /// <summary>
    /// SASL/PLAIN. Usuário e senha vêm de variáveis de ambiente.
    /// </summary>
    Plain = 1,

    /// <summary>
    /// SCRAM-SHA-256. Usuário e senha vêm de variáveis de ambiente.
    /// </summary>
    ScramSha256 = 2,

    /// <summary>
    /// SCRAM-SHA-512. Usuário e senha vêm de variáveis de ambiente.
    /// </summary>
    ScramSha512 = 3,

    /// <summary>
    /// OAUTHBEARER genérico. Este dispatcher não gera o token; use <see cref="AwsMskIam"/> para MSK.
    /// </summary>
    OAuthBearer = 4,

    /// <summary>
    /// IAM do Amazon MSK. O callback OAUTHBEARER só é registrado quando este valor está selecionado.
    /// </summary>
    AwsMskIam = 5,
}
