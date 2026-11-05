# Implementation Plan — Correção dos defeitos A e B no DropCopy simulator

Fonte: `02-design.md` (revisão 2, aprovado). O trabalho corrige dois defeitos isolados em
`sim/backend`, sem tocar no NexusRouter e sem commit/push/PR (o coder commita localmente apenas
quando o fluxo pedir). Build/test da solução: `dotnet build sim/backend/DropCopySimulator.slnx` e
`dotnet test sim/backend/DropCopySimulator.slnx` (executar a partir da raiz `c:\repos\nexusrouter`).

Stack: .NET 10 / C# 14, `TreatWarningsAsErrors=true`, nullable on. Testes: xUnit puro — **o stack de
testes do `sim/` NÃO tem NSubstitute nem FluentAssertions** (ver `sim/backend/Directory.Packages.props`).
Nenhuma dependência nova pode ser introduzida. Por isso o teste do Defeito B usa um **test double de
`WebSocket` escrito à mão** (subclasse de `System.Net.WebSockets.WebSocket`) em vez do NSubstitute que
o design citava — mesma intenção (não propagar exceção), sem novo pacote. Essa é a única adaptação ao
design, e preserva o comportamento verificado.

As duas correções são independentes entre si (arquivos distintos, sem código compartilhado) e podem
ser implementadas em qualquer ordem; o plano as ordena A → B por conveniência.

- [ ] 1. Injetar `OrderFlowGenerator` no `FixAcceptorService` e ligar a sessão ao gerador via o evento
      `StateChanged` (Defeito A). Adicionar 4º parâmetro de construtor
      `OrderFlowGenerator generator` (com `ArgumentNullException.ThrowIfNull`, campo
      `private readonly OrderFlowGenerator _generator;`, doc XML). No `AcceptLoopAsync`, após
      `Volatile.Write(ref _activeSession, session)` e **antes** de `RunAsync`, registrar um handler
      nomeado em `session.StateChanged`: quando
      `string.Equals(e.State, nameof(AcceptorSessionState.Active), StringComparison.Ordinal)` →
      `_generator.AttachSession(session)`; quando `... Disconnected ...` → `_generator.AttachSession(null)`;
      qualquer outro estado é ignorado. Envolver cada `AttachSession` em `try/catch (ObjectDisposedException)`
      que ignora (log `Debug` sem exceção). Reescrever o bloco `finally` nesta ordem fixa:
      (1) `await session.DisposeAsync()`, (2) `session.StateChanged -= handler`,
      (3) `_generator.AttachSession(null)` incondicional (em `try/catch (ObjectDisposedException)`),
      (4) `Interlocked.CompareExchange(ref _activeSession, null, session)`.
      Não editar `Program.cs` (a DI resolve o novo parâmetro por reflexão — `OrderFlowGenerator` já é
      singleton registrado antes).
      Files: `sim/backend/src/DropCopySim.Core/Acceptor/FixAcceptorService.cs`
      Verify: `dotnet build sim/backend/DropCopySimulator.slnx` — o Core compila (o build completo só
      passa após o item 2 corrigir os call sites de teste).

- [ ] 2. Atualizar os dois testes existentes de `FixAcceptorService` para o novo construtor de 4
      argumentos. Adicionar um helper `private static OrderFlowGenerator CreateGenerator()` que constrói
      um gerador real: `new OrderFlowGenerator(TestSimulationConfig(), new SimulationCounters(),
      Channel.CreateUnbounded<TelemetryEvent>().Writer, NullLogger<OrderFlowGenerator>.Instance)`, onde
      `TestSimulationConfig()` devolve uma `SimulationConfig` válida com `Accounts`/`Instruments`
      não-vazios e `EnabledMessageTypes = ["8"]` (espelhar o `CreateConfig` de
      `DropCopySim.Core.Tests/OrderFlowGeneratorTests.cs`). Passar `CreateGenerator()` como 4º
      argumento nos dois `new FixAcceptorService(...)`. Adicionar os `using` necessários
      (`System.Threading.Channels`, `DropCopySim.Core.Generation`, `DropCopySim.Core.Telemetry`).
      Files: `sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorServiceTests.cs`
      Verify: `dotnet build sim/backend/DropCopySimulator.slnx` — solução compila sem warnings;
      `dotnet test sim/backend/DropCopySimulator.slnx --filter FullyQualifiedName~FixAcceptorServiceTests`
      — os dois testes passam.

