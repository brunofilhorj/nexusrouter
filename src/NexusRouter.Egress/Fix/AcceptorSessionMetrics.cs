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

    /// <summary>
    /// Modo de replay resolvido em cada Logon aceito; <c>applied</c> distingue época nova de reconexão.
    /// </summary>
    internal static readonly Counter<long> ReplayMode = Meter.CreateCounter<long>(
        "acceptor_replay_mode_total",
        unit: "{logons}",
        description: "Accepted Logons by configured replay mode and whether the mode applied.");

    /// <summary>
    /// Logon recusado antes de criar arquivos no log de sessão. A tag <c>reason</c> vale <c>max_clients</c> ou <c>invalid_key</c>.
    /// </summary>
    internal static readonly Counter<long> ClientsRejected = Meter.CreateCounter<long>(
        "sessionlog_clients_rejected_total",
        unit: "{logons}",
        description: "Logons recusados por SenderCompID inválido ou por MaxClients.");
}
