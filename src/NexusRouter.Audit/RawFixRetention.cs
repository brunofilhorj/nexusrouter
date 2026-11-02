using NexusRouter.Domain.Models;

namespace NexusRouter.Audit;

/// <summary>
/// Decide se um registro leva <see cref="AuditRecord.RawFix"/> para a coluna <c>raw_fix</c>.
/// </summary>
internal sealed class RawFixRetention
{
    private readonly AuditPersistMode _mode;
    private readonly int _keepEvery;
    private long _counter;

    /// <summary>
    /// Prepara a contagem da amostra a partir das options.
    /// </summary>
    /// <param name="options">Modo e taxa já validados.</param>
    public RawFixRetention(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _mode = options.PersistMode;
        _keepEvery = options.SampleRate <= 0
            ? 0
            : options.SampleRate >= 1
                ? 1
                : Math.Max(1, (int)Math.Round(1d / options.SampleRate));
    }

    /// <summary>
    /// Indica se este registro grava o payload bruto.
    /// </summary>
    /// <param name="record">Registro do lote.</param>
    /// <returns><see langword="false"/> quando não há bytes ou a política pede para omitir.</returns>
    public bool ShouldStore(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RawFix is not { Length: > 0 })
        {
            return false;
        }

        return _mode switch
        {
            AuditPersistMode.Full => true,
            AuditPersistMode.MetadataOnly => false,
            AuditPersistMode.Sampled => TakeSample(),
            _ => false,
        };
    }

    /// <summary>
    /// <see cref="AuditOptions.SampleRate"/> 0,5 guarda um a cada dois registros que têm payload.
    /// A contagem é do sink, não da mensagem de mercado.
    /// </summary>
    private bool TakeSample()
    {
        if (_keepEvery == 0)
        {
            return false;
        }

        if (_keepEvery == 1)
        {
            return true;
        }

        long index = Interlocked.Increment(ref _counter);
        return index % _keepEvery == 0;
    }
}
