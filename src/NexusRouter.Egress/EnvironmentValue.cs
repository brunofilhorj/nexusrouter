namespace NexusRouter.Egress;

/// <summary>
/// Lê um segredo de variável de ambiente sem gravá-lo em options nem em log.
/// </summary>
internal static class EnvironmentValue
{
    /// <summary>
    /// Lê o valor da variável. A exceção cita o nome, nunca o conteúdo.
    /// </summary>
    /// <param name="variableName">Nome da variável de ambiente.</param>
    /// <returns>Valor não vazio.</returns>
    /// <exception cref="ArgumentException"><paramref name="variableName"/> é vazio.</exception>
    /// <exception cref="InvalidOperationException">A variável não existe ou está vazia.</exception>
    public static string ReadRequired(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"A variável de ambiente '{variableName}' não está definida.");
        }

        return value;
    }
}
