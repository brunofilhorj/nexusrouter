# NexusRouter — Plano de Tarefas Incremental

> **Status do Documento:** Rascunho v1.0  
> **Classificação:** Especificação de Engenharia Interna  
> **Metodologia:** Spec-Driven Development (SDD) — Execução Sequencial de Tarefas

---

## Modelo de Execução

As tarefas são projetadas para ser executadas **uma por vez**. Cada tarefa é autocontida, possui entradas/saídas explícitas e produz um artefato verificável. As tarefas dentro de uma fase devem ser concluídas em ordem; fases posteriores dependem da saída de fases anteriores.

No Kiro, use o task runner desta spec (`nexusrouter`). No Cursor, execute a próxima tarefa `- [ ]` em ordem e marque `- [x]` ao concluir. Não execute duas tarefas em paralelo no mesmo working tree.

**Visão Geral das Fases:**

| Fase | Área de Foco | Tarefas |
|------|-------------|---------|
| Fase 1 | Scaffold da Solução e Domínio | T-01 → T-06 |
| Fase 2 | Ingress (Sessões e Framing) | T-07 → T-12 |
| Fase 3 | Core Router / Hot Path | T-13 → T-18 |
| Fase 4 | Egress (Implementações de Dispatchers) | T-19 → T-25 |
| Fase 5 | Auditoria / Cold Path (Serilog + TimescaleDB) | T-26 → T-28 |
| Fase 6 | Host, Wiring de DI e Configuração | T-29 → T-32 |
| Fase 7 | Observabilidade e Benchmarks | T-33 → T-36 |
| Fase 8 | Testes | T-37 → T-43 |
| Fase 9 | Retransmissão opcional por cliente | T-44 → T-48 |

---

## Fase 1 — Scaffold da Solução e Camada de Domínio

- [x] T-01 — Inicializar a Solução .NET 10 e a Estrutura de Projetos

**Objetivo:** Criar o arquivo de solução e todos os projetos esqueleto com as referências de projeto corretas.

**Entradas:** `design.md` (Seção 7 — Estrutura do Projeto)

**Passos:**
1. Criar `NexusRouter.slnx` na raiz do workspace (formato de solução XML padrão do .NET 10).
2. Criar os seguintes projetos `classlib` em `src/`:
   - `NexusRouter.Domain`
   - `NexusRouter.Ingress`
   - `NexusRouter.Router`
   - `NexusRouter.Egress`
   - `NexusRouter.Audit`
3. Criar `NexusRouter.Host` como projeto `console` em `src/`.
4. Criar os seguintes projetos de teste `xunit` em `tests/`:
   - `NexusRouter.Domain.Tests`
   - `NexusRouter.Ingress.Tests`
   - `NexusRouter.Router.Tests`
   - `NexusRouter.Egress.Tests`
