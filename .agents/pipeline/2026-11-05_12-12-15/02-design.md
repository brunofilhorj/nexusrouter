# Design Técnico — Correção de dois defeitos no DropCopy simulator

> **Revisão 2.** Este documento foi revisado após `03-design-review.md`
> (veredito `CHANGES_REQUESTED`: 2 HIGH, 3 MEDIUM, 2 NIT). As respostas ponto a ponto aos
> findings estão na seção final "Respostas à revisão de design".

## Visão geral

O trabalho corrige dois defeitos isolados na pasta `sim/` do repositório, sem tocar no NexusRouter e
sem commit/push/PR. O **Defeito A** (bloqueante) é a ausência do wiring que entrega a sessão FIX
aceita ao `OrderFlowGenerator`: o gerador roda seu loop de background mas `ActiveSession()` sempre
retorna `null` porque `AttachSession(...)` nunca é chamado em lugar nenhum do host/core, então
`SendNextAsync` faz no-op silencioso a cada tick. A correção estabelece essa ponte no
`FixAcceptorService`, que é o único componente que cria e possui a `AcceptorSession`. O **Defeito B**
(ruído de log) é o `CloseAsync` em `TelemetryEndpoints.PumpUntilClosedAsync` lançando
`WebSocketException` quando o socket já está `Aborted` (fecha/reabre do SimPanel sob React
StrictMode); a correção trata esse fechamento graciosamente no endpoint.

Ambas as correções ficam contidas em dois arquivos de produção
(`FixAcceptorService.cs` e `TelemetryEndpoints.cs`), **sem edição em `Program.cs`**, mais a atualização
dos testes existentes de `FixAcceptorService` (que deixam de compilar com o novo construtor) e novos
testes. As edições WIP já commitadas em `49b39aa` (guard de shutdown no
`FixAcceptorService.StopAsync`, serialização de enums em `TelemetryHub`, hidratação do frontend) são
preservadas: nenhuma delas conflita com estas correções e a de shutdown convive com o novo código no
mesmo arquivo.

A stack está travada: .NET 10 / C# 14, `TreatWarningsAsErrors=true`, nullable habilitado, xUnit +
FluentAssertions + NSubstitute nos testes. Nenhuma dependência nova é introduzida (ver decisão de
testabilidade do Defeito B, que evita `Microsoft.AspNetCore.Mvc.Testing`).

## Defeito A — wiring da sessão ao gerador

### Causa raiz confirmada

`OrderFlowGenerator.SendNextAsync` começa com:

```csharp
var session = ActiveSession();
if (session is null)
{
    return true; // no-op silencioso, loop continua
}
```

`ActiveSession()` só devolve a sessão publicada por `AttachSession(...)` quando
`candidate.Fsm.State == AcceptorSessionState.Active`. Uma busca no `sim/` confirma que
`AttachSession` **não é chamado em nenhum ponto de produção** — apenas nos testes
(`OrderFlowGeneratorTests`), que por isso passam. O `FixAcceptorService.AcceptLoopAsync` cria a
`AcceptorSession`, a expõe em `ActiveSession` e chama `session.RunAsync(...)`, mas não há wiring
ligando essa sessão ao gerador singleton. Logo o gerador nunca enxerga uma sessão ativa e o loop
fica inerte — sem exceção e sem log, exatamente como observado.

Os testes existentes provam que `AttachSession` + `ActiveSession()` funcionam quando a sessão está
`Active`: `OrderFlowGeneratorTests.ActiveSessionFixture` transiciona a FSM para `Active` e chama
`generator.AttachSession(fixture.Session)` antes de `Start()`, e os frames são emitidos. O defeito é
puramente a ausência desse `AttachSession` no caminho de produção.

### Ponto de integração escolhido

Três pontos de integração são viáveis: (1) assinar o evento `AcceptorSession.StateChanged` e reagir
à transição para/de `Active`; (2) injetar o gerador no `FixAcceptorService` e chamar `AttachSession`
diretamente no `AcceptLoopAsync` em torno de `RunAsync`; (3) um serviço-ponte separado
(`IHostedService`) que faz polling de `FixAcceptorService.ActiveSession`.

**Escolho a opção (1) combinada com (2): injetar o gerador no `FixAcceptorService` e, no
`AcceptLoopAsync`, assinar `session.StateChanged` logo após criar a sessão, chamando
`AttachSession(session)` na transição para `Active` e `AttachSession(null)` na transição para
`Disconnected`; além disso, garantir `AttachSession(null)` incondicional no bloco `finally` que
descarta a sessão.** Razões:

