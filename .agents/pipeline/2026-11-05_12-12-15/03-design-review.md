# Revisão de Design (Revisão 2) — Correção de dois defeitos no DropCopy simulator

Revisão do documento `02-design.md` (revisão 2) contra `01-requirements.md`, com reverificação dos
7 findings da revisão anterior (embutida no próprio `02-design.md`) e revisão do que mudou. Todas as
afirmações sobre o código existente foram conferidas diretamente no fonte (`c:\repos\nexusrouter\sim\`).

Veredito: **APPROVED** (0 HIGH, 0 MEDIUM, 2 NIT).

---

## Reverificação dos findings da revisão anterior

- **Finding 1 anterior (HIGH) — novo parâmetro obrigatório quebra `FixAcceptorServiceTests`.**
  **Closed.** O design agora escolhe o parâmetro obrigatório `OrderFlowGenerator generator`
  (`ArgumentNullException.ThrowIfNull`), atualiza os dois call sites via helper `CreateGenerator()` e
  lista `FixAcceptorServiceTests.cs` na tabela "Arquivos tocados". Verifiquei no fonte que os dois
  testes (`StartAsync_ReconnectsAfterActiveSessionIsDropped` e
  `StartAsync_RejectsEnabledTlsInsteadOfAcceptingPlaintext`) usam o construtor de 3 argumentos e, de
  fato, deixariam de compilar sem a atualização. A correção está completa.

- **Finding 2 anterior (HIGH) — plano de teste do Defeito B assume referência ao Host inexistente.**
  **Closed.** Confirmei que `DropCopySim.Integration.Tests.csproj` só referencia `DropCopySim.Core`
  e que `DropCopySim.Host.csproj` não tem `InternalsVisibleTo`. O design agora especifica ambos
  (`ProjectReference` ao Host e `<InternalsVisibleTo Include="DropCopySim.Integration.Tests" />`),
  lista-os em "Arquivos tocados" e descarta `WebApplicationFactory`/`Microsoft.AspNetCore.Mvc.Testing`.

- **Finding 3 anterior (MEDIUM) — Logon de teste subespecificado.** **Closed.** O design fixa os
  campos exatos (`BeginString=FIX.4.4`, `MsgType=A`, `SenderCompID=Options.TargetCompId`,
  `TargetCompID=Options.SenderCompId`, `HeartBtInt=30`, `ResetSeqNumFlag=Y`) e o uso de
  `B3FixMessageWriter.WriteB3Header` + `WriteField`. Conferi contra `HandleInboundFrameAsync`: estes
  são exatamente os campos exigidos para a transição a `Active`. Conferi os defaults de
  `AcceptorOptions` (`SenderCompId="ENTRYPOINT-FIRM-0001"`, `TargetCompId="CLIENT1"`,
  `BeginString="FIX.4.4"`) — os valores do design batem.

- **Finding 4 anterior (MEDIUM) — ordem no `finally` e detach condicional.** **Closed.** A ordem foi
  fixada: `DisposeAsync()` → `StateChanged -= handler` → `AttachSession(null)` incondicional →
  `CompareExchange`. O "detach condicional" foi removido com justificativa correta (o gerador não
  expõe getter da sessão anexada). Conferi que o `finally` atual faz `DisposeAsync()` seguido de
  `Interlocked.CompareExchange(ref _activeSession, null, session)`, e que `Disconnect()` chama
  `TransitionToDisconnected()` + `PublishState()` com `State = _fsm.State.ToString()` — logo o handler
  de `Disconnected` dispara durante o `DisposeAsync`, como o design afirma.

- **Finding 5 anterior (MEDIUM) — FR-A-03 depende de `AttachSession` seguro em qualquer estado.**
  **Closed.** O design afirma explicitamente que `AttachSession` é seguro em Idle/Running/Paused e
  cobre os dois sub-casos. Conferi no fonte: `AttachSession` só checa `_disposed` e faz
  `Volatile.Write(ref _session, session)`, e `Start()` não toca em `_session`. Afirmações corretas.

- **Finding 6 anterior (NIT) — catch interno deve capturar apenas `WebSocketException`.** **Closed.**
  O design explicita que os dois `catch` do pump e o de `TryCloseGracefullyAsync` capturam apenas
  `WebSocketException`, preservando a propagação de `OperationCanceledException`. Conferi no endpoint
  atual que o catch externo `OperationCanceledException` + `finally` fazem a limpeza.

- **Finding 7 anterior (NIT) — texto sobre `Program.cs` redundante.** **Closed.** A seção foi
  reduzida a uma frase. Conferi `Program.cs`: `OrderFlowGenerator` é registrado como singleton via
  factory antes de `builder.Services.AddSingleton<FixAcceptorService>();` (registro por tipo, sem
  factory manual), então a resolução por reflexão de construtor cobre o novo 4º parâmetro sem edição.
  Afirmação correta.

Todos os 7 findings anteriores estão **Closed**. Abaixo, a revisão do que mudou na revisão 2.

---

## Findings (revisão 2)

### 1. NIT — O helper `CreateGenerator()` dos testes atualizados exige `clearingFirm` não-branco e `SimulationConfig` concreta; o snippet omite esses detalhes

**Onde:** seção "Defeito A → Mudanças concretas → `FixAcceptorServiceTests.cs`", snippet `CreateGenerator()`.

**Observação:** o snippet passa quatro argumentos ao construtor de `OrderFlowGenerator`
(`config`, `new SimulationCounters()`, `Channel...Writer`, `NullLogger<...>.Instance`) e deixa os
parâmetros opcionais no default. Conferi o construtor: `messageSampleMaxPerSecond` (default 50) e
`clearingFirm` (default `"CLEARING"`, validado como não-branco por `ArgumentException.ThrowIfNullOrWhiteSpace`)
têm defaults válidos, então o snippet compila e não lança. O único ponto em aberto é `TestSimulationConfig()`:
o design diz "reutilizar o default/fábrica já usada por `OrderFlowGeneratorTests` se existir, ou montar
uma instância". Isso é uma alternativa aceitável, mas não fixa qual. Como o construtor **não** valida
accounts/instruments (só `Start()` valida, e estes dois testes não chamam `Start()`), qualquer
`SimulationConfig` não-nula serve.

**Fix concreto:** manter como está é aceitável; se quiser remover a ambiguidade, fixe no design que
`TestSimulationConfig()` retorna `SimulationConfigBinder`/`new SimulationConfig()` padrão (não-nulo),
já que o construtor do gerador não valida o conteúdo. Nenhuma ação bloqueante.

### 2. NIT — No teste de integração, `WriteB3Header(msgType: "A", ...)` com argumentos nomeados liga-se à sobrecarga `string`; o `B3FixMessageWriter` também tem a sobrecarga `ReadOnlySpan<byte>`

**Onde:** seção "Defeito A → teste de integração", snippet do Logon.

**Observação:** `B3FixMessageWriter.WriteB3Header` tem duas sobrecargas (`string...` e
`ReadOnlySpan<byte>...`). O snippet usa argumentos nomeados com literais `string` (`msgType: "A"`,
`senderCompId: options.TargetCompId`), o que resolve inequivocamente para a sobrecarga `string` —
correto. Conferi que `options.TargetCompId`/`options.SenderCompId` são `string`. O snippet está
coerente; o único detalhe é que `WriteField(FixTags.HeartBtInt, 30)` usa a sobrecarga `long` e
`WriteField(FixTags.ResetSeqNumFlag, "Y")` a sobrecarga `string` — ambas existem e compilam.

**Fix concreto:** nenhum; a observação é apenas para garantir, na implementação, que os literais
sejam `string` (não `u8`) para casar com a sobrecarga nomeada. Nenhuma ação bloqueante.

---

## Verified Assumptions

- **`OrderFlowGenerator.SendNextAsync` faz no-op silencioso sem sessão** — confirmado: `ActiveSession()`
  retorna `null` quando `_session` é null ou `Fsm.State != Active`, e `SendNextAsync` retorna `true`. ✔
- **`AttachSession` nunca é chamado em produção** — confirmado: nenhuma chamada no Host/Core; só nos
  testes do gerador. ✔
- **`AttachSession` usa `Volatile.Write`; `ActiveSession()` usa `Volatile.Read`; `AttachSession` só
  checa `_disposed`** — confirmado em `OrderFlowGenerator.cs`; seguro em qualquer estado. ✔
- **`Start()` não limpa `_session`** — confirmado: `Start()` mexe apenas em `_ordersStarted`,
  `_lifecycle`, `_lifecycleIndex`, `_counters`, `_cts`, `_loop`, `_state`. ✔
- **`AcceptorSession.StateChanged` é `event Action<SessionStateChanged>?` emitido via `PublishState()`
  em toda transição** — confirmado; `PublishState` é chamado em Connected, LogonReceived, Active,
  LogoutSent e `Disconnect()`. ✔
- **`SessionStateChanged.State` é `required string` = `_fsm.State.ToString()`** — confirmado em
  `SessionStateChanged.cs` e `AcceptorSession.PublishState`; comparação ordinal com
  `nameof(AcceptorSessionState.Active)`/`Disconnected` é válida. ✔
- **`AcceptorSessionState` tem `Active = 3` e `Disconnected = 0`** — confirmado. ✔
- **`Disconnect()` chama `TransitionToDisconnected()` + `PublishState()`** — confirmado; o
  `DisposeAsync` do socket leva o read-loop a `Disconnect()`, disparando o handler `Disconnected`. ✔
- **`FixAcceptorService.AcceptLoopAsync` é sequencial (uma sessão por vez); `finally` faz
  `DisposeAsync()` depois `CompareExchange`** — confirmado no fonte. ✔
- **Construtor atual de `FixAcceptorService` tem 3 parâmetros; os dois testes usam-no** — confirmado;
  o 4º parâmetro obrigatório quebraria a compilação sem a atualização prevista. ✔
- **`FixAcceptorService` registrado por tipo (`AddSingleton<FixAcceptorService>()`) após
  `OrderFlowGenerator` (singleton via factory)** — confirmado em `Program.cs`; resolução por reflexão
  cobre o novo parâmetro sem edição. ✔
- **`TelemetryEndpoints.PumpUntilClosedAsync` chama `CloseAsync` direto ao receber Close; o catch
  externo faz `LogDebug(ex, ...)` com a exceção; o `finally` faz `hub.RemoveClient`** — confirmado em
  `TelemetryEndpoints.cs`. ✔
- **`B3FixMessageWriter` é `public ref struct` com sobrecargas `string` e `ReadOnlySpan<byte>` de
  `WriteB3Header`, e `WriteField(int,string)`/`WriteField(int,long)`** — confirmado. ✔
- **Campos exigidos para Logon → `Active` em `HandleInboundFrameAsync`** — confirmado: `BeginString ==
  Options.BeginString`, `MsgType == "A"`, `TargetCompID == Options.SenderCompId`, `SenderCompID ==
  Options.TargetCompId`, `HeartBtInt` inteiro positivo; `ResetSeqNumFlag=Y` opcional. ✔
- **Defaults de `AcceptorOptions`: `SenderCompId="ENTRYPOINT-FIRM-0001"`, `TargetCompId="CLIENT1"`,
  `BeginString="FIX.4.4"`** — confirmado; os valores do Logon de teste batem com os defaults. ✔
- **`OrderFlowGenerator` constrói sem validar accounts/instruments (só `Start()` valida); `clearingFirm`
  default `"CLEARING"` é não-branco** — confirmado; o helper `CreateGenerator()` compila e não lança. ✔
- **`DropCopySim.Host` é `Microsoft.NET.Sdk.Web` com `OutputType=Exe` e `public partial class Program`**
  — confirmado; o `ProjectReference` a partir do teste é suportado. ✔
- **`DropCopySim.Integration.Tests.csproj` só referencia `DropCopySim.Core` e não tem `Mvc.Testing`** —
  confirmado; o design adiciona o `ProjectReference` ao Host e o `InternalsVisibleTo`. ✔

## Unverified / Wrong Assumptions

- Nenhuma afirmação incorreta encontrada nesta revisão. As duas observações NIT acima são pequenas
  lacunas de especificação (qual `SimulationConfig` concreta o helper retorna; garantir literais
  `string` na chamada nomeada de `WriteB3Header`), nenhuma delas incorreta nem bloqueante.
- O design declara que `AttachSession` lança `ObjectDisposedException` após dispose do gerador e
  envolve as chamadas em `try/catch (ObjectDisposedException)` — conferido: `AttachSession` chama
  `ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this)`, então a mitigação é
  correta. ✔
