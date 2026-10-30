using NexusRouter.Domain.Abstractions;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Atraso de reconexão com crescimento exponencial, teto e jitter uniforme.
/// </summary>
/// <remarks>
/// Fórmula, para a tentativa <c>n</c> (zero na primeira espera):
/// <code>
/// delay  = min(baseDelay × 2^n, maxDelay)
/// jitter ~ Uniform(−jitterFactor × delay, +jitterFactor × delay)
/// result = max(0, delay + jitter)
/// </code>
/// O teto é aplicado antes do jitter; o jitter pode ultrapassar <c>maxDelay</c>.
/// O cálculo não faz I/O nem espera.
/// </remarks>
public sealed class ExponentialBackoffReconnectPolicy : IReconnectPolicy
{
    /// <summary>
    /// Cria a política com os parâmetros de uma sessão.
    /// </summary>
    /// <param name="baseDelay">Atraso da tentativa zero, antes do teto e do jitter.</param>
    /// <param name="maxDelay">Teto do atraso exponencial, aplicado antes do jitter.</param>
    /// <param name="jitterFactor">Fração do atraso já limitado usada como meia-amplitude do jitter. Zero desliga o jitter.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="baseDelay"/> ou <paramref name="maxDelay"/> é negativo, ou
    /// <paramref name="jitterFactor"/> não é finito ou é negativo.
    /// </exception>
    public ExponentialBackoffReconnectPolicy(TimeSpan baseDelay, TimeSpan maxDelay, double jitterFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseDelay.Ticks, nameof(baseDelay));
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelay.Ticks, nameof(maxDelay));
        if (!double.IsFinite(jitterFactor) || jitterFactor < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterFactor),
                jitterFactor,
                "O fator de jitter deve ser finito e não negativo.");
        }

        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        JitterFactor = jitterFactor;
    }

    /// <summary>
    /// Atraso base da primeira espera.
    /// </summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>
    /// Teto aplicado ao crescimento exponencial, antes do jitter.
    /// </summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Fração simétrica do atraso calculado usada como amplitude do jitter.
    /// </summary>
    public double JitterFactor { get; }

    /// <summary>
    /// Calcula o atraso da tentativa informada.
    /// </summary>
    /// <param name="attemptNumber">Falhas já observadas nesta sequência. Zero é a primeira espera.</param>
    /// <returns>Atraso não negativo. Quem chama é que aplica a espera.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attemptNumber"/> é negativo.</exception>
    /// <remarks>
    /// <c>min(baseDelay × 2^attemptNumber, maxDelay)</c>, somado a um jitter uniforme
    /// no intervalo <c>[−jitterFactor, +jitterFactor]</c> desse atraso, com piso em zero.
    /// O sorteio usa <see cref="Random.Shared"/>.
    /// </remarks>
    public TimeSpan ComputeDelay(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptNumber);

        long cappedTicks = CapExponentialTicks(BaseDelay.Ticks, MaxDelay.Ticks, attemptNumber);
        if (JitterFactor == 0d || cappedTicks == 0L)
        {
            return TimeSpan.FromTicks(cappedTicks);
        }

        double unit = (Random.Shared.NextDouble() * 2d) - 1d;
        double jitteredTicks = cappedTicks * (1d + (unit * JitterFactor));
        if (jitteredTicks <= 0d)
        {
            return TimeSpan.Zero;
        }

        if (jitteredTicks >= TimeSpan.MaxValue.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks((long)jitteredTicks);
    }

    private static long CapExponentialTicks(long baseTicks, long maxTicks, int attemptNumber)
    {
        if (baseTicks == 0L || maxTicks == 0L)
        {
            return 0L;
        }

        long ticks = baseTicks;
        for (var attempt = 0; attempt < attemptNumber; attempt++)
        {
            if (ticks >= maxTicks || ticks > long.MaxValue / 2L)
            {
                return maxTicks;
            }

            ticks *= 2L;
            if (ticks >= maxTicks)
            {
                return maxTicks;
            }
        }

        return ticks < maxTicks ? ticks : maxTicks;
    }
}