- O `FixAcceptorService` é o dono do ciclo de vida da sessão (cria, roda, descarta) — é o lugar
  natural e com menor acoplamento para o wiring; não há serviço novo nem polling.
- O evento `StateChanged` já é emitido em toda transição da FSM (`AcceptorSession.PublishState`), o
  que dá o gancho exato para `Active` e `Disconnected` sem introduzir corrida: o gerador só precisa
  ver a sessão quando ela está `Active` (o próprio `ActiveSession()` revalida `Fsm.State == Active`,
  então mesmo que o `AttachSession` ocorra um instante antes da transição observável, o guard do
  gerador cobre o intervalo).
- A opção (3) (polling) adiciona um `IHostedService` e latência desnecessária; a opção pura (2) sem o
  evento teria que anexar antes de `RunAsync` e detectar `Active`/desconexão por outra via — o evento
  já existe e é mais limpo.

O acoplamento DI: `FixAcceptorService` passa a depender de `OrderFlowGenerator` (ambos singletons no
`Program.cs`, sem ciclo — o gerador não referencia o acceptor). Isso respeita a camada: ambos vivem
em `DropCopySim.Core`, e o `Program.cs` já registra os dois como singleton.

### Decisão sobre a assinatura do construtor (parâmetro obrigatório vs. opcional)

O construtor atual de `FixAcceptorService` tem **três** parâmetros
(`IOptions<AcceptorOptions>`, `ILogger<FixAcceptorService>`, `ILogger<AcceptorSession>`). O design
precisa de acesso ao `OrderFlowGenerator`. Duas opções:

- **(a) parâmetro obrigatório** `OrderFlowGenerator generator` — exige atualizar os dois call sites de
  teste existentes em `FixAcceptorServiceTests.cs` que hoje usam o construtor de 3 argumentos.
- **(b) parâmetro opcional** `OrderFlowGenerator? generator = null` — não quebra assinatura; o wiring
  só é feito quando não-nulo.

**Escolho a opção (a): parâmetro obrigatório `OrderFlowGenerator generator` com
`ArgumentNullException.ThrowIfNull`.** Razões: o gerador é parte essencial do contrato de produção
(sem ele o acceptor não cumpre sua função de emitir), e um parâmetro obrigatório torna o invariante
explícito e verificável no startup. Um parâmetro opcional silenciaria uma eventual regressão de
wiring (o mesmo tipo de defeito que este design corrige). O custo é atualizar os dois testes
existentes, o que é feito abaixo. A DI de produção já resolve o parâmetro obrigatório sem edição em
`Program.cs` (ver "Program.cs").

### Mudanças concretas

**`sim/backend/src/DropCopySim.Core/Acceptor/FixAcceptorService.cs`**

1. Adicionar um 4º parâmetro de construtor `OrderFlowGenerator generator` (com
   `ArgumentNullException.ThrowIfNull`) e guardá-lo em campo
   `private readonly OrderFlowGenerator _generator;`. Documentar no XML do construtor.
2. No `AcceptLoopAsync`, após `var session = new AcceptorSession(...)` e
   `Volatile.Write(ref _activeSession, session)`, e **antes** de `RunAsync`, registrar um handler
   nomeado para `session.StateChanged`:
   - Em `State == nameof(AcceptorSessionState.Active)` → `_generator.AttachSession(session)`.
   - Em `State == nameof(AcceptorSessionState.Disconnected)` → `_generator.AttachSession(null)`.

   `SessionStateChanged.State` é uma `string` (verificado: `public required string State`), contendo
   o nome do estado da FSM (`PublishState` usa `_fsm.State.ToString()`). A comparação usa
   `string.Equals(e.State, nameof(AcceptorSessionState.Active), StringComparison.Ordinal)` e idem para
   `Disconnected`, evitando literais soltos. Qualquer outro estado é ignorado.

   O handler é implementado como **método local nomeado** (ou delegate em variável local) para
   permitir `session.StateChanged -= handler` no `finally`. Como `AcceptLoopAsync` roda **uma sessão
   por vez** (sequencial — o `while` só aceita a próxima conexão após `RunAsync`+`DisposeAsync` da
   atual, verificado no fonte), não há duas sessões anexadas simultaneamente.

   O handler envolve a chamada a `AttachSession` em `try/catch (ObjectDisposedException)` que ignora
   (log `Debug` sem exceção): no shutdown o gerador pode já ter sido descartado quando a transição
   `Disconnected` tardia chega; `AttachSession` lança `ObjectDisposedException` nesse caso (verificado:
   só checa `_disposed` + `Volatile.Write`). Ignorar é correto — o gerador descartado não precisa mais
   da sessão.

