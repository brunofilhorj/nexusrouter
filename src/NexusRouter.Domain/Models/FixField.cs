namespace NexusRouter.Domain.Models;

/// <summary>
/// Par tag/valor de um campo FIX. O valor aponta para bytes já existentes e não copia o conteúdo.
/// </summary>
/// <param name="tag">Número da tag FIX.</param>
/// <param name="value">Bytes do valor no buffer da mensagem.</param>
public readonly struct FixField(int tag, ReadOnlyMemory<byte> value)
{
    /// <summary>
    /// Número da tag FIX.
    /// </summary>
    public int Tag { get; } = tag;

    /// <summary>
    /// Bytes do valor. Permanecem válidos enquanto o buffer de origem não for reutilizado.
    /// </summary>
    public ReadOnlyMemory<byte> Value { get; } = value;
}