- [ ] 3. Adicionar o teste de integração do Defeito A (wiring de ponta a ponta). Novo arquivo que sobe
      `FixAcceptorService` com um `OrderFlowGenerator` real (mesmo helper do item 2) em porta efêmera,
      conecta um `TcpClient`, envia um Logon FIX 4.4 válido via `B3FixMessageWriter` (CompIDs invertidos:
      `SenderCompID = Options.TargetCompId` = `"CLIENT1"`, `TargetCompID = Options.SenderCompId` =
      `"ENTRYPOINT-FIRM-0001"`, `MsgType="A"`, `msgSeqNum:1`, `businessSeqNum:1`, `HeartBtInt(108)=30`,
      `ResetSeqNumFlag(141)="Y"`), espera `service.ActiveSession?.Fsm.State == Active`, chama
      `generator.Start()`, e asserta `generator.Counters.TotalSent > 0` e recebimento de um frame com
      `35=8` lido do `TcpClient`. Timeout defensivo de 5 s em todas as esperas.
      Files: `sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorGeneratorWiringTests.cs` (novo)
      Verify: `dotnet test sim/backend/DropCopySimulator.slnx --filter FullyQualifiedName~FixAcceptorGeneratorWiringTests`
      — o novo teste passa.

- [ ] 4. Tratar o fechamento abortado do WebSocket de telemetria graciosamente (Defeito B). Extrair
      `internal static async Task TryCloseGracefullyAsync(WebSocket socket, CancellationToken cancellationToken)`
      que retorna cedo se `socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)`,
      senão chama `CloseAsync(NormalClosure, "Closing", ct)` dentro de `try/catch (WebSocketException)`
      (engole só `WebSocketException`). Em `PumpUntilClosedAsync`, envolver o `ReceiveAsync` em
      `try/catch (WebSocketException) { return; }` e, no frame de Close, chamar
      `TryCloseGracefullyAsync(socket, cancellationToken)` em vez do `CloseAsync` direto. No catch
      externo do endpoint, trocar `logger.LogDebug(ex, ...)` por `logger.LogDebug(...)` **sem** a
      exceção. Nenhum catch deve capturar `OperationCanceledException` (ela continua propagando).
      Files: `sim/backend/src/DropCopySim.Host/Endpoints/TelemetryEndpoints.cs`
      Verify: `dotnet build sim/backend/DropCopySimulator.slnx` — Host compila sem warnings.

- [ ] 5. Expor o método `internal` do Host ao projeto de teste. Adicionar
      `<InternalsVisibleTo Include="DropCopySim.Integration.Tests" />` ao csproj do Host e
      `<ProjectReference Include="..\..\src\DropCopySim.Host\DropCopySim.Host.csproj" />` ao csproj de
      teste (hoje referencia só `DropCopySim.Core`).
      Files: `sim/backend/src/DropCopySim.Host/DropCopySim.Host.csproj`,
      `sim/backend/tests/DropCopySim.Integration.Tests/DropCopySim.Integration.Tests.csproj`
      Verify: `dotnet build sim/backend/DropCopySimulator.slnx` — solução compila.

- [ ] 6. Adicionar o teste de unidade do Defeito B. Novo arquivo com um test double `WebSocket` escrito
      à mão (subclasse de `System.Net.WebSockets.WebSocket` que sobrescreve `State` e `CloseAsync`;
      demais membros abstratos lançam `NotSupportedException`). Dois casos: (a) `State = Aborted` →
      `TryCloseGracefullyAsync` retorna sem lançar e sem chamar `CloseAsync`; (b) `State = Open` com
      `CloseAsync` lançando `WebSocketException` → `TryCloseGracefullyAsync` não propaga. Usar
      asserções xUnit puras (`Assert`), sem NSubstitute/FluentAssertions.
      Files: `sim/backend/tests/DropCopySim.Integration.Tests/TelemetryCloseTests.cs` (novo)
      Verify: `dotnet test sim/backend/DropCopySimulator.slnx --filter FullyQualifiedName~TelemetryCloseTests`
      — o novo teste passa.

- [ ] 7. Verificação final da solução completa. Rodar build e toda a suíte.
      Files: nenhum (verificação).
      Verify: `dotnet build sim/backend/DropCopySimulator.slnx` (zero erros/warnings) e
      `dotnet test sim/backend/DropCopySimulator.slnx` (todos os testes verdes).

## Digest

Comandos (a partir de `c:\repos\nexusrouter`):
- Build: `dotnet build sim/backend/DropCopySimulator.slnx`
- Test: `dotnet test sim/backend/DropCopySimulator.slnx`
- Verificado hoje: a solução já compila e os testes existentes passam antes destas mudanças
  (os dois testes de `FixAcceptorServiceTests` passam com o construtor de 3 args; os
  `OrderFlowGeneratorTests` já exercitam `AttachSession`). Após o item 1, o build só volta a passar
  com o item 2 (os call sites de teste deixam de compilar com o 4º parâmetro obrigatório — esperado).

`sim/backend/src/DropCopySim.Core/Acceptor/FixAcceptorService.cs`
- Construtor atual (3 params): `FixAcceptorService(IOptions<AcceptorOptions> options,
  ILogger<FixAcceptorService> logger, ILogger<AcceptorSession> sessionLogger)` → adicionar 4º param
  `OrderFlowGenerator generator` + campo `_generator`.