3. **Ordem determinística no bloco `finally`** (que hoje faz `await session.DisposeAsync()` seguido de
   `Interlocked.CompareExchange(ref _activeSession, null, session)`). A nova ordem é:

   1. `await session.DisposeAsync().ConfigureAwait(false);` — fecha o socket; o read-loop observa o
      transporte fechado e chama `Disconnect()` → `PublishState()` com estado `Disconnected`, o que
      **dispara o handler** `StateChanged("Disconnected")` → `AttachSession(null)`. Mantê-lo primeiro
      garante que o detach via evento ocorre.
   2. `session.StateChanged -= handler;` — desinscreve **depois** do `DisposeAsync`, para não perder o
      evento `Disconnected`.
   3. `_generator.AttachSession(null);` — detach **incondicional** (idempotente e barato: só um
      `Volatile.Write`), como última linha de defesa caso o evento não tenha disparado nesta sessão.
      Também envolto em `try/catch (ObjectDisposedException)` ignorado (mesmo motivo do item 2).
   4. `Interlocked.CompareExchange(ref _activeSession, null, session);` — preserva o comportamento
      existente de limpar `ActiveSession`.

   O "detach condicional" descrito na versão anterior é **removido**: o gerador não expõe a sessão
   anexada (não há getter), então um predicado "só se eu ainda sou a sessão anexada" não é
   implementável. Como o loop é sequencial e `AttachSession(null)` é idempotente, o detach
   incondicional é correto e mais simples. (Finding 4.)

**`sim/backend/src/DropCopySim.Host/Program.cs`**

Nenhuma edição em `Program.cs`. `builder.Services.AddSingleton<FixAcceptorService>()` resolve o novo
4º parâmetro por reflexão de construtor, porque `OrderFlowGenerator` já é singleton registrado antes
de `FixAcceptorService` (verificado no fonte). Não há factory manual para `FixAcceptorService`, então
o container satisfaz o parâmetro automaticamente. (Finding 7.)

**`sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorServiceTests.cs`**

Os dois testes existentes (`StartAsync_ReconnectsAfterActiveSessionIsDropped` e
`StartAsync_RejectsEnabledTlsInsteadOfAcceptingPlaintext`) constroem `FixAcceptorService` com o
construtor de 3 argumentos e **deixam de compilar** com o 4º parâmetro obrigatório. Ambos são
atualizados para passar um `OrderFlowGenerator` real construído por um helper de teste. O helper
produz um gerador mínimo válido:

```csharp
private static OrderFlowGenerator CreateGenerator() =>
    new OrderFlowGenerator(
        TestSimulationConfig(),                               // SimulationConfig mínima válida
        new SimulationCounters(),
        Channel.CreateUnbounded<TelemetryEvent>().Writer,
        NullLogger<OrderFlowGenerator>.Instance);
```

`TestSimulationConfig()` devolve uma `SimulationConfig` válida o suficiente para construir o gerador
(o construtor do gerador não inicia o loop; os dois testes existentes não chamam `Start()`, então a
config só precisa ser não-nula — reutilizar o default/fábrica já usada por `OrderFlowGeneratorTests`
se existir, ou montar uma instância com `Accounts`/`Instruments` não-vazios para satisfazer validações
posteriores). Esses dois testes continuam exercitando exatamente o que exercitavam antes (conexão e
reconexão de socket cru; rejeição de TLS); o gerador é apenas um colaborador injetado e não altera o
comportamento sob teste. (Finding 1.)

### Fluxo resultante (atende FR-A-01..05)

`AttachSession` é **seguro em qualquer estado do gerador** (Idle/Running/Paused): só verifica
`_disposed` e faz `Volatile.Write(ref _session, session)` (verificado no fonte). Isso cobre os dois
sub-casos de FR-A-03 de forma simétrica:

- **Sessão conecta antes do `start`** (sessão já `Active`, gerador `Idle`): o handler chama
  `AttachSession(session)` enquanto o gerador está `Idle`; `_session` fica setado. Quando o `POST
  /v1/sim/start` ocorre depois, `Start()` **não** limpa `_session` (verificado: `Start()` só mexe em
  `_ordersStarted`, `_lifecycle`, `_counters`, `_cts`, `_loop`, `_state`), então o loop, ao girar,
  enxerga a sessão imediatamente e emite.