5. Criar `NexusRouter.Benchmarks` como projeto `console` em `tests/`.
6. Conectar todas as referências projeto-a-projeto conforme definido em `design.md`.
7. Adicionar `.editorconfig` e `.gitignore` em nível de solução (template dotnet).
8. Adicionar `Directory.Build.props` na raiz com: `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
9. **Não recriar** `docker-compose.yml`, `docker/postgres/init.sql` nem `.env.example` — já existem na raiz. Garantir que o `.gitignore` continue ignorando `.env` e preservando `.env.example`.

**Saída:** `dotnet build NexusRouter.slnx` conclui com zero avisos.

---

- [x] T-02 — Definir as Enumerações do Núcleo

**Objetivo:** Definir todos os enums compartilhados no projeto de Domínio.

**Entradas:** `requirements.md` (FR-PTH, FR-ING), `design.md` (Seção 4)

**Passos:**
1. Criar `src/NexusRouter.Domain/Enums/DeliveryMode.cs` — valores: `PassThrough`, `Parse`.
2. Criar `src/NexusRouter.Domain/Enums/SessionState.cs` — valores: `Disconnected`, `Connecting`, `LogonSent`, `Active`, `LogoutSent`.
3. Criar `src/NexusRouter.Domain/Enums/DispatchResult.cs` — valores: `Enqueued`, `ChannelFull`, `DispatcherUnhealthy`.
4. Todos os enums devem ter comentários de documentação XML.

**Saída:** Projeto de Domínio compila com os enums visíveis; nenhuma outra alteração.

---

- [x] T-03 — Definir os Value Types do Núcleo: `SessionId`, `FixField`, `RouteContext`

**Objetivo:** Implementar os value types primitivos usados em todo o hot path.

**Entradas:** `design.md` (Seções 4.3, 4.6)

**Passos:**
1. Criar `src/NexusRouter.Domain/Models/SessionId.cs` como `readonly record struct` encapsulando uma `string`. Implementar `IEquatable<SessionId>`, `IComparable<SessionId>` e sobrescrever `GetHashCode` para uso como chave de dicionário.
2. Criar `src/NexusRouter.Domain/Models/FixField.cs` como `readonly struct` com `int Tag` e `ReadOnlyMemory<byte> Value`.
3. Criar `src/NexusRouter.Domain/Models/RouteContext.cs` como `readonly record struct` com `SessionId`, `DeliveryMode` e `ImmutableArray<IMessageDispatcher>` (placeholder de referência antecipada até T-04).
4. Todos os tipos devem ter comentários de documentação XML.

**Saída:** Projeto de Domínio compila. `RouteContext` pode ter um comentário de placeholder para `IMessageDispatcher` até T-04.

---

- [x] T-04 — Definir as Interfaces do Núcleo: `IMessageDispatcher`, `ISessionFrameDecoder`, `IReconnectPolicy`

**Objetivo:** Estabelecer as abstrações primárias que todas as implementações downstream devem satisfazer.

**Entradas:** `design.md` (Seções 4.1, 4.2, 3.1)

**Passos:**
1. Criar `src/NexusRouter.Domain/Abstractions/IMessageDispatcher.cs`:
   - Propriedades: `string DispatcherId`, `bool IsHealthy`.
   - Métodos: `ValueTask StartAsync(CancellationToken)`, `ValueTask StopAsync(CancellationToken)`, `bool TryEnqueue(DispatchPayload payload)`.
   - Evento: `event Action<string, Exception>? OnDispatchError` (dispatcher ID + exceção).
   - Comentários XML completos incluindo `<remarks>` explicando o contrato não bloqueante de `TryEnqueue`.
2. Criar `src/NexusRouter.Domain/Abstractions/ISessionFrameDecoder.cs`:
   - Método: `bool TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame)`.
   - Comentários XML.
3. Criar `src/NexusRouter.Domain/Abstractions/IReconnectPolicy.cs`:
   - Método: `TimeSpan ComputeDelay(int attemptNumber)`.
   - Comentários XML.
4. Atualizar `RouteContext` (T-03) para usar a interface real `IMessageDispatcher`.

**Saída:** Todas as interfaces compilam. `RouteContext` usa a referência real de `IMessageDispatcher`.

---

- [x] T-05 — Definir os Transfer Object Types: `IngressFrame`, `DispatchPayload`, `AuditRecord`

**Objetivo:** Implementar os containers de dados que fluem entre os tiers.

**Entradas:** `design.md` (Seções 4.3, 4.4, FR-AUD-06)

**Passos:**
1. Criar `src/NexusRouter.Domain/Models/IngressFrame.cs` como `ref struct`:
   - Campos: `SessionId SessionId`, `ReadOnlySequence<byte> RawBuffer`, `long IngressTimestampTicks`, `int FrameLength`.
   - Nota: por ser um `ref struct`, não pode implementar interfaces; documentar essa restrição.
2. Criar `src/NexusRouter.Domain/Models/DispatchPayload.cs` como `sealed class` (poolizável):
   - Propriedades: `SessionId`, `DeliveryMode`, `IMemoryOwner<byte>? RawBuffer`, `FixMessage? ParsedMessage`, `long IngressTimestampTicks`, `long RoutingSequence`, `long EgressTimestampTicks`.
   - Método: `void Reset()` — limpa todos os campos para devolução ao pool.
3. Criar `src/NexusRouter.Domain/Models/AuditRecord.cs` como `sealed class`:
   - Propriedades: `SessionId`, `long IngressTimestampTicks`, `string? MsgType`, `string? ClOrdId`, `long? MsgSeqNum`, `DeliveryMode`, `string[] DispatcherIds`, `long EgressTimestampTicks`, `bool WasDropped`, `string? DropReason`, `byte[]? RawFix`.
   - `RawFix` é preenchido no cold-path enqueue (cópia de `ArrayPool` / array próprio do `AuditRecord`), nunca no `RouterEngine` via I/O. `Reset`/pool se aplicável deve soltar a referência.
4. Criar `src/NexusRouter.Domain/Models/FixMessage.cs` como `ref struct`:
   - Propriedades: `ReadOnlySpan<byte> MsgType`, `ReadOnlySpan<FixField> Fields`.
   - Método: `bool TryGetField(int tag, out ReadOnlySpan<byte> value)`.

**Saída:** Todos os tipos compilam. `DispatchPayload.Reset()` limpa todas as referências de objeto para evitar retenção de objetos no pool.

---

- [x] T-06 — Definir `ArrayPoolMemoryOwner<T>` e `DispatchPayloadPool`

**Objetivo:** Implementar os utilitários de pool zero-allocation necessários para o hot path.

**Entradas:** `design.md` (Seção 5.3, discussão sobre ownership de buffer)

**Passos:**
1. Criar `src/NexusRouter.Domain/Pooling/ArrayPoolMemoryOwner.cs` como `sealed class` implementando `IMemoryOwner<byte>`:
   - Construtor recebe `byte[] array` e `int length` de `ArrayPool<byte>`.
   - `Memory<byte> Memory { get; }` retorna `array.AsMemory(0, length)`.
   - `Dispose()` chama `ArrayPool<byte>.Shared.Return(array)`.
   - Guarda de dispose thread-safe usando `Interlocked.Exchange` (seguro contra double-dispose).
2. Criar `src/NexusRouter.Domain/Pooling/RefCountedMemoryOwner.cs` como `sealed class` encapsulando `IMemoryOwner<byte>` com contagem de referências `int`:
   - `AddRef()` — `Interlocked.Increment`.
   - `Release()` — `Interlocked.Decrement`; se chegar a 0, faz dispose do owner interno.
   - Usado durante fan-out para compartilhar um único buffer alugado entre múltiplos dispatchers.
3. Criar `src/NexusRouter.Domain/Pooling/DispatchPayloadPool.cs` encapsulando `Microsoft.Extensions.ObjectPool.ObjectPool<DispatchPayload>`:
   - Usa `DefaultObjectPoolProvider` com `PooledObjectPolicy<DispatchPayload>` que chama `Reset()` no retorno.
   - Método de pré-aquecimento do pool: `Prewarm(int count)`.

**Saída:** Todos os utilitários de pool compilam. `Dispose` é idempotente (seguro contra double-dispose).

---

## Fase 2 — Camada de Ingress

- [x] T-07 — Implementar `ExponentialBackoffReconnectPolicy`

**Objetivo:** Implementar a estratégia de delay de reconexão usada por todas as sessões.

**Entradas:** `requirements.md` (FR-ING-04), `design.md` (Seção 3.1)

**Passos:**
1. Criar `src/NexusRouter.Ingress/Sessions/ExponentialBackoffReconnectPolicy.cs` implementando `IReconnectPolicy`:
   - Parâmetros do construtor: `TimeSpan baseDelay`, `TimeSpan maxDelay`, `double jitterFactor`.
   - `ComputeDelay(int attemptNumber)`: implementa `min(baseDelay * 2^attempt, maxDelay)` com jitter uniforme no intervalo `[-jitterFactor, +jitterFactor]` do delay calculado.
   - Usar `Random.Shared` para jitter (thread-safe no .NET 6+).
   - Limitar resultado ao mínimo de zero.
2. Comentários XML incluindo a fórmula em `<remarks>`.

**Saída:** Implementação testável unitariamente. Sem I/O ou dependências externas.

---

- [x] T-08 — Implementar `FixSessionStateMachine`

**Objetivo:** Implementar a state machine de nível de sessão FIX (ciclo de vida Logon, Heartbeat, Logout, TestRequest).

**Entradas:** `requirements.md` (FR-ING-02, FR-ING-03), `design.md` (Seção 3.1 — Session FSM)

**Passos:**
1. Criar `src/NexusRouter.Ingress/Sessions/FixSessionStateMachine.cs`:
   - Transições de estado impostas usando `Interlocked.CompareExchange` em um campo `SessionState` (cast para `int`).
   - Métodos de transição de estado: `TransitionToConnecting()`, `TransitionToLogonSent()`, `TransitionToActive()`, `TransitionToLogoutSent()`, `TransitionToDisconnected()`. Cada um valida o estado predecessor legal e lança `InvalidSessionStateException` se a transição for ilegal.
   - Rastreamento de heartbeat: `long _lastHeartbeatReceivedTicks`. Atualizado via `Interlocked.Exchange` quando um Heartbeat ou qualquer mensagem de aplicação é recebido (spec FIX: qualquer mensagem recebida reinicia o timer de heartbeat).
   - Método `bool IsHeartbeatStale(int missedHeartbeatThreshold, int heartbeatIntervalSeconds)`: computa staleness sem locking usando `Environment.TickCount64`.
   - Stubs de handlers de mensagens administrativas: `HandleHeartbeat()`, `HandleTestRequest()`, `HandleResendRequest()`, `HandleLogout()` — retornam frames de resposta outbound (como `byte[]`) a serem enviados pela sessão.
2. Criar `src/NexusRouter.Ingress/Sessions/InvalidSessionStateException.cs`.

**Saída:** State machine compila com transições determinísticas. Sem I/O.

---

- [x] T-09 — Implementar `Fix44FrameDecoder` e `Fix50FrameDecoder`

**Objetivo:** Implementar detecção de limite de frame específica do protocolo para FIX 4.x e FIX 5.0/FIXT.

**Entradas:** `requirements.md` (FR-ING-05), `design.md` (Seção 3.1, `ISessionFrameDecoder`)

**Passos:**
1. Criar `src/NexusRouter.Ingress/Decoders/Fix44FrameDecoder.cs` implementando `ISessionFrameDecoder`:
   - Framing FIX: mensagens delimitadas por `8=FIX.4.x\x01` (BeginString) e terminadas por `10=XXX\x01` (CheckSum).
   - `TryDecode`: varrer o `ReadOnlySequence<byte>` pelo terminador do campo checksum `10=` seguido de 3 dígitos e `\x01`. Se encontrado, popular `IngressFrame` com o slice. Avançar o buffer além dos bytes consumidos.
   - Tratar sequências divididas (`ReadOnlySequence<byte>` que abrange múltiplos segmentos) corretamente usando `SequenceReader<byte>`.
   - Zero allocation: toda varredura em `Span<byte>` / `SequenceReader<byte>`; `IngressFrame` é um `ref struct`.
2. Criar `src/NexusRouter.Ingress/Decoders/Fix50FrameDecoder.cs` implementando `ISessionFrameDecoder`:
   - Mesma lógica, mas valida `BeginString` como `FIX.5.0` ou `FIXT.1.1`.
3. Criar `src/NexusRouter.Ingress/Decoders/SbeFrameDecoder.cs` como stub (`throw new NotImplementedException("SBE decoding is a future extension")`) implementando `ISessionFrameDecoder`.

**Saída:** Decoders compilam e tratam sequências de segmento único. `SbeFrameDecoder` é um stub documentado.

---

- [x] T-10 — Implementar `FixSession` — Conexão TCP e Loop de Leitura do Pipe

**Objetivo:** Implementar o ciclo de vida TCP da sessão e o message pump do `PipeReader`.

**Entradas:** `requirements.md` (FR-ING-01 até FR-ING-06), `design.md` (Seções 3.1, 5.1)

**Passos:**
1. Criar `src/NexusRouter.Ingress/Sessions/FixSession.cs` implementando `IHostedService`:
   - Construtor: recebe `SessionOptions`, `ISessionFrameDecoder`, `IReconnectPolicy`, `RouterEngine` (ou interface `IMessageRouter`), `AuditChannel`, `ILogger<FixSession>`.
   - `StartAsync`: lança um `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` para o loop de recebimento principal. Retorna imediatamente após o lançamento.
   - **Loop de conexão** (dentro da task de longa duração):
     - Criar `TcpClient` (ou `Socket` com `SocketType.Stream`).
     - Opcionalmente encapsular em `SslStream` se TLS estiver habilitado.
     - Em conexão bem-sucedida: criar um `Pipe` de `PipeOptions` com pool de buffer pré-alocado, iniciar `FillPipeAsync` (writer) e `ReadPipeAsync` (reader) como tasks concorrentes.
     - Em qualquer exceção: transicionar estado para `Disconnected`, aguardar `ReconnectPolicy.ComputeDelay(attempt)`, incrementar contador de tentativas, repetir.
   - **`FillPipeAsync`**: lê de `Socket.ReceiveAsync(Memory<byte>)` em `PipeWriter.GetMemory()` e chama `PipeWriter.Advance`. Sinaliza `PipeWriter.Complete` ao fechar o socket.
   - **`ReadPipeAsync`**: chama `ISessionFrameDecoder.TryDecode` em loop na saída de `PipeReader.ReadAsync`. Para cada `IngressFrame` decodificado: chama a `SessionFSM` para mensagens admin OU chama `RouterEngine.Route(frame)` para mensagens de aplicação.
   - `StopAsync`: define o cancellation token, envia FIX Logout, aguarda a task de longa duração com timeout.
2. A classe deve ser sealed.

**Saída:** Sessão conecta, lê o pipe e faz handoff de frames. O caminho TLS está conectado, mas testado separadamente.

---

- [x] T-11 — Implementar o Handshake de Logon FIX e o Timer de Heartbeat

**Objetivo:** Completar o protocolo FIX de nível de sessão: envio/recebimento de Logon, geração de Heartbeat, TestRequest em caso de staleness.

**Entradas:** `requirements.md` (FR-ING-02, FR-ING-03)

**Passos:**
1. Em `FixSession`, após conexão TCP bem-sucedida:
   - Construir e enviar uma mensagem FIX Logon (MsgType=A) usando um serializador FIX mínimo zero-allocation (`FixMessageBuilder` — um builder simples baseado em stack que escreve diretamente em um `byte[]` alugado).
   - Aguardar uma resposta de Logon com timeout configurável; se não recebida, fechar a conexão e reconectar.
   - Transicionar a FSM para `Active` no Logon confirmado.
2. Iniciar um `Timer` (ou `PeriodicTimer` .NET 6+) para geração de heartbeat:
   - A cada `HeartBtInt` segundos, escrever um FIX Heartbeat (MsgType=0) no pipe outbound.
   - Se `IsHeartbeatStale(...)` retornar true: escrever um FIX TestRequest (MsgType=1); se não houver resposta após um `HeartBtInt` completo, desconectar e reconectar.
3. Criar `src/NexusRouter.Ingress/Sessions/FixMessageBuilder.cs`:
   - Método: `Span<byte> BuildLogon(Span<byte> buffer, SessionOptions options)` — escreve `8=FIX.x.x\x019=...\x0135=A\x0149=...\x0156=...\x0134=...\x0198=0\x01108=...\x0110=...\x01`.
   - Método: `Span<byte> BuildHeartbeat(...)`.
   - Método: `Span<byte> BuildTestRequest(...)`.
   - Método: `Span<byte> BuildLogout(...)`.
   - Todos os métodos escrevem em um `Span<byte>` fornecido pelo chamador e retornam o slice preenchido. Zero allocation.

**Saída:** As sessões completam o handshake de Logon e mantêm heartbeats de forma autônoma.

---

- [x] T-12 — Implementar a Publicação de Eventos de Ciclo de Vida da Sessão

**Objetivo:** Emitir eventos de ciclo de vida da sessão para o channel de auditoria sem acoplamento ao hot path.

**Entradas:** `requirements.md` (FR-ING-07, FR-AUD-05)

**Passos:**
1. Definir o record `SessionLifecycleEvent` em `NexusRouter.Domain/Models/` com: `SessionId`, `SessionState NewState`, `DateTimeOffset Timestamp`, `string? Detail`.
2. Estender `AuditRecord` (T-05) com abordagem de union discriminada: adicionar propriedade `SessionLifecycleEvent? LifecycleEvent` (nullable). Quando definida, os campos de roteamento/despacho são nulos.
3. Em `FixSession`, a cada transição de estado, construir um `SessionLifecycleEvent` e chamar `AuditChannel.TryWrite(new AuditRecord { LifecycleEvent = evt })` — não bloqueante.
4. Emitir entradas de log estruturado `ILogger` a cada evento de ciclo de vida (nível Information).

**Saída:** Eventos de ciclo de vida fluem para o channel de auditoria; visíveis na saída de log estruturado.

---

## Fase 3 — Core Router / Hot Path

- [x] T-13 — Implementar `PassThroughRewriter`

**Objetivo:** Reescrita in-place de `MsgSeqNum (34)` e `CheckSum (10)` em um `Span<byte>` sem alocação.

**Entradas:** `requirements.md` (FR-PTH-01 até FR-PTH-03), `design.md` (Seção 3.2)

**Passos:**
1. Criar `src/NexusRouter.Router/PassThroughRewriter.cs` como `static class`:
   - Método: `void Rewrite(Span<byte> buffer, long newSeqNum)`:
     - Varrer pelo padrão `\x0134=` usando `MemoryExtensions.IndexOf` em spans.
     - Sobrescrever os bytes de dígitos decimais do `MsgSeqNum` in-place. Tratar largura do campo: preencher com espaços à esquerda se o novo valor for mais curto, ou falhar rápido se for maior que o original (registrar aviso, não corromper o frame).
     - Após reescrever a `tag 34`, recomputar o checksum FIX: somar todos os bytes desde o início da mensagem até (não incluindo) o campo `10=`, módulo 256, escrever como 3 dígitos ASCII no campo `10=` in-place.
   - Todas as operações em `Span<byte>` — zero alocação heap.
   - Método: `static int ComputeChecksum(ReadOnlySpan<byte> buffer)` — utilitário interno.
2. Comentários XML com referências à spec FIX para o cálculo do checksum.

**Saída:** O rewriter reescreve corretamente `MsgSeqNum` e `CheckSum` em um array de bytes de frame FIX válido. Verificável com teste unitário em T-38.

---

- [x] T-14 — Implementar `FixTokenizer`

**Objetivo:** Tokenizador de campos FIX zero-allocation para o modo de entrega `Parse`.

**Entradas:** `requirements.md` (FR-PTH-04), `design.md` (Seção 4.5)

**Passos:**
1. Criar `src/NexusRouter.Router/FixTokenizer.cs` como `static class`:
   - Método: `FixMessage Tokenize(ReadOnlySpan<byte> buffer, Span<FixField> fieldBuffer)`:
     - Analisar pares `tag=value\x01` sequencialmente no `Span<FixField>` fornecido pelo chamador.
     - `FixField.Value` é um slice `ReadOnlySpan<byte>` no `buffer` original — sem cópia.
     - Parar após preencher `fieldBuffer` (o chamador controla a contagem máxima de campos).
     - Extrair referência ao campo `MsgType` (tag 35) para o `FixMessage` retornado.
   - A assinatura do método recebe um `Span<FixField>` do chamador (seja `stackalloc` ou poolizado) — é assim que zero-allocation é garantido.
2. Documentar a restrição de que os campos de `FixMessage` apontam para o `buffer` original — os chamadores não devem devolver o buffer alugado antes de consumir o `FixMessage`.

**Saída:** O tokenizador analisa mensagens FIX padrão. O `FixMessage` retornado contém referências tag-valor válidas.

---

- [x] T-15 — Implementar `RouteResolver`

**Objetivo:** Construir a tabela de roteamento imutável a partir da configuração e expor lookup O(1) lock-free.

**Entradas:** `design.md` (Seção 3.2, uso de `FrozenDictionary`), schema de configuração (Seção 6)

**Passos:**
1. Criar `src/NexusRouter.Router/RouteResolver.cs`:
   - Construtor: recebe `IReadOnlyList<RouteContext>` (construído pelo host de DI a partir da configuração).
   - Internamente constrói um `FrozenDictionary<SessionId, RouteContext>` (usando `System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary()`).
   - Método: `bool TryResolve(SessionId sessionId, out RouteContext context)` — wrapper fino sobre `FrozenDictionary.TryGetValue`.
   - Registrar aviso na inicialização para qualquer ID de sessão referenciado na config que não tenha dispatcher correspondente registrado.
2. Comentários XML.

**Saída:** `RouteResolver` compila. Pode ser construído com uma tabela de roteamento de teste para testes unitários.

---

- [x] T-16 — Implementar `FanOutEngine`

**Objetivo:** Despachar um `DispatchPayload` para todos os dispatchers registrados em uma rota, de forma não bloqueante.

**Entradas:** `requirements.md` (FR-EGR-04, FR-EGR-05), `design.md` (Seção 3.2, Seção 4.4 — ownership de buffer)

**Passos:**
1. Criar `src/NexusRouter.Router/FanOutEngine.cs`:
   - Construtor: recebe `Channel<AuditRecord> auditChannel`, `ILogger<FanOutEngine>`.
   - Método: `void Dispatch(ref RouteContext route, DispatchPayload payload)`:
     - Se `route.Dispatchers.Length == 1`: chamar `dispatcher.TryEnqueue(payload)` diretamente (sem contagem de referências necessária).
     - Se `route.Dispatchers.Length > 1`: encapsular `payload.RawBuffer` em um `RefCountedMemoryOwner` com contagem inicial = N. Substituir `payload.RawBuffer` pelo wrapper com ref-count. Para cada dispatcher após o primeiro, alugar um novo `DispatchPayload` do pool, copiar campos escalares, atribuir o mesmo `RefCountedMemoryOwner` (chamando `AddRef()`), e chamar `TryEnqueue` em cada dispatcher.
     - Para cada `TryEnqueue` que retornar `false` (channel cheio): chamar `RefCountedMemoryOwner.Release()` no payload daquele dispatcher, depois `auditChannel.TryWrite` com um `AuditRecord` de overflow. Não lançar exceção.
2. Comentários XML explicando o protocolo de ownership com ref-counting.

**Saída:** Fan-out despacha para todos os dispatchers saudáveis. Um channel de dispatcher cheio dispara um registro de auditoria, não uma exceção.

---

- [x] T-17 — Implementar `RouterEngine`

**Objetivo:** Compor o pipeline de roteamento do hot path em um único ponto de entrada.

**Entradas:** `design.md` (Seção 3.2, fluxo de dados end-to-end na Seção 8)

**Passos:**
1. Criar `src/NexusRouter.Router/RouterEngine.cs`:
   - Construtor: recebe `RouteResolver`, `FanOutEngine`, `DispatchPayloadPool`, `ILogger<RouterEngine>`.
   - Método: `void Route(in IngressFrame frame)`:
     1. Chamar `routeResolver.TryResolve(frame.SessionId, out var route)`. Se não encontrado, registrar aviso e retornar.
     2. Switch em `route.DeliveryMode`:
        - `PassThrough`: alugar buffer do `ArrayPool`, copiar `frame.RawBuffer` nele, chamar `PassThroughRewriter.Rewrite(Span<byte>, nextSeqNum)`, encapsular em `ArrayPoolMemoryOwner`, popular `DispatchPayload` do pool.
        - `Parse`: alugar buffer, copiar, chamar `FixTokenizer.Tokenize(...)` com `stackalloc FixField[64]` (ou fallback para 128 se necessário), popular `DispatchPayload.ParsedMessage`.
     3. Definir `payload.EgressTimestampTicks = Environment.TickCount64`.
     4. Chamar `fanOutEngine.Dispatch(ref route, payload)`.
   - O método é síncrono (`void`) — deliberadamente, para manter o hot path livre de overhead de state-machine async.
2. Comentários XML.

**Saída:** `RouterEngine.Route` pode ser chamado do loop de leitura do pipe da sessão e trata ambos os modos de entrega.

---

- [x] T-18 — Implementar a Interface `IMessageRouter` e Conectar Session ↔ Router

**Objetivo:** Desacoplar `FixSession` do `RouterEngine` concreto via interface.

**Entradas:** `design.md` (Seção 3.1 — Ingress referencia o Router via abstração)

**Passos:**
1. Criar `src/NexusRouter.Domain/Abstractions/IMessageRouter.cs`:
   - Método: `void Route(in IngressFrame frame)`.
   - Comentários XML.
2. Fazer `RouterEngine` implementar `IMessageRouter`.
3. Atualizar o construtor de `FixSession` (T-10) para aceitar `IMessageRouter` em vez do `RouterEngine` concreto.
4. Verificar que `dotnet build` passa.

**Saída:** `FixSession` depende apenas de `IMessageRouter`. Separação limpa entre as camadas Ingress e Router.

---

## Fase 4 — Camada de Egress

- [x] T-19 — Implementar a Classe Base Abstrata `DispatcherBase<TOptions>`

**Objetivo:** Fornecer o consume loop de background comum, gerenciamento de ciclo de vida e tratamento de erros compartilhados por todos os dispatchers.

**Entradas:** `requirements.md` (FR-EGR-07, FR-EGR-10), `design.md` (Seção 3.3)

**Passos:**
1. Criar `src/NexusRouter.Egress/DispatcherBase.cs` como `abstract class` implementando `IMessageDispatcher`:
   - Construtor: recebe `string dispatcherId`, `int channelCapacity`, `ILogger`.
   - Cria `Channel<DispatchPayload>.CreateBounded(new BoundedChannelOptions(channelCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false })`.
   - `IsHealthy`: `volatile bool`, inicialmente `false`, definido como `true` em `StartAsync` e `false` em erro fatal.
   - `TryEnqueue(DispatchPayload payload)`: chama `_channel.Writer.TryWrite(payload)` e retorna o resultado.
   - `StartAsync`: define `IsHealthy = true`, inicia o consume loop de background via `Task.Run`.
   - `StopAsync`: chama `_channel.Writer.Complete()`, aguarda a task de consumo com timeout, define `IsHealthy = false`.
   - **Consume loop**: `await foreach (var payload in _channel.Reader.ReadAllAsync(ct))` → chama `abstract ValueTask SendAsync(DispatchPayload payload, CancellationToken ct)` → em exceção, chama `OnDispatchError?.Invoke(DispatcherId, ex)`, registra a exceção, NÃO relança → sempre chama `payload.RawBuffer?.Dispose()` em um bloco `finally`.
   - `event Action<string, Exception>? OnDispatchError` — implementação.
2. Método abstrato: `protected abstract ValueTask SendAsync(DispatchPayload payload, CancellationToken ct)`.
3. Método abstrato: `protected abstract ValueTask ConnectAsync(CancellationToken ct)` — chamado antes do início do consume loop.

**Saída:** `DispatcherBase` compila. Dispatchers concretos precisam apenas implementar `ConnectAsync` e `SendAsync`.

---

- [x] T-20 — Implementar `KafkaMessageDispatcher`

**Objetivo:** Dispatcher de egress para Kafka usando `Confluent.Kafka`.

**Entradas:** `requirements.md` (FR-EGR-08, FR-EGR-11)

**Passos:**
1. Adicionar o pacote NuGet `Confluent.Kafka` (versão fixada) ao `NexusRouter.Egress.csproj`.
2. Criar `src/NexusRouter.Egress/Kafka/IKafkaPartitionStrategy.cs`:
   - Método: `int? GetPartition(DispatchPayload payload)` — retorna null para particionamento automático.
3. Criar `src/NexusRouter.Egress/Kafka/ByClOrdIdPartitionStrategy.cs` implementando `IKafkaPartitionStrategy`:
   - Extrai `ClOrdId` (tag 11) de `ParsedMessage` se disponível, aplica hash módulo `partitionCount` (parâmetro do construtor). Retorna null se o campo não estiver presente.
4. Criar `src/NexusRouter.Egress/Kafka/KafkaMessageDispatcher.cs` estendendo `DispatcherBase<KafkaDispatcherOptions>`:
   - `ConnectAsync`: constrói `ProducerConfig` a partir das options (sem `if (aws)`). Mapeia `SecurityProtocol` e `SaslMechanism` para os enums do `Confluent.Kafka`. Lê usuário/senha SASL apenas das env vars nomeadas. Se `SaslMechanism == AwsMskIam`, registra o callback OAUTHBEARER do pacote AWS MSK Auth; caso contrário o pacote não é exigido em runtime.
   - `SendAsync`: chama `_producer.Produce(topic, new Message<Null, byte[]> { Value = payload.RawBuffer.Memory.ToArray() })` em modo fire-and-forget (sobrecarga `Produce` não async para latência mínima quando `Acks=0`). Para modos de ack maiores, usar `ProduceAsync`.
   - Tratar `ProduceException<Null, byte[]>` — invocar `OnDispatchError`.
5. Criar `src/NexusRouter.Egress/Kafka/KafkaDispatcherOptions.cs` com: `BootstrapServers`, `Topic`, `ClientId`, `Acks` (int), `SecurityProtocol`, `SaslMechanism`, `SaslUsernameEnvVar`, `SaslPasswordEnvVar`, `SslCaLocation`, `EnableSslCertificateVerification`, `ChannelCapacity`, `PartitionStrategy` (enum string). Validar: `SaslSsl`/`SaslPlaintext` exigem mechanism ≠ `None` e env vars quando o mechanism usa usuário/senha.

**Saída:** `KafkaMessageDispatcher` compila e pode ser registrado como `IMessageDispatcher`.

---

- [x] T-21 — Implementar `RabbitMQMessageDispatcher`

**Objetivo:** Dispatcher de egress para RabbitMQ usando `RabbitMQ.Client`.

**Entradas:** `requirements.md` (FR-EGR-09)

**Passos:**
1. Adicionar o pacote NuGet `RabbitMQ.Client` (versão fixada) ao `NexusRouter.Egress.csproj`.
2. Criar `src/NexusRouter.Egress/RabbitMQ/RabbitMQMessageDispatcher.cs` estendendo `DispatcherBase<RabbitMQDispatcherOptions>`:
   - `ConnectAsync`: cria `ConnectionFactory`, abre `IConnection`, abre `IModel` dedicado (um model por dispatcher — conforme FR-EGR-09). Define `BasicQos` se configurado.
   - `SendAsync`: chama `_model.BasicPublish(exchange, routingKey, mandatory: false, basicProperties: null, body: payload.RawBuffer.Memory)`.
   - Em `AlreadyClosedException` ou `BrokerUnreachableException`: tentar reconectar via `ConnectAsync` uma vez; se falhar, invocar `OnDispatchError`.
3. Criar `src/NexusRouter.Egress/RabbitMQ/RabbitMQDispatcherOptions.cs` com: `Host`, `Port`, `VirtualHost`, `Username` (de env), `Password` (de env), `Exchange`, `RoutingKey`, `ChannelCapacity`.
4. Credenciais (`Username`, `Password`) DEVEM ser obtidas de variáveis de ambiente ou injeção de secrets de `IConfiguration` — nunca hardcoded.

**Saída:** `RabbitMQMessageDispatcher` compila e pode ser registrado como `IMessageDispatcher`.

---

- [x] T-22 — Implementar `FixOutboundDispatcher` — Acceptor FIX para Clientes Downstream

**Objetivo:** Dispatcher FIX que escuta e re-emite mensagens para cada cliente FIX conectado, com sequence number próprio.

**Entradas:** `requirements.md` (FR-EGR-03), `design.md` (Seção 3.3)

**Passos:**
1. Criar `src/NexusRouter.Egress/Fix/FixOutboundDispatcher.cs` estendendo `DispatcherBase<FixOutboundDispatcherOptions>`:
   - `ConnectAsync`: abre `TcpListener` em `ListenAddress`/`ListenPort`. Não conecta em cliente. Opcionalmente encapsula cada socket aceito em TLS de servidor.
   - Aceita N clientes. O primeiro frame de cada um é Logon. TargetCompID (tag 56) tem de ser o `SenderCompId` do acceptor. Cada Logon aceito vira uma `FixAcceptedSession` com fila e MsgSeqNum próprios.
   - `SendAsync`: para cada sessão logada, copia o frame e aplica o MsgSeqNum daquela sessão. `PassThrough` usa `PassThroughRewriter`. `Parse` usa `FixMessageSerializer` com o SenderCompID do acceptor e o TargetCompID do cliente. Falha ou fila cheia de uma sessão não bloqueia as outras. Sem sessão logada, descarta o payload neste dispatcher.
2. Criar `src/NexusRouter.Egress/Fix/FixOutboundDispatcherOptions.cs` com endereço de escuta, não host de destino.

**Saída:** `FixOutboundDispatcher` escuta e mantém uma sessão FIX independente por cliente conectado, cada uma com sequence number outbound próprio.

---

- [x] T-23 — Implementar `FixMessageSerializer` (Serialização Outbound para Modo Parse)

**Objetivo:** Serializar um value type `FixMessage` de volta para `byte[]` no formato wire para dispatchers outbound operando em modo `Parse`.

**Entradas:** `design.md` (Seção 3.3 — resserialiação no modo Parse em `FixOutboundDispatcher`)

**Passos:**
1. Criar `src/NexusRouter.Egress/Fix/FixMessageSerializer.cs` como `static class`:
   - Método: `int Serialize(in FixMessage message, Span<byte> destination, long seqNum, string senderCompId, string targetCompId)`:
     - Escreve os campos padrão do header FIX (BeginString, BodyLength, MsgType, SenderCompID, TargetCompID, MsgSeqNum, SendingTime).
     - Copia os campos do body de `FixMessage.Fields` excluindo os campos de header (tag 8, 9, 35, 49, 56, 34, 52).
     - Anexa o trailer padrão (CheckSum, tag 10).
     - Todas as escritas em `Span<byte>` — sem alocação de string. Retorna o número de bytes escritos.
2. Comentários XML.

**Saída:** `FixMessageSerializer` produz uma representação em bytes de mensagem FIX válida e parseável.

---

- [x] T-24 — Implementar Suporte a TLS para Sessões Inbound e Outbound

**Objetivo:** Adicionar mTLS TLS 1.2/1.3 opcional ao `FixSession` (inbound) e `FixOutboundDispatcher` (outbound).

**Entradas:** `requirements.md` (NFR-SEC-01)

**Passos:**
1. Criar `src/NexusRouter.Ingress/Sessions/TlsSocketFactory.cs`:
   - Método: `static async ValueTask<Stream> WrapAsync(TcpClient client, TlsOptions options, CancellationToken ct)`:
     - Se `options.Enabled == false`: retornar `client.GetStream()` diretamente.
     - Se habilitado: carregar `X509Certificate2` de `options.CertificatePath` usando `options.CertificatePasswordEnvVar` como nome da variável de ambiente para a senha.
     - Criar `SslStream`, chamar `AuthenticateAsClientAsync` com `SslClientAuthenticationOptions` (TLS 1.2 mínimo).
2. Usar `TlsSocketFactory.WrapAsync` em `FixSession.ConnectAsync` quando TLS estiver configurado. No `FixOutboundDispatcher`, usar `TlsSocketFactory.WrapServerAsync` em cada socket aceito.
3. NÃO registrar em log nem expor material de chave privada de certificado.

**Saída:** Sessões conectam com sucesso via TLS quando configurado. O caminho plaintext não é alterado.

---

- [x] T-25 — Implementar Gerenciamento de Sequence Number por Dispatcher

**Objetivo:** Garantir que cada sessão aceita pelo `FixOutboundDispatcher` rastreie seu próprio contador de `MsgSeqNum` outbound, com persistência para recuperação de crash. O contador é independente da sessão inbound e das outras sessões aceitas.

**Entradas:** `requirements.md` (FR-EGR-03), restrições de design (sem I/O síncrono no hot path; seqnum persistido de forma async)

**Passos:**
1. Criar `src/NexusRouter.Egress/Fix/SequenceNumberStore.cs`:
   - Lê o último sequence number conhecido de um arquivo flat local (um arquivo por chave de sessão — id do dispatcher mais o SenderCompID do cliente —, armazenado em diretório configurável) durante `LoadAsync(string dispatcherId)` no Logon.
   - Persiste o sequence number atual no arquivo de forma assíncrona via `PeriodicTimer` em background (ex.: a cada 5 segundos) e em `StopAsync`.
   - O contador em memória (`long`) é acessado exclusivamente via `Interlocked.Increment` no hot path; a persistência é somente cold-path.
2. Integrar `SequenceNumberStore` em cada `FixAcceptedSession`: carregar no Logon e gravar ao encerrar a sessão. `FixOutboundDispatcher.StopAsync` encerra todas as sessões aceitas.

**Saída:** Sequence numbers outbound de cada cliente sobrevivem a reinicializações do processo sem perda.

---

## Fase 5 — Auditoria / Cold Path

- [ ] T-26 — Implementar `AuditChannelSingleton`

**Objetivo:** Criar o `Channel<AuditRecord>` compartilhado usado em todo o sistema como mecanismo de desacoplamento entre hot path e audit writer.

**Entradas:** `requirements.md` (FR-AUD-02, FR-AUD-03)

**Passos:**
1. Criar `src/NexusRouter.Audit/AuditChannelSingleton.cs`:
   - Expõe um `static Channel<AuditRecord> Instance` (ou registrado como singleton de DI).
   - Configuração: capacidade limitada de `AuditOptions.ChannelCapacity`, `FullMode` de `AuditOptions.OverflowMode` (mapeado para `BoundedChannelFullMode`).
   - `SingleWriter = false` (múltiplas sessões e dispatchers escrevem), `SingleReader = true` (o audit writer é o único consumidor).
2. Expor o `ChannelWriter<AuditRecord>` como um wrapper `AuditChannelWriter` injetável separado para evitar vazar o `Channel<T>` completo para os writers do hot path.

**Saída:** `AuditChannelWriter` é injetável. Todos os writers usam `TryWrite`; nenhum `await` envolvido.

---

- [ ] T-27 — Implementar `AuditWriterService`

**Objetivo:** Serviço hosted de background que drena o channel de auditoria e persiste os registros.

**Entradas:** `requirements.md` (FR-AUD-04, FR-AUD-07, FR-AUD-08, FR-AUD-09, FR-AUD-10)

**Passos:**
1. Criar `src/NexusRouter.Audit/Sinks/IAuditSink.cs` com `Name`, `StartAsync`, `WriteBatchAsync(ReadOnlyMemory<AuditRecord>, CancellationToken)`, `StopAsync`.
2. Criar `src/NexusRouter.Audit/Sinks/SerilogAuditSink.cs`: cada registro do lote vira um evento estruturado (propriedades nomeadas, sem concatenar string). Não logar `RawFix` em `Information` ou acima.
3. Criar `src/NexusRouter.Audit/Sinks/PostgresAuditSink.cs`:
   - Pacote NuGet `Npgsql` (versão fixada) em `NexusRouter.Audit.csproj`.
   - `StartAsync`: abre `NpgsqlDataSource` a partir das options (host/port/database + user/password via env). Executa um `SELECT 1` de health. Não cria schema em runtime — o schema vem de `docker/postgres/init.sql` (local) ou migração equivalente em AWS.
   - `WriteBatchAsync`: `COPY` binário (ou `BeginBinaryImport`) para `ingress_fix_messages`. Respeitar `PersistMode`: omitir `raw_fix` quando `MetadataOnly`; em `Sampled`, persistir `raw_fix` só na fração configurada.
   - Falhas: logar via `ILogger`, não relançar para o writer (os outros sinks do lote ainda rodam).
4. Criar `src/NexusRouter.Audit/AuditWriterService.cs` implementando `IHostedService`:
   - Construtor: recebe `Channel<AuditRecord>`, `IReadOnlyList<IAuditSink>`, `ILogger<AuditWriterService>`, `AuditOptions`.
   - `StartAsync`: `StartAsync` em cada sink; inicia `Task` que lê via `ReadAllAsync`.
   - Batching: coletar até `options.BatchSize` ou `options.FlushIntervalMs`; então `WriteBatchAsync` em cada sink (try/catch por sink).
   - Drenagem graciosa em `StopAsync`: drenar o channel até vazio (timeout absoluto 10s), flush final, `StopAsync` nos sinks.
5. Comentários XML: proibir qualquer chamada a `IAuditSink` a partir do hot path.

**Saída:** Metadados no Serilog e mensagens FIX (conforme `PersistMode`) na hypertable TimescaleDB; channel drenado no shutdown.

---

- [ ] T-28 — Implementar a Integração de Auditoria de Erros dos Dispatchers

**Objetivo:** Conectar `IMessageDispatcher.OnDispatchError` e eventos de overflow de channel cheio ao channel de auditoria.

**Entradas:** `requirements.md` (FR-EGR-10, FR-AUD-01)

**Passos:**
1. Criar `src/NexusRouter.Audit/DispatcherAuditObserver.cs`:
   - Construtor: recebe `AuditChannelWriter`, `IEnumerable<IMessageDispatcher>`.
   - Subscreve em `OnDispatchError` para cada dispatcher.
   - No evento: constrói um `AuditRecord` com `WasDropped = true`, `DropReason = ex.Message`, chama `auditChannelWriter.TryWrite(record)`.
2. Registrar `DispatcherAuditObserver` como serviço hosted para que as subscrições sejam estabelecidas na inicialização e limpas em `StopAsync`.

**Saída:** Erros de dispatcher aparecem no log de auditoria sem nenhum acoplamento ao hot path.

---

## Fase 6 — Host, Wiring de DI e Configuração

- [ ] T-29 — Implementar as Classes de Options de Configuração

**Objetivo:** Definir POCOs de configuração fortemente tipados para todo o sistema.

**Entradas:** `design.md` (Seção 6 — Modelo de Configuração)

**Passos:**
1. Criar `src/NexusRouter.Host/Configuration/` com:
   - `NexusRouterOptions.cs`: raiz de options de nível superior.
   - `SessionOptions.cs`: todas as configurações de nível de sessão (host, porta, comp IDs, TLS, reconexão, delivery mode, tipo de frame decoder, rotas).
   - `TlsOptions.cs`: configurações específicas de TLS.
   - `ReconnectPolicyOptions.cs`: parâmetros de backoff.
   - `DispatcherOptions.cs`: classe base / union discriminada para options de Kafka, RabbitMQ e FIX outbound.
   - `KafkaDispatcherOptions.cs`, `RabbitMQDispatcherOptions.cs`, `FixOutboundDispatcherOptions.cs` (Kafka: campos de FR-EGR-11 / design Seção 6).
   - `AuditOptions.cs`: channel, overflow, batch, persist mode, lista de sinks.
   - `PostgresAuditSinkOptions.cs`: host, port, database, env vars de credencial, SslMode.
2. Todos os POCOs implementam `IValidateOptions<T>` ou usam atributos `DataAnnotations` para validação na inicialização.
3. Nenhum secret em qualquer POCO — credenciais referenciam apenas nomes de variáveis de ambiente.

**Saída:** POCOs de configuração compilam. `dotnet run` com config obrigatória ausente falha rapidamente com um erro de validação claro.

---

- [ ] T-30 — Implementar as Extensions de Registro de DI

**Objetivo:** Conectar todos os serviços ao `IServiceCollection` de forma limpa e organizada.

**Entradas:** `design.md` (Seções 3.1–3.4, 5.1), todas as tarefas anteriores

**Passos:**
1. Criar `src/NexusRouter.Host/Extensions/ServiceCollectionExtensions.cs`:
   - `AddNexusRouterIngress(this IServiceCollection, IConfiguration)`: registra implementações de `ISessionFrameDecoder` (com chave por nome de tipo de decoder), `ExponentialBackoffReconnectPolicy`, um `FixSession` `IHostedService` por sessão configurada.
   - `AddNexusRouterRouter(this IServiceCollection, IConfiguration)`: registra `RouterEngine` como `IMessageRouter` (singleton), `RouteResolver` (singleton), `FanOutEngine` (singleton), `DispatchPayloadPool` (singleton).
   - `AddNexusRouterEgress(this IServiceCollection, IConfiguration)`: registra cada dispatcher (Kafka, RabbitMQ, FixOutbound) com base em `DispatcherOptions.Type`. Cada dispatcher registrado tanto como tipo concreto quanto como `IMessageDispatcher` (registro nomeado).
   - `AddNexusRouterAudit(this IServiceCollection, IConfiguration)`: registra `AuditChannelSingleton`, sinks (`SerilogAuditSink`, `PostgresAuditSink` conforme `Audit:Sinks`), `AuditWriterService` como `IHostedService`, `DispatcherAuditObserver` como `IHostedService`.
2. Objetos `RouteContext` são construídos a partir de `SessionOptions.Routes` resolvendo instâncias de dispatcher pelo nome no container de DI.

**Saída:** Todos os serviços registrados. `dotnet run` inicia sem erros de resolução de DI (com um `appsettings.json` válido).

---

- [ ] T-31 — Implementar `Program.cs` e o Entry Point da Aplicação

**Objetivo:** Inicializar o Generic Host com todos os serviços registrados e configurar Serilog.

**Entradas:** T-29, T-30

**Passos:**
1. Criar `src/NexusRouter.Host/Program.cs`:
   - Usar o padrão `Host.CreateDefaultBuilder(args)` / `WebApplication.CreateBuilder`.
   - Configurar Serilog a partir de `appsettings.json` (`UseSerilog`).
   - Chamar todos os métodos de extensão `AddNexusRouter*`.
   - Adicionar `UseConsoleLifetime()` para shutdown limpo via Ctrl+C.
2. Criar `src/NexusRouter.Host/appsettings.json` com uma configuração de exemplo completa alinhada ao `docker-compose.yml` (`localhost:19092` Kafka, `localhost:5672` RabbitMQ, `localhost:5432` Postgres, sem credenciais reais — só nomes de env vars).
3. Adicionar pacotes NuGet do Serilog: `Serilog.AspNetCore` ou `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` (versões fixadas).

**Saída:** `dotnet run` inicia a aplicação, registra mensagens de startup e as sessões começam a tentar conectar.

---

- [ ] T-32 — Implementar a Sequência de Shutdown Gracioso

**Objetivo:** Impor a sequência de shutdown definida em NFR-REL-04.

**Entradas:** `requirements.md` (NFR-REL-04)

**Passos:**
1. Verificar que o Generic Host do .NET para instâncias de `IHostedService` na ordem inversa de registro. Ajustar a ordem de registro em T-30 para garantir: sessões de Ingress param primeiro → dispatchers de Egress drenam → Audit writer drena por último.
2. Em `FixSession.StopAsync`: implementar envio de Logout + drenagem com `ShutdownTimeoutSeconds` configurável.
3. Em `AuditWriterService.StopAsync`: drenar o channel completamente antes de retornar (com timeout absoluto de 10s como rede de segurança).
4. Adicionar um handler `AppDomain.UnhandledException` de nível superior que registra a exceção no Serilog antes do processo sair.
5. Adicionar `Environment.Exit(1)` apenas no handler de exceção não tratada — não usar para shutdown normal.

**Saída:** `dotnet run` → Ctrl+C produz um log de shutdown limpo com todos os serviços parando na ordem correta.

---

## Fase 7 — Observabilidade e Benchmarks

- [ ] T-33 — Implementar Instrumentação com `System.Diagnostics.Metrics`

**Objetivo:** Adicionar métricas de runtime para throughput, latência e profundidade de fila.

**Entradas:** `requirements.md` (NFR-OBS-01)

**Passos:**
1. Criar `src/NexusRouter.Router/NexusRouterMetrics.cs`:
   - Criar um `Meter` chamado `"NexusRouter"`.
   - Contadores: `messages_received_total` (tag por sessão), `messages_dispatched_total` (tag por dispatcher), `messages_dropped_total` (tag por dispatcher, tag de motivo).
   - Histogramas: `hotpath_latency_microseconds` (fronteira: ingress timestamp → fan-out completo).
   - Gauges: `dispatcher_channel_depth` (por dispatcher, sondado via `ObservableGauge<int>`).
   - Pausa de GC: usar `GC.RegisterForFullGCNotification` existente ou confiar nas métricas de GC integradas do `dotnet-counters`.
2. Instrumentar `RouterEngine.Route(...)` com o histograma de latência.
3. Instrumentar `FanOutEngine.Dispatch(...)` com o contador de descarte.

**Saída:** Métricas visíveis via `dotnet-counters monitor --name NexusRouter`.

---

- [ ] T-34 — Implementar Endpoint de Métricas OpenTelemetry / Prometheus

**Objetivo:** Expor métricas no formato de scrape Prometheus.

**Entradas:** `requirements.md` (NFR-OBS-03)

**Passos:**
1. Adicionar pacotes NuGet: `OpenTelemetry.Exporter.Prometheus.AspNetCore` (ou `prometheus-net.AspNetCore`), versão fixada.
2. Em `Program.cs`, adicionar host HTTP ASP.NET Core mínimo junto ao Generic Host (ou usar `IHostBuilder` com `ConfigureWebHostDefaults`).
3. Expor endpoint `/metrics` usando middleware de exportação Prometheus.
4. Configurar intervalo de scrape e porta em `appsettings.json`.

**Saída:** `curl http://localhost:9090/metrics` retorna contadores e histogramas no formato Prometheus.

