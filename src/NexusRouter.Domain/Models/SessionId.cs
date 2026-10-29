namespace NexusRouter.Domain.Models;

/// <summary>
/// Identificador imutável de uma sessão de roteamento.
/// </summary>
/// <remarks>
/// A igualdade e o hash são ordinais, para uso como chave de dicionário sem depender da cultura da thread.
/// </remarks>
public readonly record struct SessionId : IEquatable<SessionId>, IComparable<SessionId>
{
    /// <summary>
    /// Texto do identificador. O valor default da struct é uma string vazia.
    /// </summary>
    public string Value { get; } = string.Empty;

    /// <summary>
    /// Cria um identificador a partir do texto configurado para a sessão.
    /// </summary>
    /// <param name="value">Texto não nulo. Pode ser vazio.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> é nulo.</exception>
    public SessionId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>
    /// Compara dois identificadores pela ordem ordinal do texto.
    /// </summary>
    /// <param name="other">Identificador à direita da comparação.</param>
    /// <returns>Valor negativo, zero ou positivo, conforme a ordem ordinal.</returns>
    public int CompareTo(SessionId other) => string.CompareOrdinal(Value, other.Value);

    /// <summary>
    /// Indica se os dois identificadores têm o mesmo texto ordinal.
    /// </summary>
    /// <param name="other">Identificador comparado.</param>
    /// <returns><see langword="true"/> quando os textos coincidem.</returns>
    public bool Equals(SessionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>
    /// Hash ordinal do texto, estável dentro do processo e adequado a dicionários.
    /// </summary>
    /// <returns>Código de hash do identificador.</returns>
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
}
