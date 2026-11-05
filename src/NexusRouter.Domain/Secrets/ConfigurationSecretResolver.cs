using Microsoft.Extensions.Configuration;

namespace NexusRouter.Domain.Secrets;

/// <summary>
/// Resolve segredos por <see cref="IConfiguration"/>, preservando compatibilidade com variáveis de ambiente do processo.
/// </summary>
/// <remarks>
/// <para>
/// A chave de lookup é lida direto de <see cref="IConfiguration"/> pelo indexador (<c>configuration[key]</c>).
/// O provider de variáveis de ambiente do .NET expõe cada variável do processo sob o seu nome exato, então uma
/// chave como <c>RABBITMQ_DEFAULT_PASS</c> continua resolvível quando definida como variável de ambiente, e também
/// quando definida em User Secrets, appsettings ou AWS Secrets Manager.
/// </para>
/// <para>
/// Convenção de chave: use o nome plano usado hoje (<c>RABBITMQ_DEFAULT_PASS</c>, <c>NEXUS_PG_USER</c>, etc.).
/// Para chaves hierárquicas do .NET (<c>Secao:Chave</c>), o duplo underscore <c>__</c> na variável de ambiente
/// mapeia para o separador <c>:</c> — comportamento padrão do provider, não implementado aqui.
/// O valor resolvido é devolvido sob demanda e nunca é gravado em options nem em log.
/// </para>
/// </remarks>
public sealed class ConfigurationSecretResolver : ISecretResolver
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Cria o resolvedor sobre a configuração da aplicação.
    /// </summary>
    /// <param name="configuration">Configuração que agrega variáveis de ambiente, User Secrets, appsettings e provedores externos.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> é nulo.</exception>
    public ConfigurationSecretResolver(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string ResolveRequired(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string? value = _configuration[key];
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"O segredo '{key}' não está definido em nenhuma fonte de configuração (variável de ambiente, User Secrets, appsettings ou AWS Secrets Manager).");
        }

        return value;
    }
}