---

- [ ] T-35 — Adicionar Instrumentação de Tracing com `ActivitySource`

**Objetivo:** Adicionar spans de rastreamento distribuído nas fronteiras chave do hot path.

**Entradas:** `requirements.md` (NFR-OBS-04)

**Passos:**
1. Criar `src/NexusRouter.Router/NexusRouterTracing.cs`:
   - `ActivitySource` estático chamado `"NexusRouter.HotPath"`.
   - Encapsular `RouterEngine.Route` em `activitySource.StartActivity("Route")` somente se o sampling estiver ativo (verificar `Activity.Current` / sampler — não criar activities se o sampling estiver desligado, para evitar regressão na garantia de zero-overhead).
2. Configurar a taxa de sampling do `ActivitySource` em `appsettings.json` (padrão: 0% — desligado por padrão conforme NFR-OBS-04).
3. Adicionar exportação do SDK OpenTelemetry para o exporter OTLP se configurado.

**Saída:** Spans de tracing visíveis no Jaeger/Zipkin quando o sampling está habilitado; zero overhead quando desabilitado.

---

- [ ] T-36 — Implementar Benchmarks BenchmarkDotNet do Hot Path

**Objetivo:** Fornecer benchmarks de latência reproduzíveis para os componentes do hot path.

**Entradas:** `requirements.md` (NFR-PERF-01 até NFR-PERF-03), T-13, T-14, T-17

