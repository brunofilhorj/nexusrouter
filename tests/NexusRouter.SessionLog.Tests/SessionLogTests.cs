using System.Text;
using System.Buffers;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging.Abstractions;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;
using NexusRouter.SessionLog;

namespace NexusRouter.SessionLog.Tests;

public sealed class SessionLogTests
{
    [Fact]
    public async Task Append_ThenRead_ReturnsTheOriginalFrame()
    {
        await using SessionLogManager manager = CreateManager(out string directory);
        try
        {
            var stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            byte[] frame = "8=FIX.4.4\u000135=A\u0001"u8.ToArray();
            AppendResult appended = stream.Append(frame, inSeq: 7, ingressTs: 99);
            Assert.True(appended.IsOk);
            Assert.Equal(7, stream.Committed);

            var buffer = new ArrayBufferWriter<byte>();
            Assert.True(manager.Reader.TryRead(stream.StreamId, epoch: 0, inSeq: 7, buffer));
            Assert.Equal(frame, buffer.WrittenSpan.ToArray());
            Assert.False(manager.Reader.TryRead(stream.StreamId, epoch: 0, inSeq: 8, buffer));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Recover_TruncatedTail_DropsThePartialRecord()
    {
        string directory = NewDirectory();
        byte[] frame = "hello"u8.ToArray();
        ushort streamId;
        await using (SessionLogManager manager = CreateManager(directory))
        {
            var stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            streamId = stream.StreamId;
            Assert.True(stream.Append(frame, 1, 1).IsOk);
            Assert.True(stream.Append(frame, 2, 2).IsOk);
            manager.SimulateCrash();
        }

        string log = FindLog(directory);
        int record = LogSegment.HeaderSize + frame.Length;
        using (var file = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Position = record * 2;
            file.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3, 4 });
        }

        await using SessionLogManager reopened = CreateManager(directory);
        try
        {
            var stream = (SessionLogStream)reopened.OpenStream("EXCH_A");
            Assert.Equal(streamId, stream.StreamId);
            Assert.Equal(2, stream.Committed);
            var buffer = new ArrayBufferWriter<byte>();
            Assert.True(reopened.Reader.TryRead(streamId, 0, 2, buffer));
            Assert.False(reopened.Reader.TryRead(streamId, 0, 3, buffer));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Recover_InvalidCrcAtEnd_DropsThatRecord()
    {
        string directory = NewDirectory();
        byte[] frame = "hello"u8.ToArray();
        await using (SessionLogManager manager = CreateManager(directory))
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            Assert.True(stream.Append(frame, 1, 1).IsOk);
            Assert.True(stream.Append(frame, 2, 2).IsOk);
            manager.SimulateCrash();
        }

        string log = FindLog(directory);
        int record = LogSegment.HeaderSize + frame.Length;
        using (var file = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            file.Position = record + 20;
            int value = file.ReadByte();
            file.Position = record + 20;
            file.WriteByte((byte)(value ^ 0xFF));
        }

        await using SessionLogManager reopened = CreateManager(directory);
        try
        {
            SessionLogStream stream = (SessionLogStream)reopened.OpenStream("EXCH_A");
            Assert.Equal(1, stream.Committed);
            var buffer = new ArrayBufferWriter<byte>();
            Assert.True(reopened.Reader.TryRead(stream.StreamId, 0, 1, buffer));
            Assert.False(reopened.Reader.TryRead(stream.StreamId, 0, 2, buffer));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task ClientIndex_AfterManagerStops_IgnoresPublishInsteadOfTouchingUnmappedMemory()
    {
        string directory = NewDirectory();
        IClientIndex index;
        await using (SessionLogManager manager = CreateManager(directory, margin: 0))
        {
            index = manager.OpenClient("FIX_OUT", "OMS1").Index;
            _ = index.ReserveSeq();
        }

        try
        {
            long seq = index.ReserveSeq();
            index.PublishAdmin(seq);

            Assert.Equal(IndexReadResult.Missing, index.TryGet(seq, out _));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task UncleanRestart_OutboundSeqJumpsAndDoesNotRegress()
    {
        string directory = NewDirectory();
        await using (SessionLogManager manager = CreateManager(directory, margin: 10))
        {
            ClientBinding client = manager.OpenClient("FIX_OUT", "OMS1");
            long first = client.Index.ReserveSeq();
            long second = client.Index.ReserveSeq();
            client.Index.PublishAdmin(first);
            client.Index.PublishAdmin(second);
            manager.FlushNow();
            manager.SimulateCrash();
        }

        await using SessionLogManager reopened = CreateManager(directory, margin: 10);
        try
        {
            ClientBinding client = reopened.OpenClient("FIX_OUT", "OMS1");
            long next = client.Index.ReserveSeq();
            Assert.True(next > 2);
            Assert.Equal(2 + 1 + 10, next);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task ClientIndex_PublishOutOfOrder_UnpublishedSlotStaysNotYet()
    {
        await using SessionLogManager manager = CreateManager(out string directory);
        try
        {
            IClientIndex index = manager.OpenClient("FIX_OUT", "OMS1").Index;
            long first = index.ReserveSeq();
            long second = index.ReserveSeq();
            index.PublishAdmin(second);
            Assert.Equal(IndexReadResult.NotYet, index.TryGet(first, out _));
            Assert.Equal(IndexReadResult.Published, index.TryGet(second, out IndexEntry published));
            Assert.Equal(2, published.Kind);

            index.PublishAdmin(first);
            Assert.Equal(IndexReadResult.Published, index.TryGet(first, out _));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task PublishApp_StoresTheIngressPosition()
    {
        await using SessionLogManager manager = CreateManager(out string directory);
        try
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            AppendResult appended = stream.Append("frame"u8, 4, 10);
            ClientBinding client = manager.OpenClient("FIX_OUT", "OMS1");
            long seq = client.Index.ReserveSeq();
            client.Index.PublishApp(seq, appended.Ref);
            Assert.Equal(IndexReadResult.Published, client.Index.TryGet(seq, out IndexEntry entry));
            Assert.Equal(1, entry.Kind);
            Assert.Equal(stream.StreamId, entry.Stream);
            Assert.Equal(0u, entry.Segment);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task TryReadAt_WhenSlotPointsAtARecord_CopiesTheOriginalFrame()
    {
        await using SessionLogManager manager = CreateManager(out string directory);
        try
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            _ = stream.Append("first"u8, 4, 10);
            AppendResult appended = stream.Append("second"u8, 5, 11);
            ClientBinding client = manager.OpenClient("FIX_OUT", "OMS1");
            long seq = client.Index.ReserveSeq();
            client.Index.PublishApp(seq, appended.Ref);
            Assert.Equal(IndexReadResult.Published, client.Index.TryGet(seq, out IndexEntry entry));

            var buffer = new ArrayBufferWriter<byte>();
            Assert.True(manager.Reader.TryReadAt(entry.Stream, entry.Segment, entry.Offset, buffer));
            Assert.Equal("second", Encoding.ASCII.GetString(buffer.WrittenSpan));
            Assert.False(manager.Reader.TryReadAt(entry.Stream, entry.Segment, entry.Offset + 1, new ArrayBufferWriter<byte>()));
            Assert.False(manager.Reader.TryReadAt(entry.Stream, 99, entry.Offset, new ArrayBufferWriter<byte>()));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Retention_DeletesOldDays_KeepsState()
    {
        string directory = NewDirectory();
        string oldDay = Path.Combine(directory, "streams", "EXCH_A", "20200101-e000");
        Directory.CreateDirectory(oldDay);
        await File.WriteAllBytesAsync(Path.Combine(oldDay, "00000000.log"), [1]);
        string client = Path.Combine(directory, "clients", "FIX_OUT", "OMS1");
        Directory.CreateDirectory(client);
        await File.WriteAllTextAsync(Path.Combine(client, "state.json"), """{"nextOut":3,"nextInExpected":1,"epoch":0}""");
        await File.WriteAllBytesAsync(Path.Combine(client, "20200101-e000.cidx"), [1]);
        string cursor = Path.Combine(directory, "brokers", "KAFKA", "cursor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursor)!);
        await File.WriteAllTextAsync(cursor, """{"nextOut":1,"nextInExpected":1,"epoch":0}""");

        await using SessionLogManager manager = CreateManager(directory, retentionDays: 5);
        try
        {
            Assert.False(Directory.Exists(oldDay));
            Assert.False(File.Exists(Path.Combine(client, "20200101-e000.cidx")));
            Assert.True(File.Exists(Path.Combine(client, "state.json")));
            Assert.True(File.Exists(cursor));
            _ = manager;
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task DailyRollover_UsesConfiguredTimeZoneAndStartsEmptyStorageEpoch()
    {
        string directory = NewDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 12, 2, 59, 30, TimeSpan.Zero));
        var manager = new SessionLogManager(
            Options(directory, margin: 0),
            NullLogger<SessionLogManager>.Instance,
            clock);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            ClientBinding client = manager.OpenClient("FIX_OUT", "OMS1");
            Assert.True(stream.Append("previous-day"u8, 1, 1).IsOk);
            long oldClientSequence = client.Index.ReserveSeq();
            client.Index.PublishAdmin(oldClientSequence);

            clock.Advance(TimeSpan.FromMinutes(2));
            await WaitUntilAsync(() => client.State.Epoch == 1);

            Assert.Equal(1, client.Index.NextOut);
            Assert.Equal(1, client.State.NextInExpected);
            string clientDirectory = Path.Combine(directory, "clients", "FIX_OUT", "OMS1");
            Assert.True(
                File.Exists(Path.Combine(clientDirectory, "20261112-e001.cidx")),
                string.Join(", ", Directory.GetFiles(clientDirectory).Select(Path.GetFileName)));

            AppendResult currentDay = stream.Append("current-day"u8, 1, 2);
            Assert.True(currentDay.IsOk);
            Assert.Equal(1, currentDay.Ref.Epoch);
            Assert.Equal(1, stream.Committed);
            Assert.True(Directory.Exists(Path.Combine(directory, "streams", "EXCH_A", "20261112-e001")));

            var previous = new ArrayBufferWriter<byte>();
            Assert.True(manager.Reader.TryRead(stream.StreamId, epoch: 0, inSeq: 1, previous));
            Assert.Equal("previous-day", Encoding.ASCII.GetString(previous.WrittenSpan));
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            Delete(directory);
        }
    }

    [Fact]
    public async Task StreamLogon_AfterB3Rollover_OpensEpochAndResetsBothSequences()
    {
        string directory = NewDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 12, 1, 59, 30, TimeSpan.Zero));
        var manager = new SessionLogManager(
            Options(directory, margin: 0),
            NullLogger<SessionLogManager>.Instance,
            clock);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            IConsumerState state = manager.OpenStreamState("EXCH_A");
            SessionLogLogonState initial = manager.PrepareStreamLogon("EXCH_A");
            Assert.True(initial.ResetSeqNumFlag);
            manager.SetStreamNextOut("EXCH_A", 5);
            manager.CompleteStreamLogon("EXCH_A");
            state.NextInExpected = 8;
            Assert.True(stream.Append("previous-day"u8, 1, 1).IsOk);

            clock.Advance(TimeSpan.FromMinutes(2));

            SessionLogLogonState nextDay = manager.PrepareStreamLogon("EXCH_A");
            Assert.True(nextDay.ResetSeqNumFlag);
            Assert.Equal(1, nextDay.NextOut);
            Assert.Equal(1, state.NextInExpected);
            Assert.Equal(2, state.Epoch);
            Assert.Equal(2, stream.Epoch);

            manager.SetStreamNextOut("EXCH_A", 2);
            manager.CompleteStreamLogon("EXCH_A");
            Assert.True(stream.Append("current-day"u8, 1, 2).IsOk);
            SessionLogLogonState restart = manager.PrepareStreamLogon("EXCH_A");
            Assert.False(restart.ResetSeqNumFlag);
            Assert.Equal(2, restart.NextOut);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            Delete(directory);
        }
    }

    [Fact]
    public async Task Restart_SameTradingDay_ReusesTheExistingStorageEpoch()
    {
        string directory = NewDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        var options = Options(directory, margin: 0);

        var first = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance, clock)
        {
            PauseMaintenance = true,
        };
        await first.StartAsync(CancellationToken.None);
        ISessionLogStream initialStream = first.OpenStream("EXCH_A");
        ClientBinding initialClient = first.OpenClient("FIX_OUT", "OMS1");
        Assert.True(initialStream.Append("first"u8, 1, 1).IsOk);
        long firstClientSequence = initialClient.Index.ReserveSeq();
        initialClient.Index.PublishAdmin(firstClientSequence);
        await first.StopAsync(CancellationToken.None);

        var reopened = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance, clock)
        {
            PauseMaintenance = true,
        };
        await reopened.StartAsync(CancellationToken.None);
        try
        {
            ISessionLogStream stream = reopened.OpenStream("EXCH_A");
            ClientBinding client = reopened.OpenClient("FIX_OUT", "OMS1");
            AppendResult appended = stream.Append("second"u8, 2, 2);

            Assert.True(appended.IsOk);
            Assert.Equal(0, appended.Ref.Epoch);
            Assert.Equal(2, client.Index.ReserveSeq());
            Assert.Single(Directory.GetDirectories(Path.Combine(directory, "streams", "EXCH_A")));
            Assert.True(ClientIndex.TryGetLatestIndex(
                Path.Combine(directory, "clients", "FIX_OUT", "OMS1"),
                out DateOnly day,
                out int epoch));
            Assert.Equal(new DateOnly(2026, 11, 12), day);
            Assert.Equal(0, epoch);
        }
        finally
        {
            await reopened.StopAsync(CancellationToken.None);
            Delete(directory);
        }
    }

    [Fact]
    public async Task StreamLogon_RestartSameTradingDay_ContinuesSequenceWithoutAnotherReset()
    {
        string directory = NewDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        var options = Options(directory, margin: 0);

        var first = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance, clock)
        {
            PauseMaintenance = true,
        };
        await first.StartAsync(CancellationToken.None);
        SessionLogLogonState firstLogon = first.PrepareStreamLogon("EXCH_A");
        Assert.True(firstLogon.ResetSeqNumFlag);
        Assert.Equal(1, firstLogon.NextOut);
        first.SetStreamNextOut("EXCH_A", 4);
        first.OpenStreamState("EXCH_A").NextInExpected = 7;
        first.CompleteStreamLogon("EXCH_A");
        Assert.True(first.OpenStream("EXCH_A").Append("message"u8, 1, 1).IsOk);
        first.FlushNow();
        await first.StopAsync(CancellationToken.None);

        var reopened = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance, clock)
        {
            PauseMaintenance = true,
        };
        await reopened.StartAsync(CancellationToken.None);
        try
        {
            SessionLogLogonState nextLogon = reopened.PrepareStreamLogon("EXCH_A");

            Assert.False(nextLogon.ResetSeqNumFlag);
            Assert.Equal(4, nextLogon.NextOut);
            Assert.Equal(7, reopened.OpenStreamState("EXCH_A").NextInExpected);
            Assert.Equal(1, reopened.OpenStream("EXCH_A").Epoch);
        }
        finally
        {
            await reopened.StopAsync(CancellationToken.None);
            Delete(directory);
        }
    }

    [Fact]
    public async Task StreamLogon_UncleanSameDayRestart_AdvancesBeyondPersistedSequence()
    {
        string directory = NewDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        var options = Options(directory, margin: 10_000);
        var first = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance, clock)
        {
            PauseMaintenance = true,
        };
        await first.StartAsync(CancellationToken.None);
        SessionLogLogonState firstLogon = first.PrepareStreamLogon("EXCH_A");
        Assert.True(firstLogon.ResetSeqNumFlag);
        first.SetStreamNextOut("EXCH_A", 4);
        first.CompleteStreamLogon("EXCH_A");
        Assert.True(first.OpenStream("EXCH_A").Append("message"u8, 1, 1).IsOk);
        first.FlushNow();
        first.SimulateCrash();

        var reopened = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance, clock)
        {
            PauseMaintenance = true,
        };
        await reopened.StartAsync(CancellationToken.None);
        try
        {
            SessionLogLogonState nextLogon = reopened.PrepareStreamLogon("EXCH_A");

            Assert.False(nextLogon.ResetSeqNumFlag);
            Assert.Equal(10_004, nextLogon.NextOut);
        }
        finally
        {
            await reopened.StopAsync(CancellationToken.None);
            Delete(directory);
        }
    }

    [Fact]
    public async Task SundayToMonday_DoesNotResetSequencesAndRetentionUsesB3TradingDays()
    {
        string directory = NewDirectory();
        string streamDirectory = Path.Combine(directory, "streams", "EXCH_A");
        Directory.CreateDirectory(Path.Combine(streamDirectory, "20261111-e000"));
        Directory.CreateDirectory(Path.Combine(streamDirectory, "20261112-e000"));
        string clientDirectory = Path.Combine(directory, "clients", "FIX_OUT", "OMS1");
        Directory.CreateDirectory(clientDirectory);
        await File.WriteAllBytesAsync(Path.Combine(clientDirectory, "20261111-e000.cidx"), new byte[128 * 16]);
        await File.WriteAllBytesAsync(Path.Combine(clientDirectory, "20261112-e000.cidx"), new byte[128 * 16]);

        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 16, 1, 59, 30, TimeSpan.Zero));
        var manager = new SessionLogManager(
            Options(directory, margin: 0, retentionDays: 1),
            NullLogger<SessionLogManager>.Instance,
            clock);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(Directory.Exists(Path.Combine(streamDirectory, "20261111-e000")));
            Assert.True(Directory.Exists(Path.Combine(streamDirectory, "20261112-e000")));
            Assert.False(File.Exists(Path.Combine(clientDirectory, "20261111-e000.cidx")));
            Assert.True(File.Exists(Path.Combine(clientDirectory, "20261112-e000.cidx")));

            SessionLogLogonState firstLogon = manager.PrepareStreamLogon("EXCH_A");
            Assert.True(firstLogon.ResetSeqNumFlag);
            manager.CompleteStreamLogon("EXCH_A");
            manager.SetStreamNextOut("EXCH_A", 9);
            IConsumerState state = manager.OpenStreamState("EXCH_A");
            state.NextInExpected = 3;
            long epoch = state.Epoch;

            clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(150);
            SessionLogLogonState mondayLogon = manager.PrepareStreamLogon("EXCH_A");

            Assert.False(mondayLogon.ResetSeqNumFlag);
            Assert.Equal(9, mondayLogon.NextOut);
            Assert.Equal(3, state.NextInExpected);
            Assert.Equal(epoch, state.Epoch);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            Delete(directory);
        }
    }

    [Fact]
    public void Validate_UnknownDayRolloverTimeZone_RejectsStartupOptions()
    {
        var options = new SessionLogOptions { DayRolloverTimeZone = "not-a-time-zone" };

        Microsoft.Extensions.Options.ValidateOptionsResult result = options.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("DayRolloverTimeZone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Append_WhenIoFails_ReturnsFailedAndDegrades()
    {
        await using SessionLogManager manager = CreateManager(out string directory);
        long failures = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == SessionLogMetrics.MeterName && instrument.Name == "sessionlog_append_failed_total")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => failures += value);
        listener.Start();
        try
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            stream.ArmIoFailure();
            AppendResult failed = stream.Append("x"u8, 1, 1);
            Assert.False(failed.IsOk);
            Assert.True(stream.IsDegraded);
            Assert.False(stream.Append("y"u8, 2, 2).IsOk);
            listener.RecordObservableInstruments();
            Assert.True(failures >= 1);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Append_WhenSegmentIsFullAndNextIsNotReady_RotatesSynchronously()
    {
        string directory = NewDirectory();
        var options = Options(directory, margin: 0);
        var manager = new SessionLogManager(options, NullLogger<SessionLogManager>.Instance)
        {
            PauseMaintenance = true,
        };
        await manager.StartAsync(CancellationToken.None);
        try
        {
            SessionLogStream stream = (SessionLogStream)manager.OpenStream("EXCH_A");
            byte[] frame = new byte[200_000];
            int appended = 0;
            for (int seq = 1; seq <= 8; seq++)
            {
                if (!stream.Append(frame, seq, seq).IsOk)
                {
                    break;
                }

                appended++;
            }

            Assert.True(appended >= 2);
            Assert.True(stream.SyncRotationCount >= 1);
        }
        finally
        {
            await manager.DisposeAsync();
            Delete(directory);
        }
    }

    private static SessionLogManager CreateManager(out string directory, int margin = 0)
    {
        directory = NewDirectory();
        return CreateManager(directory, margin);
    }

    private static SessionLogManager CreateManager(string directory, int margin = 0, int retentionDays = 5)
    {
        var manager = new SessionLogManager(Options(directory, margin, retentionDays), NullLogger<SessionLogManager>.Instance)
        {
            PauseMaintenance = true,
        };
        manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return manager;
    }

    private static SessionLogOptions Options(string directory, int margin, int retentionDays = 5) =>
        new()
        {
            Directory = directory,
            SegmentSizeMB = 1,
            IndexSlotsPerSegment = 128,
            UncleanRestartSeqMargin = margin,
            FlushMode = margin == 0 ? SessionLogFlushMode.PerMessage : SessionLogFlushMode.Interval,
            FlushIntervalMs = 60_000,
            RetentionTradingDays = retentionDays,
            CatchUpIdleMs = 20,
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected session-log state was not reached.");
    }

    private static string FindLog(string directory)
    {
        string session = Path.Combine(directory, "streams", "EXCH_A");
        return Directory.GetFiles(session, "*.log", SearchOption.AllDirectories).Single();
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "nexusrouter-slog-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string directory)
    {
        // O manager (await using) só libera os arquivos mapeados ao sair do método, depois deste finally.
        // No Windows, apagar arquivo mapeado lança IOException; a limpeza vira best-effort e o resto fica no %TEMP%.
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _utcTicks;

        public AdjustableTimeProvider(DateTimeOffset utcNow) => _utcTicks = utcNow.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }
}
