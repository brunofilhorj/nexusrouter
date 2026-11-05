namespace NexusRouter.Domain.Secrets;

/// <summary>
/// Resolve um segredo a partir de uma chave de lookup, sem materializá-lo em options nem em log.
/// </summary>
/// <remarks>
/// A chave é o mesmo nome que historicamente era uma variável de ambiente (ex.: <c>RABBITMQ_DEFAULT_PASS</c>).
/// A implementação decide a fonte (variável de ambiente, User Secrets, appsettings, AWS Secrets Manager),
/// mas o contrato é sempre o mesmo: o chamador passa a chave e recebe o valor sob demanda. O valor nunca
/// é gravado em options nem registrado em log.
/// </remarks>
public interface ISecretResolver
{
    /// <summary>
    /// Resolve o valor obrigatório associado a <paramref name="key"/>.
    /// </summary>
    /// <param name="key">Chave de lookup. É o nome da variável de ambiente usado historicamente.</param>
    /// <returns>Valor não vazio associado à chave.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> é nulo ou vazio.</exception>
    /// <exception cref="InvalidOperationException">A chave não está definida em nenhuma fonte ou está vazia.</exception>
    string ResolveRequired(string key);
}
