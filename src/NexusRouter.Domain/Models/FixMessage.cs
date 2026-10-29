namespace NexusRouter.Domain.Models;

/// <summary>
/// Mensagem FIX tokenizada, apoiada em spans do buffer original.
/// </summary>
/// <remarks>
/// É um <c>ref struct</c>: não pode ser campo de uma classe, não pode ser capturado por lambda
/// e não pode atravessar um <c>await</c>. Os spans apontam para o buffer que o chamador mantém vivo.
/// </remarks>
public readonly ref struct FixMessage
{
    /// <summary>
    /// Cria uma visão tokenizada sobre spans já existentes.
    /// </summary>
    /// <param name="msgType">Bytes de MsgType (tag 35).</param>
    /// <param name="fields">Campos tokenizados, na ordem em que aparecem na mensagem.</param>
    public FixMessage(ReadOnlySpan<byte> msgType, ReadOnlySpan<FixField> fields)
    {
        MsgType = msgType;
        Fields = fields;
    }

    /// <summary>
    /// Bytes de MsgType (tag 35).
    /// </summary>
    public ReadOnlySpan<byte> MsgType { get; }

    /// <summary>
    /// Campos tokenizados. O span é válido enquanto o buffer de campos de origem permanecer vivo.
    /// </summary>
    public ReadOnlySpan<FixField> Fields { get; }

    /// <summary>
    /// Procura o primeiro campo com a tag informada.
    /// </summary>
    /// <param name="tag">Número da tag FIX.</param>
    /// <param name="value">Bytes do valor quando a tag existe; span vazio quando não existe.</param>
    /// <returns><see langword="true"/> quando a tag foi encontrada.</returns>
    /// <remarks>Zero allocation. Seguro para chamada no hot path.</remarks>
    public bool TryGetField(int tag, out ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<FixField> fields = Fields;
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Tag == tag)
            {
                value = fields[i].Value.Span;
                return true;
            }
        }

        value = default;
        return false;
    }
}