- `AcceptLoopAsync`: hoje o bloco é `var session = new AcceptorSession(socket, _options, _sessionLogger);
  Volatile.Write(ref _activeSession, session); try { await session.RunAsync(ct); } catch (Exception ex)
  { _logger.LogError(...); } finally { await session.DisposeAsync(); Interlocked.CompareExchange(ref
  _activeSession, null, session); }`. Inserir o handler nomeado de `StateChanged` entre o
  `Volatile.Write` e o `try`; reescrever o `finally` na ordem fixa (DisposeAsync → unsubscribe →
  AttachSession(null) incondicional → CompareExchange). `using DropCopySim.Core.Generation;` já não é
  necessário (mesmo namespace raiz? não — `OrderFlowGenerator` está em `DropCopySim.Core.Generation`,
  então adicionar o `using`).
- Teste que cobre: `FixAcceptorServiceTests` (atualizado, item 2) + `FixAcceptorGeneratorWiringTests`
  (novo, item 3).

`sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorServiceTests.cs`
- Dois `new FixAcceptorService(Options.Create(new AcceptorOptions{...}), NullLogger<FixAcceptorService>.Instance,
  NullLogger<AcceptorSession>.Instance)` → adicionar o 4º argumento `CreateGenerator()`. Adicionar o
  helper `CreateGenerator()` + `TestSimulationConfig()` (espelhar `CreateConfig` de
  `OrderFlowGeneratorTests`). Novos `using`: `System.Threading.Channels`, `DropCopySim.Core.Generation`,
  `DropCopySim.Core.Telemetry`.

`sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorGeneratorWiringTests.cs` (novo)
- Padrão de socket/espera: reaproveitar `GetAvailablePort()` e o estilo `WaitForConditionAsync` de
  `FixAcceptorServiceTests`; Logon via `B3FixMessageWriter.WriteB3Header(msgType:"A", senderCompId:
  options.TargetCompId, targetCompId: options.SenderCompId, msgSeqNum:1, sendingTime:DateTime.UtcNow,
  businessSeqNum:1)` + `WriteField(FixTags.HeartBtInt, 30)` + `WriteField(FixTags.ResetSeqNumFlag, "Y")`
  + `Finish()`. Leitura de `35=8` via `TcpClient` (estilo `ReadFramesAsync`/`Fix44FrameScanner` de
  `OrderFlowGeneratorTests`). `AcceptorOptions` default: `SenderCompId="ENTRYPOINT-FIRM-0001"`,
  `TargetCompId="CLIENT1"`, `BeginString="FIX.4.4"` (confirmar os defaults em `AcceptorOptions` ao
  implementar).

`sim/backend/src/DropCopySim.Host/Endpoints/TelemetryEndpoints.cs`
- Hoje `PumpUntilClosedAsync` faz `var result = await socket.ReceiveAsync(buffer, ct);` e no Close
  chama `socket.CloseAsync(NormalClosure, "Closing", ct)`. O catch externo do endpoint faz
  `logger.LogDebug(ex, "Telemetry WebSocket client {ClientId} closed abnormally.", clientId)`.
  Extrair `TryCloseGracefullyAsync`; envolver `ReceiveAsync` em `catch (WebSocketException) return;`;
  trocar o `CloseAsync` direto pelo helper; remover o `ex` do `LogDebug`.
- Teste que cobre: `TelemetryCloseTests` (novo, item 6).

`sim/backend/src/DropCopySim.Host/DropCopySim.Host.csproj`
- Adicionar `<ItemGroup><InternalsVisibleTo Include="DropCopySim.Integration.Tests" /></ItemGroup>`.

`sim/backend/tests/DropCopySim.Integration.Tests/DropCopySim.Integration.Tests.csproj`
- Hoje só `<ProjectReference Include="..\..\src\DropCopySim.Core\DropCopySim.Core.csproj" />`. Adicionar
  `<ProjectReference Include="..\..\src\DropCopySim.Host\DropCopySim.Host.csproj" />`.

`sim/backend/tests/DropCopySim.Integration.Tests/TelemetryCloseTests.cs` (novo)
- Test double: `private sealed class FakeWebSocket : WebSocket` com `State` configurável e `CloseAsync`
  opcionalmente lançando `WebSocketException`; membros abstratos restantes (`CloseOutputAsync`,
  `ReceiveAsync`, `SendAsync`, `Abort`, `Dispose`, `CloseStatus`, `CloseStatusDescription`,
  `SubProtocol`) com stubs mínimos. Asserções: `await TelemetryEndpoints.TryCloseGracefullyAsync(fake, default)`
  não lança em ambos os casos (acessível via `InternalsVisibleTo`).