**Passos:**
1. Adicionar pacote NuGet `BenchmarkDotNet` (fixado) ao `NexusRouter.Benchmarks.csproj`.
2. Criar `tests/NexusRouter.Benchmarks/PassThroughRewriterBenchmark.cs`:
   - Método `[Benchmark]`: aloca um array de bytes de mensagem FIX uma vez em `[GlobalSetup]`, depois chama `PassThroughRewriter.Rewrite(span, seqNum)` por iteração. Mede ns/op, bytes alocados (alvo: 0 B).
3. Criar `tests/NexusRouter.Benchmarks/FixTokenizerBenchmark.cs`:
   - Setup similar para `FixTokenizer.Tokenize(...)` com uma mensagem FIX de 20 campos.
4. Criar `tests/NexusRouter.Benchmarks/RouterEngineBenchmark.cs`:
   - End-to-end: `RouterEngine` pré-construído com um stub `IMessageDispatcher` em memória (channel que descarta payloads). Mede o ciclo completo de `Route()`.
5. `[MemoryDiagnoser]` em todas as classes de benchmark.

**Saída:** `dotnet run -c Release` em `NexusRouter.Benchmarks` produz um relatório BenchmarkDotNet com allocation = 0 B para benchmarks do hot path.

---

## Fase 8 — Testes