- **`start` antes da sessão conectar** (gerador `Running`, sem sessão): o loop gira fazendo no-op via
  `SendNextAsync` (retorna `true` sem emitir); quando a sessão chega a `Active`, o handler chama
  `AttachSession(session)` e o próximo tick passa a emitir — sem novo `start` (AC-3, FR-A-05).

Em ambos os casos o guard `ActiveSession()` revalida `Fsm.State == Active` a cada tick, então o
wiring é tolerante a eventos fora de ordem. (Finding 5.)

- Sessão desconecta → `StateChanged("Disconnected")` → `AttachSession(null)` → o próximo tick volta
  ao no-op silencioso, sem erro (FR-A-05).
- `GET /v1/state` passa a refletir `totalSent` crescente e taxa > 0 enquanto houver sessão `Active`,
  porque `SimulationCounters` é incrementado em `SendNextAsync` após cada envio (FR-A-04). O
  `BuildCountersSnapshot` em `Program.cs` já calcula a taxa a partir de `TotalSent` e `ElapsedMs`.

### Concorrência (NFR-03)

`AttachSession` usa `Volatile.Write(ref _session, session)` e `ActiveSession()` usa `Volatile.Read` —
o contrato de concorrência já existe e é respeitado. O handler de `StateChanged` roda na thread do
read-loop da sessão; o loop do gerador roda em `Task.Run`. A única escrita em `_session` passa a vir
do handler (e do detach incondicional no `finally`, que roda após o read-loop encerrar); o gerador só
lê. Nenhum lock novo é introduzido no caminho de envio FIX.

## Defeito B — fechamento gracioso do WebSocket de telemetria

### Causa raiz confirmada

