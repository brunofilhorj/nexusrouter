using System.Buffers;

namespace NexusRouter.Domain.Pooling;

/// <summary>
/// Compartilha um único <see cref="IMemoryOwner{T}"/> entre vários consumidores do fan-out.
/// </summary>
/// <remarks>
/// <para>
/// A contagem inicial é o número de donos. <see cref="AddRef"/> acrescenta um dono.
/// Cada dono chama <see cref="Release"/> ou <see cref="Dispose"/> exatamente uma vez.
/// O owner interno é descartado quando a contagem chega a zero.
/// </para>
/// <para>
/// Esta instância assume a propriedade exclusiva do owner interno informado no construtor.
/// Não descarte esse owner por fora.
/// </para>
/// </remarks>
public sealed class RefCountedMemoryOwner : IMemoryOwner<byte>
{
    private readonly IMemoryOwner<byte> _inner;
    private int _refCount;

    /// <summary>
    /// Passa a controlar o tempo de vida de <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">Buffer compartilhado. Esta instância passa a ser a única a descartá-lo.</param>
    /// <param name="initialRefCount">Quantidade de donos já existentes. Precisa ser positiva.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialRefCount"/> é menor que um.</exception>
    public RefCountedMemoryOwner(IMemoryOwner<byte> inner, int initialRefCount)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialRefCount);

        _inner = inner;
        _refCount = initialRefCount;
    }

    /// <summary>
    /// Fatia compartilhada do buffer interno.
    /// </summary>
    /// <exception cref="ObjectDisposedException">A contagem de referências já chegou a zero.</exception>
    public Memory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _refCount) <= 0, this);
            return _inner.Memory;
        }
    }

    /// <summary>
    /// Registra mais um dono do buffer.
    /// </summary>
    /// <remarks>Zero allocation. Seguro para chamada no hot path. Não chame depois que a contagem chegou a zero.</remarks>
    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    /// Libera um dono. O último a liberar descarta o owner interno.
    /// </summary>
    /// <remarks>Zero allocation. Seguro para chamada no hot path.</remarks>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            _inner.Dispose();
        }
    }

    /// <summary>
    /// Libera um dono. Equivale a <see cref="Release"/>.
    /// </summary>
    public void Dispose() => Release();
}
