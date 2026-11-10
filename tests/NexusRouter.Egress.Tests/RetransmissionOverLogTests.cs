using NexusRouter.Domain.Models;
using NexusRouter.Egress.Fix;

using static NexusRouter.Egress.Tests.FixWire;

namespace NexusRouter.Egress.Tests;

/// <summary>
/// T-48: retransmissão FIX sobre o log de sessão, ponta a ponta (acceptor real, log real, socket real).
/// Os cenários que já têm teste próprio ficam onde estão: índice fora de ordem (<c>SessionLogTests</c>, <c>ResendPlannerTests</c>),
/// restart no broker (<c>BrokerRedeliveryTests</c>) e marca-d'água do cursor (<c>BrokerCursorTrackerTests</c>).
/// </summary>
public sealed class RetransmissionOverLogTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    // Passo 1: cada cliente recebe só o que o índice dele aponta.
    [Fact]
    public async Task ResendRequest_WithTwoClientsOnTheSameAcceptor_ReturnsOnlyTheMessagesOfEachIndex()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1");

            // OMS1 conhecia A1; RISK1 só passa a existir no Logon, depois de A1.
            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("2", Field(await oms.ReadFrameAsync(), 34));
            using FixPeer risk = await FixPeer.ConnectAsync(rig, "RISK1");
            Assert.Equal("1", Field(await risk.ReadFrameAsync(), 34));

            rig.Dispatch(rig.Append("B1"));
            byte[] omsLive = await oms.ReadFrameAsync();
            byte[] riskLive = await risk.ReadFrameAsync();
            Assert.Equal("3", Field(omsLive, 34));
            Assert.Equal("2", Field(riskLive, 34));

            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 1, end: 0));
            await risk.SendAsync(ResendRequest("RISK1", msgSeqNum: 2, begin: 1, end: 0));

            byte[] omsFirst = await oms.ReadFrameAsync();
            Assert.Equal("8", Field(omsFirst, 35));
            Assert.Equal("1", Field(omsFirst, 34));
            Assert.Equal("A1", Field(omsFirst, 11));
            Assert.Equal("Y", Field(omsFirst, 43));
            byte[] omsLogonGap = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(omsLogonGap, 35));
            Assert.Equal("2", Field(omsLogonGap, 34));
            Assert.Equal("3", Field(omsLogonGap, 36));
            byte[] omsLast = await oms.ReadFrameAsync();
            Assert.Equal("3", Field(omsLast, 34));
            Assert.Equal("B1", Field(omsLast, 11));
            await oms.ExpectSilenceAsync(Quiet);

            byte[] riskLogonGap = await risk.ReadFrameAsync();
            Assert.Equal("4", Field(riskLogonGap, 35));
            Assert.Equal("1", Field(riskLogonGap, 34));
            Assert.Equal("2", Field(riskLogonGap, 36));
            byte[] riskOnly = await risk.ReadFrameAsync();
            Assert.Equal("2", Field(riskOnly, 34));
            Assert.Equal("B1", Field(riskOnly, 11));
            await risk.ExpectSilenceAsync(Quiet);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 3: queda sem marcador clean. O seq salta, não regride, e o intervalo pulado volta como gap-fill.
    [Fact]
    public async Task ResendRequest_AfterUncleanRestart_SkipsTheOutboundSeqAndReturnsTheJumpedRangeAsGapFill()
    {
        string directory = TempDirectory();
        try
        {
            AcceptorRig first = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"], uncleanMargin: 5);
            await first.AppendAndDispatchAsync("A1", "A2");
            await first.StopAsUncleanAsync();

            await using AcceptorRig restarted = await AcceptorRig.StartAsync(directory, uncleanMargin: 5);
            using FixPeer oms = await FixPeer.ConnectAsync(restarted, "OMS1");
            byte[] logon = await oms.ReadFrameAsync();
            long logonSeq = long.Parse(Field(logon, 34));
            Assert.True(logonSeq > 2, $"O seq de saída regrediu ou repetiu: {logonSeq}.");
            Assert.Equal(3 + 5, logonSeq);

            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 1, end: 0));

            byte[] a1 = await oms.ReadFrameAsync();
            Assert.Equal("1", Field(a1, 34));
            Assert.Equal("A1", Field(a1, 11));
            byte[] a2 = await oms.ReadFrameAsync();
            Assert.Equal("2", Field(a2, 34));
            Assert.Equal("A2", Field(a2, 11));
            byte[] gap = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(gap, 35));
            Assert.Equal("Y", Field(gap, 123));
            Assert.Equal("3", Field(gap, 34));
            Assert.Equal((logonSeq + 1).ToString(), Field(gap, 36));
            await oms.ExpectSilenceAsync(Quiet);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4 (lado da sessão): slot reservado e publicado depois do vizinho não vira buraco.
    [Fact]
    public async Task ResendRequest_WhenAnEarlierSlotIsPublishedAfterItsNeighbour_ReplaysBothWithoutGapFill()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"], slotTimeout: TimeSpan.FromSeconds(10));
            ClientBinding binding = rig.Client("OMS1");
            AcceptorRig.Appended earlier = rig.Append("S0");
            long reserved = binding.Index.ReserveSeq();
            Assert.Equal(1, reserved);
            rig.Dispatch(rig.Append("S1"));
            await AcceptorRig.WaitPublishedAsync(binding.Index, 2);

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("3", Field(await oms.ReadFrameAsync(), 34));
            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 1, end: 2));

            Task<byte[]> pending = oms.ReadFrameAsync();
            await Task.Delay(250);
            Assert.False(pending.IsCompleted, "O reenvio não deveria andar com o slot 1 ainda sem publicação.");

            binding.Index.PublishApp(reserved, earlier.Ref);

            byte[] s0 = await pending;
            Assert.Equal("8", Field(s0, 35));
            Assert.Equal("1", Field(s0, 34));
            Assert.Equal("S0", Field(s0, 11));
            byte[] s1 = await oms.ReadFrameAsync();
            Assert.Equal("2", Field(s1, 34));
            Assert.Equal("S1", Field(s1, 11));
            await oms.ExpectSilenceAsync(Quiet);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4a, modo 1: 141=Y abre época nova e a anterior deixa de ser reenviável.
    [Fact]
    public async Task Logon_WithResetSeqNumFlag_OpensANewEpochAndTheOldOneIsNoLongerResendable()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1", "A2");

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", msgSeqNum: 1, reset: true);
            byte[] logon = await oms.ReadFrameAsync();
            Assert.Equal("A", Field(logon, 35));
            Assert.Equal("1", Field(logon, 34));
            Assert.Equal("Y", Field(logon, 141));

            rig.Dispatch(rig.Append("A3"));
            byte[] live = await oms.ReadFrameAsync();
            Assert.Equal("2", Field(live, 34));
            Assert.Equal("A3", Field(live, 11));

            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 1, end: 0));

            byte[] logonGap = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(logonGap, 35));
            Assert.Equal("1", Field(logonGap, 34));
            Assert.Equal("2", Field(logonGap, 36));
            byte[] replay = await oms.ReadFrameAsync();
            Assert.Equal("2", Field(replay, 34));
            Assert.Equal("A3", Field(replay, 11));
            Assert.Equal("Y", Field(replay, 43));
            await oms.ExpectSilenceAsync(Quiet);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Logon_WithResetSeqNumFlagWhenNotAllowed_SendsLogout()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { AllowResetSeqNumFlag = false, LogoutTimeoutSeconds = 1 });

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", msgSeqNum: 1, reset: true);
            byte[] logout = await oms.ReadFrameAsync();
            Assert.Equal("5", Field(logout, 35));
            Assert.Equal("ResetSeqNumFlag not allowed", Field(logout, 58));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4a, modo 2: 789 = N reenvia N..M-1 com PossDupFlag=Y e cobre o seq do Logon com gap-fill.
    [Fact]
    public async Task Logon_WithNextExpectedMsgSeqNum_ResendsFromNThroughMMinusOneThenGapFillsTheLogonSeq()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1", "A2", "A3");

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", nextExpected: 2);
            byte[] logon = await oms.ReadFrameAsync();
            Assert.Equal("A", Field(logon, 35));
            Assert.Equal("4", Field(logon, 34));
            Assert.Equal("2", Field(logon, 789));

            byte[] a2 = await oms.ReadFrameAsync();
            Assert.Equal("2", Field(a2, 34));
            Assert.Equal("A2", Field(a2, 11));
            Assert.Equal("Y", Field(a2, 43));
            byte[] a3 = await oms.ReadFrameAsync();
            Assert.Equal("3", Field(a3, 34));
            Assert.Equal("A3", Field(a3, 11));
            Assert.Equal("Y", Field(a3, 43));
            byte[] gap = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(gap, 35));
            Assert.Equal("Y", Field(gap, 123));
            Assert.Equal("4", Field(gap, 34));
            Assert.Equal("5", Field(gap, 36));
            await oms.ExpectSilenceAsync(Quiet);

            rig.Dispatch(rig.Append("A4"));
            byte[] live = await oms.ReadFrameAsync();
            Assert.Equal("5", Field(live, 34));
            Assert.False(live.AsSpan().IndexOf("\u000143=Y\u0001"u8) >= 0, "Mensagem nova não leva PossDupFlag.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4a, modo 3: SkipToCurrent envia gap-fill e não reenvia aplicação.
    [Fact]
    public async Task Logon_WithSkipToCurrent_SendsGapFillAndDoesNotResendApplication()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { OnReconnectGap = OnReconnectGap.SkipToCurrent },
                knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1", "A2");

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", nextExpected: 1);
            byte[] logon = await oms.ReadFrameAsync();
            Assert.Equal("3", Field(logon, 34));
            byte[] gap = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(gap, 35));
            Assert.Equal("Y", Field(gap, 123));
            Assert.Equal("1", Field(gap, 34));
            Assert.Equal("4", Field(gap, 36));
            await oms.ExpectSilenceAsync(Quiet);

            rig.Dispatch(rig.Append("A3"));
            byte[] live = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(live, 34));
            Assert.Equal("A3", Field(live, 11));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4a, modo 4: 789 acima do que o router numerou recebe Logout e o router não adota o seq do cliente.
    [Fact]
    public async Task Logon_WithNextExpectedAboveNumbered_SendsLogoutAndDoesNotAdoptTheClientSeq()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { LogoutTimeoutSeconds = 1 },
                knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1", "A2");
            ClientBinding binding = rig.Client("OMS1");

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", nextExpected: 10);
            byte[] logout = await oms.ReadFrameAsync();
            Assert.Equal("5", Field(logout, 35));
            Assert.Equal("MsgSeqNum too high", Field(logout, 58));

            Assert.True(binding.Index.NextOut < 10, $"O router adotou o seq do cliente: NextOut {binding.Index.NextOut}.");
            Assert.Equal(1, binding.State.NextInExpected);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ResendRequest_WithBeginSeqNoAboveNumbered_SendsLogoutAndDoesNotAdoptTheClientSeq()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { LogoutTimeoutSeconds = 1 },
                knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1");
            ClientBinding binding = rig.Client("OMS1");

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("2", Field(await oms.ReadFrameAsync(), 34));
            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 2, begin: 99, end: 0));

            byte[] logout = await oms.ReadFrameAsync();
            Assert.Equal("5", Field(logout, 35));
            Assert.Equal("MsgSeqNum too high", Field(logout, 58));
            Assert.True(binding.Index.NextOut < 99);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4a: o Logout espera a confirmação até o timeout.
    [Fact]
    public async Task Logout_WaitsForTheClientConfirmationUntilTheTimeoutThenClosesTheSocket()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { LogoutTimeoutSeconds = 2 },
                knownClients: ["OMS1"]);

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", nextExpected: 10);
            Assert.Equal("5", Field(await oms.ReadFrameAsync(), 35));

            await oms.ExpectSilenceAsync(TimeSpan.FromMilliseconds(800));
            TimeSpan closedAfter = await oms.WaitClosedAsync(TimeSpan.FromSeconds(5));
            Assert.True(closedAfter >= TimeSpan.FromMilliseconds(800), $"Fechou cedo demais: {closedAfter.TotalMilliseconds:0} ms depois da janela.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Logout_WhenTheClientConfirms_ClosesBeforeTheTimeout()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { LogoutTimeoutSeconds = 10 },
                knownClients: ["OMS1"]);

            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1", nextExpected: 10);
            Assert.Equal("5", Field(await oms.ReadFrameAsync(), 35));
            await oms.SendAsync(Logout("OMS1", msgSeqNum: 2));

            TimeSpan closedAfter = await oms.WaitClosedAsync(TimeSpan.FromSeconds(5));
            Assert.True(closedAfter < TimeSpan.FromSeconds(5), $"Esperou o timeout inteiro: {closedAfter.TotalSeconds:0.0} s.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4b: 34 do cliente acima do esperado é aceito com aviso.
    [Fact]
    public async Task InboundSeq_WhenAboveExpected_IsAcceptedAndAdvancesTheExpectedSeq()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"]);
            ClientBinding binding = rig.Client("OMS1");
            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("1", Field(await oms.ReadFrameAsync(), 34));

            await oms.SendAsync(TestRequest("OMS1", msgSeqNum: 6, testReqId: "T6"));
            byte[] heartbeat = await oms.ReadFrameAsync();
            Assert.Equal("0", Field(heartbeat, 35));
            Assert.Equal("T6", Field(heartbeat, 112));
            Assert.Equal(7, binding.State.NextInExpected);

            await oms.SendAsync(TestRequest("OMS1", msgSeqNum: 7, testReqId: "T7"));
            Assert.Equal("T7", Field(await oms.ReadFrameAsync(), 112));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4b: menor com PossDupFlag=Y é ignorado.
    [Fact]
    public async Task InboundSeq_WhenBelowExpectedWithPossDup_IsIgnoredAndTheSessionStaysUp()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"]);
            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("1", Field(await oms.ReadFrameAsync(), 34));

            await oms.SendAsync(TestRequest("OMS1", msgSeqNum: 1, testReqId: "DUP", possDup: true));
            await oms.ExpectSilenceAsync(Quiet);

            await oms.SendAsync(TestRequest("OMS1", msgSeqNum: 2, testReqId: "OK"));
            Assert.Equal("OK", Field(await oms.ReadFrameAsync(), 112));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4b: menor sem PossDupFlag leva a Logout, mas o ResendRequest ainda é atendido.
    [Fact]
    public async Task InboundSeq_WhenBelowExpectedWithoutPossDup_SendsLogoutButStillServesResendRequest()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(
                directory,
                recovery: new AcceptorRecoveryOptions { LogoutTimeoutSeconds = 3 },
                knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1");
            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("2", Field(await oms.ReadFrameAsync(), 34));

            await oms.SendAsync(TestRequest("OMS1", msgSeqNum: 1, testReqId: "LOW"));
            byte[] logout = await oms.ReadFrameAsync();
            Assert.Equal("5", Field(logout, 35));
            Assert.Equal("MsgSeqNum too low", Field(logout, 58));

            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 1, begin: 1, end: 0));
            byte[] replay = await oms.ReadFrameAsync();
            Assert.Equal("1", Field(replay, 34));
            Assert.Equal("A1", Field(replay, 11));
            Assert.Equal("Y", Field(replay, 43));
            byte[] gap = await oms.ReadFrameAsync();
            Assert.Equal("4", Field(gap, 35));
            Assert.Equal("2", Field(gap, 34));
            Assert.Equal("4", Field(gap, 36));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // Passo 4b: ResendRequest com 34 acima do esperado é processado.
    [Fact]
    public async Task ResendRequest_WithInboundSeqAboveExpected_IsStillProcessed()
    {
        string directory = TempDirectory();
        try
        {
            await using AcceptorRig rig = await AcceptorRig.StartAsync(directory, knownClients: ["OMS1"]);
            await rig.AppendAndDispatchAsync("A1");
            using FixPeer oms = await FixPeer.ConnectAsync(rig, "OMS1");
            Assert.Equal("2", Field(await oms.ReadFrameAsync(), 34));

            await oms.SendAsync(ResendRequest("OMS1", msgSeqNum: 20, begin: 1, end: 1));

            byte[] replay = await oms.ReadFrameAsync();
            Assert.Equal("1", Field(replay, 34));
            Assert.Equal("A1", Field(replay, 11));
            Assert.Equal("Y", Field(replay, 43));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }
}