Em `TelemetryEndpoints.PumpUntilClosedAsync`, ao receber um frame de Close o código chama
`socket.CloseAsync(WebSocketCloseStatus.NormalClosure, ...)`. Quando o cliente (SimPanel sob React
StrictMode) aborta a conexão — fecha e reabre rapidamente — o socket pode já estar em
`WebSocketState.Aborted` (ou `CloseReceived`/`Closed`) no momento do `CloseAsync`, e o
`WebSocket` lança `WebSocketException` ("The WebSocket is in an invalid state (Aborted) for this
operation"). Hoje o `catch (WebSocketException ex)` externo do endpoint registra em `Debug`
**com a exceção anexada** (`logger.LogDebug(ex, ...)`), o que ainda emite stack trace quando o nível
Debug está habilitado.

### Mudança concreta

**`sim/backend/src/DropCopySim.Host/Endpoints/TelemetryEndpoints.cs`**

1. Extrair a lógica de fechamento gracioso em um método **`internal static`** testável:

   ```csharp
   internal static async Task TryCloseGracefullyAsync(WebSocket socket, CancellationToken cancellationToken)
   {
       if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
       {
           return; // Aborted/Closed/CloseSent: nada a fechar.
       }

       try
       {
           await socket
               .CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken)
               .ConfigureAwait(false);
       }
       catch (WebSocketException)
       {
           // O socket foi abortado entre a checagem de estado e o CloseAsync; já está efetivamente
           // fechado. Nada a limpar aqui — o finally do endpoint remove o cliente do hub.
       }
   }
   ```

   O `catch` captura **apenas `WebSocketException`** — não `Exception`. Isso preserva a propagação de
   `OperationCanceledException` (shutdown/abort do request) para o `catch (OperationCanceledException)`
   externo do endpoint e para o `finally` de limpeza (FR-B-03). (Finding 6.)

2. `PumpUntilClosedAsync` passa a (a) envolver o `ReceiveAsync` em `try/catch (WebSocketException)`
   que encerra o pump graciosamente (equivalente a um Close) quando o socket é abortado durante a
   espera, e (b) chamar `TryCloseGracefullyAsync(socket, cancellationToken)` em vez do `CloseAsync`
   direto ao receber o frame de Close:

   ```csharp
   private static async Task PumpUntilClosedAsync(WebSocket socket, CancellationToken cancellationToken)
   {
       var buffer = new byte[1024];
       while (socket.State == WebSocketState.Open)
       {
           WebSocketReceiveResult result;
           try
           {
               result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
           }
           catch (WebSocketException)
           {
               // Socket abortado pelo cliente durante a espera; encerra o pump graciosamente.
               return;
           }

           if (result.MessageType == WebSocketMessageType.Close)
           {
               await TryCloseGracefullyAsync(socket, cancellationToken).ConfigureAwait(false);
               return;
           }
       }
   }
   ```

   O `catch` interno captura **apenas `WebSocketException`** (não engole `OperationCanceledException`,
   que continua propagando para o catch externo do endpoint). (Finding 6.)

3. O `catch (WebSocketException ex)` no corpo do endpoint que hoje chama `logger.LogDebug(ex, ...)`
   passa a registrar **sem a exceção** em nível `Debug`
   (`logger.LogDebug("Telemetry WebSocket client {ClientId} closed abnormally.", clientId)`),
   satisfazendo "no máximo uma entrada de log sem stack trace" (FR-B-01, FR-B-02, A4). Como o pump
   agora trata `WebSocketException` internamente, esse catch externo passa a ser a última linha de
   defesa e raramente dispara; mantê-lo sem stack trace cobre qualquer caminho residual.

**Decisão de log** (A4): logar em `Debug` **sem** a exceção, preservando visibilidade mínima de
diagnóstico sem stack trace. Alinhado às convenções do steering (`Debug` = detalhes de ciclo de vida
de sessão/conexão).

### Limpeza garantida (FR-B-03)

O `finally` do endpoint já faz `hub.RemoveClient(clientId)` e loga a desconexão em todos os caminhos
de saída (retorno normal, `OperationCanceledException`, `WebSocketException`). Como o pump agora
retorna normalmente mesmo em fechamento abortado, o `finally` continua a rodar e a remoção do cliente
ocorre em todos os caminhos. A telemetria para clientes ainda conectados é independente (o hub faz
broadcast a cada cliente), então o tratamento gracioso não afeta outros clientes (AC-5).

## Tratamento de erros (resumo por operação)

- **`AttachSession` no handler de `StateChanged` e no `finally`**: `AttachSession` lança
  `ObjectDisposedException` se o gerador já foi descartado. No shutdown, um evento `Disconnected`
  tardio (ou o detach incondicional do `finally`) pode chamar `AttachSession` após o dispose do
  gerador. **Mitigação:** `try/catch (ObjectDisposedException)` em torno de cada `AttachSession`,
  ignorando (log `Debug` sem stack trace) — recuperável, não propaga.
- **`CloseAsync` em `TryCloseGracefullyAsync`**: `WebSocketException` tratada graciosamente (retorno),
  sem log de exceção — recuperável, não propaga. Qualquer outra exceção propaga (não há catch amplo).
- **`ReceiveAsync` no pump**: `WebSocketException` → encerra o pump (retorno) — recuperável.
  `OperationCanceledException` **não** é capturada no pump; propaga para o catch externo do endpoint,
  que cai no `finally` de limpeza (FR-B-03).
- **Falha de envio FIX no gerador**: já tratada em `SendNextAsync` (incrementa `Errors`, loga
  `Warning`, avança o lifecycle) — inalterada por este design.

## Validação de entradas

- O handler de `StateChanged` valida o nome do estado por comparação ordinal com
  `nameof(AcceptorSessionState.Active)` e `nameof(AcceptorSessionState.Disconnected)`; qualquer outro
  estado é ignorado (não anexa nem desanexa). Nenhuma entrada externa nova é introduzida.
- Em `TryCloseGracefullyAsync`, a validação é sobre `socket.State` antes do `CloseAsync` (enum interno
  do framework); não há entrada de usuário.

## Invariantes e responsabilidades por camada

- **Invariante "o gerador só emite com sessão `Active`"**: dono = `OrderFlowGenerator.ActiveSession()`
  (revalida `Fsm.State == Active` a cada tick). O wiring no `FixAcceptorService` apenas alimenta o
  `_session`; a decisão final de emitir permanece no gerador. Isso mantém o guard como fonte única de
  verdade e torna o wiring tolerante a eventos fora de ordem.
- **Invariante "uma sessão por vez"**: dono = `FixAcceptorService.AcceptLoopAsync` (sequencial).
- **Invariante "limpeza do cliente WebSocket em todos os caminhos"**: dono = `finally` do endpoint em
  `TelemetryEndpoints`.

## Testabilidade

### Defeito A — teste de integração (socket → Logon → Active → emissão)

Novo arquivo `sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorGeneratorWiringTests.cs`.
Sobe o `FixAcceptorService` com um `OrderFlowGenerator` **real** (injetado via o mesmo helper
`CreateGenerator()` dos testes atualizados, com uma `SimulationConfig` válida que tenha `Accounts` e
`Instruments` não-vazios e `EnabledMessageTypes = ["8"]`), em porta efêmera. Conecta um `TcpClient`,
envia um **Logon FIX 4.4 válido** e, após a sessão chegar a `Active`, chama `generator.Start()` e
asserta que `generator.Counters.TotalSent` cresce (> 0) e que a sessão recebe `ExecutionReport`.

**Conteúdo do Logon de teste** (campos exigidos por `HandleInboundFrameAsync`, verificados no fonte).
O cliente envia com os CompIDs invertidos em relação ao acceptor:

- `BeginString = "FIX.4.4"` (default de `AcceptorOptions.BeginString`).
- `MsgType = "A"`.
- `SenderCompID = Options.TargetCompId` — default `"CLIENT1"`.
- `TargetCompID = Options.SenderCompId` — default `"ENTRYPOINT-FIRM-0001"`.
- `HeartBtInt (108) = 30` (inteiro positivo).
- `ResetSeqNumFlag (141) = "Y"`.

O frame é montado com `B3FixMessageWriter` (verificado `public ref struct`, utilizável no teste):

```csharp
Span<byte> buffer = stackalloc byte[256];
var writer = new B3FixMessageWriter(buffer);
writer.WriteB3Header(
    msgType: "A",
    senderCompId: options.TargetCompId,     // "CLIENT1"
    targetCompId: options.SenderCompId,     // "ENTRYPOINT-FIRM-0001"
    msgSeqNum: 1,
    sendingTime: DateTime.UtcNow,
    businessSeqNum: 1);
writer.WriteField(FixTags.HeartBtInt, 30);
writer.WriteField(FixTags.ResetSeqNumFlag, "Y");
int length = writer.Finish();
await stream.WriteAsync(buffer[..length].ToArray());
```

O teste então espera (`WaitForConditionAsync`) por `service.ActiveSession?.Fsm.State ==
AcceptorSessionState.Active`, chama `generator.Start()`, e espera por
`generator.Counters.TotalSent > 0`. Para confirmar a entrega real do `ExecutionReport` ao cliente
(AC-2), o teste lê do `TcpClient` até enxergar um frame com `35=8` (MsgType=8). Timeout defensivo em
todas as esperas (5 s, como no teste existente).

**Decisão explícita entre Logon real e FSM forçada:** escolho o **Logon real via `FixAcceptorService`**
(socket→Logon→Active), porque é o caminho que o AC-1..3 descreve e é o único que exercita o wiring
novo de ponta a ponta. O fixture `ActiveSessionFixture` existente força a FSM diretamente e constrói
`AcceptorSession` fora do `FixAcceptorService`, logo **não** cobre o wiring; não é reutilizado aqui. O
custo (montar o Logon acima) é baixo e o teste fica fiel ao defeito.

### Defeito B — teste de unidade de `TryCloseGracefullyAsync`

A lógica de fechamento gracioso é coberta por um **teste de unidade** do método `internal static
TryCloseGracefullyAsync(WebSocket, CancellationToken)`, com um `WebSocket` **substituído por
NSubstitute** cujo `State` retorna `WebSocketState.Aborted` (e um segundo caso `Open` cujo `CloseAsync`
lança `WebSocketException`). Asserta que o método **não propaga** exceção em nenhum dos dois casos.
Isto é determinístico e não depende do comportamento de WebSocket sobre `TestServer`.

**Infraestrutura nova exigida e onde fica** (Finding 2) — o método a testar vive no assembly do Host,
então o teste em `DropCopySim.Integration.Tests` precisa de:

1. **`ProjectReference` ao Host** no
   `sim/backend/tests/DropCopySim.Integration.Tests/DropCopySim.Integration.Tests.csproj`:
   `<ProjectReference Include="..\..\src\DropCopySim.Host\DropCopySim.Host.csproj" />`.
   (Hoje o csproj só referencia `DropCopySim.Core` — verificado.)
2. **`InternalsVisibleTo`** no Host, para expor o método `internal` ao assembly de teste. Adicionar ao
   `sim/backend/src/DropCopySim.Host/DropCopySim.Host.csproj`:
   ```xml
   <ItemGroup>
     <InternalsVisibleTo Include="DropCopySim.Integration.Tests" />
   </ItemGroup>
   ```

**Não** é usado `WebApplicationFactory<Program>` nem o pacote `Microsoft.AspNetCore.Mvc.Testing`
(ausente no `sim/` — verificado). A via de método `internal` + NSubstitute é a escolhida justamente
por não exigir esse pacote nem exercitar WebSockets sobre `TestServer`. (Findings 2 e 6.)

> Nota sobre o `ProjectReference` ao Host: o `DropCopySim.Host` é um projeto `Microsoft.NET.Sdk.Web`
> (SDK web). Referenciá-lo a partir do projeto de teste é suportado e é o que o marcador
> `public partial class Program` já antecipa. A referência não arrasta runtime extra para o teste de
> unidade (o teste nunca sobe o host; só chama o método `internal`).

### Cobertura preservada

Os testes existentes de `OrderFlowGenerator` (que já chamam `AttachSession`) permanecem verdes e
continuam cobrindo a emissão em si; o novo teste de integração cobre o elo que faltava (o wiring). Os
dois testes atualizados de `FixAcceptorServiceTests` continuam cobrindo conexão/reconexão e rejeição
de TLS. Todos os critérios de aceite 6–8 (build, test, typecheck) são verificados ao final.

## Arquivos tocados

| Arquivo | Mudança |
|---|---|
| `sim/backend/src/DropCopySim.Core/Acceptor/FixAcceptorService.cs` | Injeta `OrderFlowGenerator` (4º param obrigatório); assina `StateChanged`; `AttachSession(session)` em `Active`, `AttachSession(null)` em `Disconnected`; `finally` com ordem fixa (DisposeAsync → desinscreve handler → `AttachSession(null)` incondicional → `CompareExchange`); `try/catch (ObjectDisposedException)` nas chamadas a `AttachSession`. |
| `sim/backend/src/DropCopySim.Host/Endpoints/TelemetryEndpoints.cs` | Extrai `internal static TryCloseGracefullyAsync` (checa estado + `catch (WebSocketException)`); `PumpUntilClosedAsync` envolve `ReceiveAsync` em `catch (WebSocketException)` e chama o helper; catch externo loga em `Debug` **sem** a exceção. |
| `sim/backend/src/DropCopySim.Host/DropCopySim.Host.csproj` | Adiciona `<InternalsVisibleTo Include="DropCopySim.Integration.Tests" />`. |
| `sim/backend/tests/DropCopySim.Integration.Tests/DropCopySim.Integration.Tests.csproj` | Adiciona `ProjectReference` para `DropCopySim.Host.csproj`. |
| `sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorServiceTests.cs` | Atualiza os dois call sites do construtor de `FixAcceptorService` para passar um `OrderFlowGenerator` real (helper `CreateGenerator()`). |
| `sim/backend/tests/DropCopySim.Integration.Tests/FixAcceptorGeneratorWiringTests.cs` (novo) | Teste de integração do Defeito A: socket → Logon 4.4 válido → `Active` → `Start()` → asserta `TotalSent > 0` e recebimento de `35=8`. |
| `sim/backend/tests/DropCopySim.Integration.Tests/TelemetryCloseTests.cs` (novo) | Teste de unidade do Defeito B: `TryCloseGracefullyAsync` com `WebSocket` substituído (`Aborted`, e `Open` com `CloseAsync` lançando `WebSocketException`) não propaga. |

Nenhum arquivo do frontend é alterado por este design (o WIP de hidratação já cobre a UX e não é
necessário aos dois defeitos), então `npm run typecheck` não é requerido por estas mudanças — mas
será executado se a implementação tocar qualquer arquivo em `sim/frontend` (AC-8).

## Verificação final (mapeando AC 6–9)

1. `dotnet build sim/backend/DropCopySimulator.slnx` — sem erros nem warnings (TreatWarningsAsErrors).
   Inclui a compilação dos dois testes atualizados (Finding 1) e das novas referências de projeto.
2. `dotnet test sim/backend/DropCopySimulator.slnx` — todos os testes, incluindo os novos, verdes.
3. `cd sim/frontend && npm run typecheck` — apenas se algum arquivo de frontend for tocado.
4. Nenhum commit/push/PR; mudanças permanecem no working tree da branch
   `chore/disable-websocket-dispatcher`.

## Riscos e mitigações

- **DI: ordem de registro / ciclo.** Não há ciclo (gerador não conhece o acceptor). `OrderFlowGenerator`
  já é registrado antes de `FixAcceptorService`; a resolução por construtor cobre o novo parâmetro sem
  edição. Verificado por build/startup.
- **Evento `StateChanged` tardio após dispose do gerador no shutdown.** Mitigado capturando
  `ObjectDisposedException` em torno de cada `AttachSession`.
- **`ProjectReference` ao Host no projeto de teste.** O Host é SDK web; a referência é só para alcançar
  o método `internal` via `InternalsVisibleTo` — o teste não sobe o host. Risco baixo; mitigado pela
  escolha do teste de unidade determinístico (sem `WebApplicationFactory`/`Mvc.Testing`).
- **Logon de teste subespecificado.** Mitigado fixando os campos exatos (BeginString, MsgType, Compids
  invertidos, HeartBtInt=30, ResetSeqNumFlag=Y) na seção de testabilidade.

---

## Respostas à revisão de design (`03-design-review.md`)

**Finding 1 (HIGH) — novo parâmetro obrigatório quebra `FixAcceptorServiceTests`.** *Atendido.*
Escolhido parâmetro **obrigatório** `OrderFlowGenerator` (com justificativa na seção "Decisão sobre a
assinatura do construtor"). Os dois call sites em `FixAcceptorServiceTests.cs` são atualizados via
helper `CreateGenerator()`, e o arquivo foi adicionado à tabela "Arquivos tocados". Verifiquei no
fonte que os dois testes usam o construtor de 3 args.

**Finding 2 (HIGH) — plano de teste do Defeito B assume referência ao Host inexistente.** *Atendido.*
Verifiquei que `DropCopySim.Integration.Tests.csproj` só referencia `DropCopySim.Core` e que não há
`Mvc.Testing`/`WebApplicationFactory` no `sim/`. O design agora: (a) descarta `WebApplicationFactory`
e `Microsoft.AspNetCore.Mvc.Testing`; (b) especifica o `ProjectReference` ao Host e o
`InternalsVisibleTo` para `DropCopySim.Integration.Tests`, ambos listados em "Arquivos tocados"; (c)
cobre o Defeito B por teste de unidade de `TryCloseGracefullyAsync` com `WebSocket` substituído.

**Finding 3 (MEDIUM) — Logon de teste subespecificado.** *Atendido.* A seção "Defeito A — teste de
integração" fixa os campos exatos do Logon (CompIDs invertidos: `SenderCompID=Options.TargetCompId`,
`TargetCompID=Options.SenderCompId`; `HeartBtInt=30`; `ResetSeqNumFlag=Y`; `BeginString=FIX.4.4`;
`MsgType=A`) e o uso de `B3FixMessageWriter.WriteB3Header` + `WriteField`. Decisão explícita por
**Logon real** (não FSM forçada), por ser o único caminho que exercita o wiring de ponta a ponta.

**Finding 4 (MEDIUM) — ordem no `finally` e detach condicional sem predicado.** *Atendido.* A ordem
foi fixada: `await DisposeAsync()` → `session.StateChanged -= handler` → `AttachSession(null)`
**incondicional** (idempotente) → `CompareExchange`. O "detach condicional" foi removido, pois o
gerador não expõe a sessão anexada.

**Finding 5 (MEDIUM) — FR-A-03 depende de `AttachSession` seguro em qualquer estado.** *Atendido.* A
seção "Fluxo resultante" agora afirma explicitamente que `AttachSession` é seguro em
Idle/Running/Paused (verificado: só checa `_disposed` + `Volatile.Write`) e cobre os dois sub-casos de
FR-A-03 (sessão-antes-do-start e start-antes-da-sessão), inclusive a verificação de que `Start()` não
limpa `_session`.

**Finding 6 (NIT) — catch interno deve capturar apenas `WebSocketException`.** *Atendido.* Os dois
`catch` do pump e o de `TryCloseGracefullyAsync` capturam **apenas `WebSocketException`**, preservando
a propagação de `OperationCanceledException` para o catch externo e o `finally` (FR-B-03). Explicitado
no código de exemplo e no tratamento de erros.

**Finding 7 (NIT) — texto sobre `Program.cs` redundante.** *Atendido.* A seção `Program.cs` foi
reduzida a uma frase: nenhuma edição; `AddSingleton<FixAcceptorService>()` resolve o novo parâmetro
por reflexão porque `OrderFlowGenerator` já é singleton registrado antes.
