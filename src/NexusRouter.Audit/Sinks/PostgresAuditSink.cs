using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Models;

using Npgsql;
using NpgsqlTypes;

namespace NexusRouter.Audit.Sinks;

/// <summary>
/// Grava o lote em <c>ingress_fix_messages</c> com COPY binário.
/// </summary>
/// <remarks>
/// Não cria schema. A tabela vem de <c>docker/postgres/init.sql</c> ou da migração do ambiente.
/// Falha de um lote é registrada no log e não segue para o writer, para os outros sinks ainda rodarem.
/// </remarks>
public sealed class PostgresAuditSink : IAuditSink
{
    private const string CopyCommand =
        """
        COPY ingress_fix_messages (
            ingress_ts, session_id, msg_type, cl_ord_id, msg_seq_num,
            delivery_mode, dispatcher_ids, routing_sequence, raw_fix,
            was_dropped, drop_reason)
        FROM STDIN (FORMAT BINARY)
        """;

    private readonly PostgresAuditSinkOptions _options;
    private readonly ILogger<PostgresAuditSink> _logger;
    private readonly RawFixRetention _rawFix;
    private NpgsqlDataSource? _dataSource;

    /// <summary>
    /// Guarda as options. A conexão abre em <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="options">Endereço do PostgreSQL, sem senha.</param>
    /// <param name="auditOptions">Modo de persistência do payload.</param>
    /// <param name="logger">Log da falha de COPY. A mensagem não inclui a senha.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    public PostgresAuditSink(
        PostgresAuditSinkOptions options,
        AuditOptions auditOptions,
        ILogger<PostgresAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(auditOptions);
        ArgumentNullException.ThrowIfNull(logger);
        options.EnsureValid();
        auditOptions.EnsureValid();
        _options = options;
        _logger = logger;
        _rawFix = new RawFixRetention(auditOptions);
    }

    /// <inheritdoc />
    public string Name => "Postgres";

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is not null)
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = _options.Host,
            Port = _options.Port,
            Database = _options.Database,
            Username = ReadRequired(_options.UsernameEnvVar),
            Password = ReadRequired(_options.PasswordEnvVar),
            SslMode = _options.SslMode,
            Timeout = _options.CommandTimeoutSeconds,
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT 1");
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await dataSource.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken cancellationToken)
    {
        if (batch.Length == 0)
        {
            return;
        }

        NpgsqlDataSource? dataSource = _dataSource;
        if (dataSource is null)
        {
            _logger.LogError("O sink PostgreSQL recebeu um lote antes de abrir a conexão.");
            return;
        }

        try
        {
            await CopyAsync(dataSource, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Falha ao gravar {Count} registros em ingress_fix_messages.", batch.Length);
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        NpgsqlDataSource? dataSource = Interlocked.Exchange(ref _dataSource, null);
        if (dataSource is null)
        {
            return;
        }

        await dataSource.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CopyAsync(
        NpgsqlDataSource dataSource,
        ReadOnlyMemory<AuditRecord> batch,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBinaryImporter importer = await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < batch.Length; i++)
        {
            AuditRecord record = batch.Span[i];
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            WriteRow(importer, record);
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteRow(NpgsqlBinaryImporter importer, AuditRecord record)
    {
        importer.Write(ResolveIngressTime(record), NpgsqlDbType.TimestampTz);
        importer.Write(ResolveSessionId(record), NpgsqlDbType.Text);
        WriteText(importer, record.MsgType);
        WriteText(importer, record.ClOrdId);
        if (record.MsgSeqNum is long sequence)
        {
            importer.Write(sequence, NpgsqlDbType.Bigint);
        }
        else
        {
            importer.WriteNull();
        }

        importer.Write(DeliveryModeText(record), NpgsqlDbType.Text);
        importer.Write(record.DispatcherIds ?? [], NpgsqlDbType.Array | NpgsqlDbType.Text);
        // O registro ainda não carrega o routing sequence do router. A coluna fica nula.
        importer.WriteNull();
        if (record.RawFix is { Length: > 0 } rawFix && _rawFix.ShouldStore(record))
        {
            importer.Write(rawFix, NpgsqlDbType.Bytea);
        }
        else
        {
            importer.WriteNull();
        }

        importer.Write(record.WasDropped, NpgsqlDbType.Boolean);
        WriteText(importer, record.DropReason);
    }

    /// <summary>
    /// Evento de ciclo de vida já tem instante UTC. Mensagem de mercado guarda TickCount64,
    /// que não cabe em timestamptz, então a coluna recebe o relógio do writer.
    /// </summary>
    private static DateTime ResolveIngressTime(AuditRecord record)
    {
        if (record.LifecycleEvent is not null)
        {
            return record.LifecycleEvent.Timestamp.UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static string ResolveSessionId(AuditRecord record)
    {
        if (record.SessionId.Value.Length > 0)
        {
            return record.SessionId.Value;
        }

        return record.LifecycleEvent?.SessionId.Value ?? string.Empty;
    }

    /// <summary>
    /// Registro só de ciclo de vida não passou por uma rota. A coluna é obrigatória e recebe "Lifecycle"
    /// para não parecer entrega PassThrough.
    /// </summary>
    private static string DeliveryModeText(AuditRecord record)
    {
        if (record.LifecycleEvent is not null && record.MsgType is null && record.RawFix is null)
        {
            return "Lifecycle";
        }

        return record.DeliveryMode.ToString();
    }

    private static void WriteText(NpgsqlBinaryImporter importer, string? value)
    {
        if (value is null)
        {
            importer.WriteNull();
            return;
        }

        importer.Write(value, NpgsqlDbType.Text);
    }

    private static string ReadRequired(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"A variável de ambiente '{variableName}' não está definida.");
        }

        return value;
    }
}
