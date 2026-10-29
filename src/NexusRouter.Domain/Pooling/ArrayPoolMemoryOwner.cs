using System.Buffers;

namespace NexusRouter.Domain.Pooling;

/// <summary>
/// Dono de um array alugado de <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
/// <typeparam name="T">Tipo do elemento alugado. No hot path o uso é <see cref="byte"/>.</typeparam>
/// <remarks>
/// O array informado no construtor passa a pertencer a esta instância e precisa ter saído de
/// <see cref="ArrayPool{T}.Shared"/>. <see cref="Dispose"/> é idempotente.
/// </remarks>
public sealed class ArrayPoolMemoryOwner<T> : IMemoryOwner<T>
{
    private readonly int _length;
    private T[]? _array;
    private int _disposed;

    /// <summary>
    /// Assume a propriedade de um array já alugado.
    /// </summary>
    /// <param name="array">Array obtido de <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="length">Quantidade de elementos válidos no início do array.</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> é negativo ou maior que o array.</exception>
    public ArrayPoolMemoryOwner(T[] array, int length)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, array.Length);

        _array = array;
        _length = length;
    }

    /// <summary>
    /// Fatia válida do array alugado.
    /// </summary>
    /// <exception cref="ObjectDisposedException">O dono já foi descartado.</exception>
    public Memory<T> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            T[]? array = _array;
            ObjectDisposedException.ThrowIf(array is null, this);
            return array.AsMemory(0, _length);
        }
    }

    /// <summary>
    /// Devolve o array ao pool compartilhado. Chamadas seguintes não devolvem de novo.
    /// </summary>
    /// <remarks>
    /// Zero allocation. A guarda usa <see cref="Interlocked.Exchange(ref int, int)"/> e tolera descarte concorrente.
    /// Os bytes são zerados antes do retorno para o próximo inquilino não observar payload FIX residual.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        T[]? array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array, clearArray: true);
        }
    }
}
