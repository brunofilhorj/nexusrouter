using Microsoft.Extensions.ObjectPool;

using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Pooling;

/// <summary>
/// Pool de <see cref="DispatchPayload"/> para o router não alocar um envelope por mensagem.
/// </summary>
/// <remarks>
/// <see cref="Get"/> e <see cref="Return"/> são thread-safe. <see cref="Return"/> chama <see cref="DispatchPayload.Reset"/>
/// antes de reter a instância. Não devolva a mesma instância duas vezes sem um <see cref="Get"/> no meio.
/// </remarks>
public sealed class DispatchPayloadPool
{
    /// <summary>
    /// Quantidade máxima de payloads retidos quando o construtor não recebe outro limite.
    /// </summary>
    public const int DefaultMaximumRetained = 1024;

    private readonly ObjectPool<DispatchPayload> _pool;
    private readonly int _maximumRetained;

    /// <summary>
    /// Cria o pool com o limite de retenção informado.
    /// </summary>
    /// <param name="maximumRetained">Máximo de instâncias guardadas depois de <see cref="Return"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumRetained"/> é menor que um.</exception>
    public DispatchPayloadPool(int maximumRetained = DefaultMaximumRetained)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetained);

        DefaultObjectPoolProvider provider = new DefaultObjectPoolProvider
        {
            MaximumRetained = maximumRetained,
        };

        _pool = provider.Create(new DispatchPayloadPolicy());
        _maximumRetained = maximumRetained;
    }

    /// <summary>
    /// Máximo de instâncias que o pool retém.
    /// </summary>
    public int MaximumRetained => _maximumRetained;

    /// <summary>
    /// Obtém um payload limpo. Cria um novo quando o pool não tem instância retida.
    /// </summary>
    /// <returns>Payload pronto para preenchimento.</returns>
    public DispatchPayload Get()
    {
        return _pool.Get();
    }

    /// <summary>
    /// Devolve um payload ao pool depois de zerar os campos.
    /// </summary>
    /// <param name="payload">Instância obtida deste pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> é nulo.</exception>
    public void Return(DispatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _pool.Return(payload);
    }

    /// <summary>
    /// Aloca e devolve <paramref name="count"/> payloads para o regime estacionário não pagar o primeiro aluguel.
    /// </summary>
    /// <param name="count">Quantidade de instâncias a pré-criar. Zero não faz nada.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> é negativo ou maior que <see cref="MaximumRetained"/>.
    /// </exception>
    public void Prewarm(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _maximumRetained);

        if (count == 0)
        {
            return;
        }

        DispatchPayload[] rented = new DispatchPayload[count];
        for (int i = 0; i < count; i++)
        {
            rented[i] = _pool.Get();
        }

        for (int i = 0; i < count; i++)
        {
            _pool.Return(rented[i]);
        }
    }

    private sealed class DispatchPayloadPolicy : PooledObjectPolicy<DispatchPayload>
    {
        public override DispatchPayload Create()
        {
            return new DispatchPayload();
        }

        public override bool Return(DispatchPayload obj)
        {
            obj.Reset();
            return true;
        }
    }
}