- [ ] T-37 — Testes Unitários: Camada de Domínio

**Objetivo:** Testar todos os value types de domínio, utilitários de pooling e enumerações.

**Passos:**
1. `ArrayPoolMemoryOwnerTests`: verificar slice correto de `Memory<byte>`, segurança de double-dispose, retorno ao pool.
2. `RefCountedMemoryOwnerTests`: verificar contagem de referências (último `Release()` dispara dispose; os anteriores não).
3. `DispatchPayloadPoolTests`: verificar que `Reset()` limpa todos os campos; objetos poolizados são retornados.
4. `RouteContextTests`: verificar iteração de `ImmutableArray`.

---

- [ ] T-38 — Testes Unitários: `PassThroughRewriter` e `FixTokenizer`

**Objetivo:** Verificar a corretude dos dois algoritmos centrais de processamento do hot path.

**Passos:**
1. `PassThroughRewriterTests`:
   - Testar reescrita de uma mensagem FIX 4.4 conhecida: verificar que a `tag 34` é reescrita para o valor esperado.
   - Verificar que o checksum está correto após a reescrita.
   - Casos de borda: sequence number com mesma contagem de dígitos, menor, maior.
2. `FixTokenizerTests`:
   - Tokenizar uma mensagem FIX de 10 campos: verificar todos os pares tag-valor extraídos corretamente.
   - Verificar que `TryGetField` encontra tags existentes e retorna `false` para tags ausentes.
   - Verificar ausência de alocação heap usando `GC.GetTotalMemory` antes/depois.

