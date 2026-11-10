using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Contadores da sessão FIX de entrada.
/// </summary>
public sealed class FixSessionMetrics : IDisposable
{
    /// <summary>
    /// Nome do meter exportado pelo host.
    /// </summary>
    public const string MeterName = "NexusRouter.Ingress";

    private readonly Meter _meter;
    private readonly Counter<long> _rejected;
    private int _disposed;

    /// <summary>
    /// Cria o meter e os contadores.
    /// </summary>
    public FixSessionMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _rejected = _meter.CreateCounter<long>(
            "ingress_session_reject_total",
            unit: "{frames}",
            description: "Frames do counterpart rejeitados por validacao de sessao (35=3).");
    }

    /// <summary>
    /// Conta um frame rejeitado.
    /// </summary>
    /// <param name="sessionId">Identificador lógico da sessão.</param>
    /// <param name="sessionRejectReason">SessionRejectReason (373) enviado no Reject.</param>
    /// <param name="refTagId">RefTagID (371) do campo que motivou a rejeição.</param>
    /// <remarks>Zero allocation: <see cref="TagList"/> guarda as tags na pilha.</remarks>
    public void RecordReject(string sessionId, int sessionRejectReason, int refTagId)
    {
        var tags = new TagList
        {
            { "session_id", sessionId },
            { "reason", sessionRejectReason },
            { "ref_tag_id", refTagId },
        };
        _rejected.Add(1, tags);
    }

    /// <summary>
    /// Libera o meter.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _meter.Dispose();
    }
}
