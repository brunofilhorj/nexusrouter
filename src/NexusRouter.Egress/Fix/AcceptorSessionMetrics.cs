using System.Diagnostics.Metrics;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Contadores da política de seqnum do acceptor.
/// </summary>
public static class AcceptorSessionMetrics
{
    /// <summary>
    /// Nome do <see cref="Meter"/> exportado pelo host.
    /// </summary>
    public const string MeterName = "NexusRouter.Egress";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// <c>34</c> recebido acima de <c>nextInExpected</c>.
    /// </summary>
    internal static readonly Counter<long> InSeqGap = Meter.CreateCounter<long>("acceptor_in_seq_gap_total");

    /// <summary>
    /// Cliente pediu um seq de saída que o router não numerou.
    /// </summary>
    internal static readonly Counter<long> ClientAhead = Meter.CreateCounter<long>("acceptor_client_ahead_total");
}