---

- [ ] T-39 — Testes Unitários: `FixSessionStateMachine`

**Objetivo:** Verificar transições de estado, rejeição de transições ilegais e detecção de staleness de heartbeat.

**Passos:**
1. Todas as transições de estado válidas são bem-sucedidas.
2. Transições inválidas (ex.: `Active → Connecting` sem passar por `Disconnected`) lançam `InvalidSessionStateException`.
3. `IsHeartbeatStale` retorna `true` apenas após o tempo decorrido correto.
4. Tentativas concorrentes de transição de estado de múltiplas threads: apenas uma thread tem sucesso.

---

- [ ] T-40 — Testes Unitários: `ExponentialBackoffReconnectPolicy`

**Objetivo:** Verificar a corretude do cálculo de delay.

**Passos:**
1. Verificar crescimento exponencial: tentativa 0 = baseDelay, tentativa 1 ≈ 2×baseDelay, etc.
2. Verificar limitação em `maxDelay`.
3. Verificar que o jitter está dentro do intervalo especificado.
4. Verificar que nunca retorna delay negativo.

---

- [ ] T-41 — Testes Unitários: `RouterEngine` com Dispatchers Stub

**Objetivo:** Teste de roteamento end-to-end com stubs de dispatcher em memória.

**Passos:**
1. Construir um `RouterEngine` com um `RouteContext` roteando para 2 instâncias stub de `IMessageDispatcher` (channels em memória).
2. Chamar `Route(ingressFrame)` no modo `PassThrough`: verificar que ambos os dispatchers stub receberam um payload, verificar que o buffer é ref-counted corretamente.
3. Chamar `Route(ingressFrame)` no modo `Parse`: verificar que `ParsedMessage` está preenchido no payload.
4. Testar fan-out quando o channel de um dispatcher está cheio (`TryWrite` retorna false): verificar que o outro dispatcher ainda recebe o payload e que um registro de auditoria é escrito.

