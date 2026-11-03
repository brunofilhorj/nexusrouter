using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// TLS de uma sessão de ingresso ou de um acceptor. A senha do certificado não entra aqui.
/// </summary>
public sealed class TlsOptions : IValidateOptions<TlsOptions>
{
    /// <summary>
    /// Quando <see langword="false"/>, o socket segue sem handshake.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Caminho do PKCS#12. Na sessão de ingresso é o certificado de cliente e pode ficar vazio.
    /// No acceptor é o certificado de servidor e passa a ser obrigatório quando <see cref="Enabled"/> é <see langword="true"/>.
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Nome da variável de ambiente com a senha do PKCS#12. Nulo quando o arquivo não tem senha.
    /// </summary>
    public string? CertificatePasswordEnvVar { get; init; }

    /// <summary>
    /// Confere o nome da variável e a combinação de caminho com senha.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Bloco TLS já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, TlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.EnvVar(options.CertificatePasswordEnvVar, nameof(options.CertificatePasswordEnvVar), required: false));
        if (!string.IsNullOrWhiteSpace(options.CertificatePasswordEnvVar)
            && string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            failures.Add("CertificatePath é obrigatório quando CertificatePasswordEnvVar está definido.");
        }

        if (!options.Enabled && !string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            failures.Add("CertificatePath só vale com Enabled true.");
        }

        return ConfigurationChecks.From(failures);
    }
}
