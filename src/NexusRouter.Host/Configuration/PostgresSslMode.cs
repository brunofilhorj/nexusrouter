namespace NexusRouter.Host.Configuration;

/// <summary>
/// TLS da conexão de auditoria com o PostgreSQL. Os nomes acompanham o <c>SslMode</c> do Npgsql.
/// </summary>
public enum PostgresSslMode
{
    /// <summary>
    /// Sem TLS. É o valor do Postgres local.
    /// </summary>
    Disable = 0,

    /// <summary>
    /// Tenta TLS e segue sem ele se o servidor não oferecer.
    /// </summary>
    Allow = 1,

    /// <summary>
    /// Prefere TLS e aceita cair para texto puro.
    /// </summary>
    Prefer = 2,

    /// <summary>
    /// Exige TLS sem validar a cadeia.
    /// </summary>
    Require = 3,

    /// <summary>
    /// Exige TLS e valida a cadeia contra uma CA.
    /// </summary>
    VerifyCA = 4,

    /// <summary>
    /// Exige TLS, valida a cadeia e o nome do host.
    /// </summary>
    VerifyFull = 5,
}