---

- [ ] T-42 — Testes de Integração: Reconexão e Heartbeat do `FixSession`

**Objetivo:** Testar a lógica de reconexão da sessão e o comportamento de heartbeat sem uma bolsa real.

**Passos:**
1. Usar `TcpListener` no teste para simular um endpoint de bolsa.
2. Iniciar um `FixSession` apontando para o listener do teste.
3. Verificar que Logon é enviado na conexão; responder com Logon ACK.
4. Fechar o listener; verificar que a sessão transiciona para `Disconnected` e depois para `Connecting` (tentativa de reconexão).
5. Verificar que mensagens de heartbeat são enviadas no intervalo configurado.
6. Simular staleness de heartbeat: parar de responder; verificar que TestRequest é enviado, depois desconectar.

---

- [ ] T-43 — Testes de Integração: Fan-Out End-to-End com Stubs de Kafka e RabbitMQ

**Objetivo:** Verificar o pipeline completo desde a sessão inbound simulada até a entrega pelo dispatcher.

**Passos:**
1. Usar `Testcontainers` (ou o stack `docker-compose.yml`) para Kafka, RabbitMQ e TimescaleDB.
2. Configurar um `FixSession` apontando para o listener FIX do teste, roteando para `KafkaMessageDispatcher` e `RabbitMQMessageDispatcher`, com `Audit:Sinks` incluindo Postgres e `PersistMode=Full`.
3. Enviar 100 mensagens FIX pelo pipeline.
4. Verificar que todas as 100 mensagens foram consumidas do tópico Kafka.
5. Verificar que todas as 100 mensagens foram consumidas da fila RabbitMQ.
6. Verificar que o log Serilog contém 100 registros de metadados com timestamps correspondentes.
7. Verificar que `ingress_fix_messages` contém 100 linhas e que `raw_fix` é não-nulo.

---

## Fase 9 — Retransmissão opcional por cliente

Depende de T-22 (acceptor FIX), T-25 (sequence number), T-27 (Timescale) e T-31 (host). Não altera o default: sem cadastro e sem pedido no dia, o comportamento continua GapFill / publicação no broker.

- [ ] T-44 — Cadastro de cliente e escolha diária de retenção

**Objetivo:** Identificar cada consumidor por UUID, independente do dispatcher, e guardar a escolha de retenção do dia UTC.

**Entradas:** `requirements.md` (FR-RPL-01, FR-RPL-02, FR-RPL-04), `design.md` (Seção 3.4.3 e Seção 6 — `Clients`)

**Passos:**
1. Modelo `ReplayClient`: `ClientId` (UUID), `DispatcherId`, `FixSenderCompId` opcional.
2. Estado em memória da escolha do dia: desligada até o primeiro Logon com `9001=Y` ou o primeiro `POST` com `enabled: true`. Recusa mudança no mesmo dia. Zera na virada UTC.
3. Cliente FIX sem cadastro, ou com `9001` ausente/`N` no primeiro Logon, não entra na retenção.

**Saída:** Dá para resolver um SenderCompID ou um UUID para a escolha do dia, e a segunda declaração do mesmo dia não altera a primeira.

---

- [ ] T-45 — Channel de replay e hypertable `client_replay_messages`

**Objetivo:** Persistir no cold path só as mensagens dos clientes com retenção ligada naquele dia.

**Entradas:** `requirements.md` (FR-RPL-05, FR-RPL-06), `design.md` (Seção 3.4.3)

**Passos:**
1. `ReplayRecord` e `Channel` bounded. `TryWrite` depois do handoff ao dispatcher. Channel cheio descarta e incrementa métrica.
2. Hypertable no `init.sql` com `(client_id, trading_day, replay_seq)` e retenção de 1 dia. Writer em lote via `Npgsql`, no mesmo processo do audit, sem SQL no hot path.
3. FIX grava o `MsgSeqNum` outbound já reservado para a sessão. Broker grava um contador próprio daquele `ClientId`, começando em 1 a partir do opt-in.
4. Nenhum cliente opted-in naquele dispatcher: não copiar o payload.

**Saída:** Mensagem de um cliente com retenção ligada sobrevive no Timescale e é lida por `(client_id, dia, faixa de replay_seq)`. Cliente sem pedido não gera linha.

---

- [ ] T-46 — ResendRequest FIX com retransmissão do que foi retido

**Objetivo:** Quando o cliente FIX pediu retenção, o ResendRequest reenvia os frames guardados daquela sessão em vez de um GapFill único sobre o intervalo inteiro.

**Entradas:** `requirements.md` (FR-RPL-02, FR-RPL-03), `design.md` (Seção 3.3 e 3.4.3)

**Passos:**
1. No primeiro Logon do dia UTC, ler a tag `9001` e fixar a escolha em T-44.
2. Ao enfileirar mensagem de aplicação para uma sessão com retenção ligada, publicar o frame já reescrito no channel de T-45 com o sequence number da sessão.
3. No ResendRequest, ler o store daquele `ClientId` entre BeginSeqNo e EndSeqNo e reenviar esses bytes. Trecho sem linha no store recebe SequenceReset GapFill só desse trecho.
4. Cliente sem retenção no dia mantém o GapFill atual, do BeginSeqNo até o próximo número da sessão.

**Saída:** Cliente opted-in que perde a conexão recupera, pelo sequence number do ResendRequest, as mensagens retidas daquele dia. Cliente opted-out não recebe replay.

---

- [ ] T-47 — API de registro e recuperação para clientes de broker

**Objetivo:** Kafka e RabbitMQ não identificam o assinante. A escolha e a recuperação passam por HTTP, sempre filtradas pelo `ClientId`.

**Entradas:** `requirements.md` (FR-RPL-04, FR-RPL-07), `design.md` (Seção 3.4.3)

**Passos:**
1. `POST /v1/clients/{clientId}/replay` com `{ "enabled": true|false }`. Primeira chamada do dia UTC fixa a escolha. Token em `NEXUS_REPLAY_API_TOKEN`. UUID fora do cadastro responde 404.
2. Com retenção ligada, o dispatcher de broker continua publicando no recurso e, além disso, alimenta T-45 com `replay_seq` daquele cliente.
3. `GET /v1/clients/{clientId}/replay?fromSeq=&toSeq=` devolve só as linhas daquele UUID, em ordem de `replay_seq`.

**Saída:** Um assinante de Kafka ou RabbitMQ liga a retenção pela API e busca o intervalo que perdeu, sem receber mensagens de outro `ClientId`.

---

- [ ] T-48 — Testes da retenção e da retransmissão

**Objetivo:** Cobrir opt-in, opt-out, isolamento por UUID e o fato de o hot path não esperar o banco.

**Entradas:** T-44, T-45, T-46, T-47

**Passos:**
1. Dois clientes FIX no mesmo acceptor: só quem mandou `9001=Y` no primeiro Logon do dia recebe os frames no ResendRequest. O outro recebe GapFill.
2. Segundo Logon do mesmo dia com a tag oposta não muda a escolha.
3. Dois UUIDs no mesmo dispatcher Kafka: o GET de um não devolve linhas do outro.
4. Channel de replay cheio não impede o `TryEnqueue` do dispatcher.

**Saída:** Testes automatizados demonstram retenção opcional, retransmissão pelo sequence number pedido e isolamento por `ClientId`.

---

## Mapa de Dependências entre Tarefas

```
T-01 (scaffold)
 └── T-02 (enums)
      └── T-03 (value types)
           └── T-04 (interfaces)
                └── T-05 (transfer objects)
                     └── T-06 (pool utilities)
                          ├── T-07 (reconnect policy)
                          │    └── T-08 (FSM)
                          │         └── T-09 (decoders)
                          │              └── T-10 (session TCP)
                          │                   └── T-11 (logon/heartbeat)
                          │                        └── T-12 (lifecycle events)
                          │                             └── T-18 (IMessageRouter)
                          │                                  │
                          ├── T-13 (PassThroughRewriter)     │
                          │    └── T-14 (FixTokenizer)       │
                          │         └── T-15 (RouteResolver) │
                          │              └── T-16 (FanOut)   │
                          │                   └── T-17 (RouterEngine)
                          │                        └──────────┘ (T-18 conecta T-17 + T-10)
                          │
                          ├── T-19 (DispatcherBase)
                          │    ├── T-20 (Kafka)
                          │    ├── T-21 (RabbitMQ)
                          │    └── T-22 (FixOutbound)
                          │         ├── T-23 (Serializer)
                          │         └── T-24 (TLS)
                          │              └── T-25 (SeqNum Store)
                          │
                          ├── T-26 (AuditChannel)
                          │    └── T-27 (AuditWriter + IAuditSink Serilog/Postgres)
                          │         └── T-28 (DispatcherAudit)
                          │
                          └── T-29 (Config POCOs)
                               └── T-30 (DI Extensions)
                                    └── T-31 (Program.cs)
                                         └── T-32 (Shutdown)
                                              ├── T-33 (Metrics)
                                              │    └── T-34 (Prometheus)
                                              │         └── T-35 (Tracing)
                                              │              └── T-36 (Benchmarks)
                                              └── T-37..T-43 (Testes — podem começar após T-17/T-22/T-27)
T-22 + T-25 + T-27 + T-31
 └── T-44 (cadastro e escolha do dia)
      └── T-45 (channel + hypertable)
           ├── T-46 (ResendRequest FIX)
           └── T-47 (API dos brokers)
                └── T-48 (testes de replay)
```
