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
| Fase 9 | Retransmissão por cliente (sobre o log de sessão) | T-44, T-47, T-48 (T-45 e T-46 removidas) |
| Fase 10 | Egress WebSocket | T-49a → T-53 |
| Fase 11 | Melhorias e débitos técnicos | TD-01 → TD-08 (inclui TD-04a, TD-04b) e TD-10 → TD-14 (TD-09 foi absorvido pelo TD-04; o ID não é reutilizado) |

**Ordem de execução.** TD-01 a TD-08 já estão concluídos, e T-44, T-47 e T-48 também. T-49a a T-49d não usam o log. Índice e replay continuam dependendo de T-44, TD-04 e TD-04a. TD-10a a TD-10d, TD-11 e TD-12a a TD-12c são independentes das Fases 9 e 10. Quem encontrar uma task com a dependência desmarcada deve avisar e parar (`AGENTS.md`).

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

- [x] T-26 — Implementar `AuditChannelSingleton`

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

- [x] T-27 — Implementar `AuditWriterService`

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

- [x] T-28 — Implementar a Integração de Auditoria de Erros dos Dispatchers

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

- [x] T-29 — Implementar as Classes de Options de Configuração

**Objetivo:** Definir POCOs de configuração fortemente tipados para todo o sistema.

**Entradas:** `design.md` (Seção 6 — Modelo de Configuração)

**Passos:**
1. Criar `src/NexusRouter.Host/Configuration/` com:
   - `NexusRouterOptions.cs`: raiz de options de nível superior.
   - `SessionOptions.cs`: todas as configurações de nível de sessão (host, porta, comp IDs, TLS, reconexão, delivery mode, tipo de frame decoder, rotas).
   - `TlsOptions.cs`: configurações específicas de TLS.
   - `ReconnectPolicyOptions.cs`: parâmetros de backoff.
   - `DispatcherOptions.cs`: classe base / union discriminada para options de Kafka, RabbitMQ, FIX outbound e WebSocket.
   - `KafkaDispatcherOptions.cs`, `RabbitMQDispatcherOptions.cs`, `FixOutboundDispatcherOptions.cs`, `WebSocketDispatcherOptions.cs` (Kafka: campos de FR-EGR-11 / design Seção 6; WebSocket: campos de FR-EGR-12 / design Seção 6).
   - `AuditOptions.cs`: channel, overflow, batch, persist mode, lista de sinks.
   - `PostgresAuditSinkOptions.cs`: host, port, database, env vars de credencial, SslMode.
2. Todos os POCOs implementam `IValidateOptions<T>` ou usam atributos `DataAnnotations` para validação na inicialização.
3. Nenhum secret em qualquer POCO — credenciais referenciam apenas nomes de variáveis de ambiente.

**Saída:** POCOs de configuração compilam. `dotnet run` com config obrigatória ausente falha rapidamente com um erro de validação claro.

---

- [x] T-30 — Implementar as Extensions de Registro de DI

**Objetivo:** Conectar todos os serviços ao `IServiceCollection` de forma limpa e organizada.

**Entradas:** `design.md` (Seções 3.1–3.4, 5.1), todas as tarefas anteriores

**Passos:**
1. Criar `src/NexusRouter.Host/Extensions/ServiceCollectionExtensions.cs`:
   - `AddNexusRouterIngress(this IServiceCollection, IConfiguration)`: registra implementações de `ISessionFrameDecoder` (com chave por nome de tipo de decoder), `ExponentialBackoffReconnectPolicy`, um `FixSession` `IHostedService` por sessão configurada.
   - `AddNexusRouterRouter(this IServiceCollection, IConfiguration)`: registra `RouterEngine` como `IMessageRouter` (singleton), `RouteResolver` (singleton), `FanOutEngine` (singleton), `DispatchPayloadPool` (singleton).
   - `AddNexusRouterEgress(this IServiceCollection, IConfiguration)`: registra cada dispatcher (Kafka, RabbitMQ, FixOutbound, WebSocket) com base em `DispatcherOptions.Type`. Cada dispatcher registrado tanto como tipo concreto quanto como `IMessageDispatcher` (registro nomeado).
   - `AddNexusRouterAudit(this IServiceCollection, IConfiguration)`: registra `AuditChannelSingleton`, sinks (`SerilogAuditSink`, `PostgresAuditSink` conforme `Audit:Sinks`), `AuditWriterService` como `IHostedService`, `DispatcherAuditObserver` como `IHostedService`.
2. Objetos `RouteContext` são construídos a partir de `SessionOptions.Routes` resolvendo instâncias de dispatcher pelo nome no container de DI.

**Saída:** Todos os serviços registrados. `dotnet run` inicia sem erros de resolução de DI (com um `appsettings.json` válido).

---

- [x] T-31 — Implementar `Program.cs` e o Entry Point da Aplicação

**Objetivo:** Inicializar o Generic Host com todos os serviços registrados e configurar Serilog.

**Entradas:** T-29, T-30

**Passos:**
1. Criar `src/NexusRouter.Host/Program.cs`:
   - Usar o padrão `Host.CreateDefaultBuilder(args)` / `WebApplication.CreateBuilder`.
   - Configurar Serilog a partir de `appsettings.json` (`UseSerilog`).
   - Chamar todos os métodos de extensão `AddNexusRouter*`.
   - Adicionar `UseConsoleLifetime()` para shutdown limpo via Ctrl+C.
2. Criar `src/NexusRouter.Host/appsettings.json` com uma configuração de exemplo completa alinhada ao `docker-compose.yml` (`localhost:19092` Kafka, `localhost:5672` RabbitMQ, `localhost:5432` Postgres, acceptor WebSocket em `localhost:6002`, sem credenciais reais — só nomes de env vars).
3. Adicionar pacotes NuGet do Serilog: `Serilog.AspNetCore` ou `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` (versões fixadas).

**Saída:** `dotnet run` inicia a aplicação, registra mensagens de startup e as sessões começam a tentar conectar.

---

- [x] T-32 — Implementar a Sequência de Shutdown Gracioso

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

- [x] T-33 — Implementar Instrumentação com `System.Diagnostics.Metrics`

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

- [x] T-34 — Implementar Endpoint de Métricas OpenTelemetry / Prometheus

**Objetivo:** Expor métricas no formato de scrape Prometheus.

**Entradas:** `requirements.md` (NFR-OBS-03)

**Passos:**
1. Adicionar pacotes NuGet: `OpenTelemetry.Exporter.Prometheus.AspNetCore` (ou `prometheus-net.AspNetCore`), versão fixada.
2. Em `Program.cs`, adicionar host HTTP ASP.NET Core mínimo junto ao Generic Host (ou usar `IHostBuilder` com `ConfigureWebHostDefaults`).
3. Expor endpoint `/metrics` usando middleware de exportação Prometheus.
4. Configurar intervalo de scrape e porta em `appsettings.json`.

**Saída:** `curl http://localhost:9090/metrics` retorna contadores e histogramas no formato Prometheus.

---

- [x] T-35 — Adicionar Instrumentação de Tracing com `ActivitySource`

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

- [x] T-36 — Implementar Benchmarks BenchmarkDotNet do Hot Path

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

- [x] T-37 — Testes Unitários: Camada de Domínio

**Objetivo:** Testar todos os value types de domínio, utilitários de pooling e enumerações.

**Passos:**
1. `ArrayPoolMemoryOwnerTests`: verificar slice correto de `Memory<byte>`, segurança de double-dispose, retorno ao pool.
2. `RefCountedMemoryOwnerTests`: verificar contagem de referências (último `Release()` dispara dispose; os anteriores não).
3. `DispatchPayloadPoolTests`: verificar que `Reset()` limpa todos os campos; objetos poolizados são retornados.
4. `RouteContextTests`: verificar iteração de `ImmutableArray`.

---

- [x] T-38 — Testes Unitários: `PassThroughRewriter` e `FixTokenizer`

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

- [x] T-39 — Testes Unitários: `FixSessionStateMachine`

**Objetivo:** Verificar transições de estado, rejeição de transições ilegais e detecção de staleness de heartbeat.

**Passos:**
1. Todas as transições de estado válidas são bem-sucedidas.
2. Transições inválidas (ex.: `Active → Connecting` sem passar por `Disconnected`) lançam `InvalidSessionStateException`.
3. `IsHeartbeatStale` retorna `true` apenas após o tempo decorrido correto.
4. Tentativas concorrentes de transição de estado de múltiplas threads: apenas uma thread tem sucesso.

---

- [x] T-40 — Testes Unitários: `ExponentialBackoffReconnectPolicy`

**Objetivo:** Verificar a corretude do cálculo de delay.

**Passos:**
1. Verificar crescimento exponencial: tentativa 0 = baseDelay, tentativa 1 ≈ 2×baseDelay, etc.
2. Verificar limitação em `maxDelay`.
3. Verificar que o jitter está dentro do intervalo especificado.
4. Verificar que nunca retorna delay negativo.

---

- [x] T-41 — Testes Unitários: `RouterEngine` com Dispatchers Stub

**Objetivo:** Teste de roteamento end-to-end com stubs de dispatcher em memória.

**Passos:**
1. Construir um `RouterEngine` com um `RouteContext` roteando para 2 instâncias stub de `IMessageDispatcher` (channels em memória).
2. Chamar `Route(ingressFrame)` no modo `PassThrough`: verificar que ambos os dispatchers stub receberam um payload, verificar que o buffer é ref-counted corretamente.
3. Chamar `Route(ingressFrame)` no modo `Parse`: verificar que `ParsedMessage` está preenchido no payload.
4. Testar fan-out quando o channel de um dispatcher está cheio (`TryWrite` retorna false): verificar que o outro dispatcher ainda recebe o payload e que um registro de auditoria é escrito.

---

- [x] T-42 — Testes de Integração: Reconexão e Heartbeat do `FixSession`

**Objetivo:** Testar a lógica de reconexão da sessão e o comportamento de heartbeat sem uma bolsa real.

**Passos:**
1. Usar `TcpListener` no teste para simular um endpoint de bolsa.
2. Iniciar um `FixSession` apontando para o listener do teste.
3. Verificar que Logon é enviado na conexão; responder com Logon ACK.
4. Fechar o listener; verificar que a sessão transiciona para `Disconnected` e depois para `Connecting` (tentativa de reconexão).
5. Verificar que mensagens de heartbeat são enviadas no intervalo configurado.
6. Simular staleness de heartbeat: parar de responder; verificar que TestRequest é enviado, depois desconectar.

---

- [x] T-43 — Testes de Integração: Fan-Out End-to-End com Stubs de Kafka e RabbitMQ

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

## Fase 9 — Retransmissão por cliente (sobre o log de sessão)

> **Revisão de 2026-11-06 (`notes/session-log-rethink.md`):** a retenção deixou de ser opt-in diário gravado em hypertable. O log de sessão de TD-04/TD-04a guarda **todos** os frames, então toda sessão tem o que reenviar. A hypertable `client_replay_messages` sai; o replay lê do log em arquivo e do índice por cliente. O filtro de roteamento por tag de negócio (`notes/replay-rethink.md` §2) fica **fora do backlog**.

Depende de TD-04 (log de sessão), TD-04a (sessão grava no log e dispatcher com cursor), TD-04b (brokers), TD-08 (reenvio) e T-31 (host). Os TD correspondentes (Fase 11) já estão concluídos.

- [x] T-44 — Chave de cliente (`ClientKey`), `MaxClients` e cadastro WebSocket

**Objetivo:** Fechar o que falta da identidade por cliente: chave validada antes de virar nome de pasta, limite de clientes FIX por dispatcher e o cadastro WebSocket por UUID.

**Entradas:** `requirements.md` (FR-RPL-01), `design.md` (Seção 3.4.6; Seção 6 — `Clients`). Antes de começar, conferir no código o que o `SessionLogPaths.EnsureKey` e o TD-07 já cobrem, para não duplicar.

**Já entregue (TD-04, TD-04a, TD-07):** criação dos arquivos do cliente FIX no primeiro Logon aceito e reabertura dos clientes conhecidos na abertura do `SessionLogManager`. Não refazer.

**Passos:**
1. Tipo `ClientKey` (string): FIX = `DispatcherId` + `SenderCompID`; WebSocket = `ClientId` (UUID). Validar o `SenderCompID` (`[A-Za-z0-9_-]`, 1 a 64) e confirmar o caminho final dentro de `SessionLog.Directory` após `Path.GetFullPath`. Recusar `..`, separadores, caracteres de controle e nomes reservados do Windows (`CON`, `NUL` etc.). Cobrir com testes o que o `EnsureKey` ainda não cobrir.
2. `FixOutboundDispatcherOptions.MaxClients` (padrão 64): limite de `SenderCompID` distintos que criam arquivos por instância. Passado o limite, recusar o Logon com `Logout` e incrementar `sessionlog_clients_rejected_total`. Validação de options e `appsettings.json`.
3. Cadastro `Clients` só para WebSocket (UUID, `DispatcherId`, `TokenEnvVar`), com consulta por `ClientId` para a T-50. Entradas FIX em `Clients` seguem ignoradas.

**Saída:** Um `SenderCompID` inválido nunca vira pasta, o número de clientes FIX por dispatcher é limitado e a T-50 consegue resolver um UUID WebSocket cadastrado.

---

> **T-45 (removida).** Channel de replay e hypertable `client_replay_messages`. Substituída pelo log de sessão em arquivo (TD-04) e pelo índice por cliente (TD-04a). A hypertable não será criada; o Timescale fica só com a auditoria (`ingress_fix_messages`).
>
> **T-46 (removida).** ResendRequest FIX lendo do log. Consolidada em TD-08.

---

- [x] T-47 — Recuperação para clientes de broker pelo cursor

**Objetivo:** Kafka e RabbitMQ não identificam o assinante na entrega. A recuperação se dá pelo cursor persistido do dispatcher, não por API paralela.

**Entradas:** `requirements.md` (FR-RPL-04, FR-RPL-07), `design.md` (Seção 3.4.4 — Cursor de broker; Seção 3.4.5), `notes/session-log-rethink.md` §4, §8.2.

**Decisão (2026-11-08):** a API HTTP de replay (`GET`/`POST /v1/clients/{id}/replay`) e a variável `NEXUS_REPLAY_API_TOKEN` não serão implementadas. O cursor persistido cobre a recuperação do dispatcher de broker, e o assinante deduplica pelo header. Se surgir um caso de uso de leitura sob demanda por assinante, entra como task nova, lendo do log.

**Passos:**
1. O `BrokerCursorTracker`, o `StreamCatchUp` e o `CursorAckMode` já existem (TD-04b). Confirmar que, no restart, o dispatcher retoma do cursor persistido por `(stream, época)` e reentrega do log. Só corrigir se a verificação falhar.
2. Confirmar que nenhuma rota, option ou configuração da API de replay existe no código e na documentação (no código não há). Remover referências restantes na documentação.

**Saída:** Um assinante de broker se recupera pelo cursor após restart do router, sem API paralela.

---

- [x] T-48 — Testes da retransmissão sobre o log

**Objetivo:** Cobrir reenvio por seqnum, isolamento por UUID, ao menos uma vez no broker e o fato de o hot path não ser bloqueado por consumidor lento.

**Entradas:** T-44, T-47, TD-04, TD-04a, TD-04b, TD-08

**Antes de escrever:** TD-04 a TD-08 trouxeram testes próprios (por exemplo `ResendPlannerTests`). Para cada cenário abaixo, conferir se já há teste equivalente e cobrir só o que falta.

**Passos:**
1. Dois clientes FIX no mesmo acceptor: o `ResendRequest` de cada um devolve só as mensagens do índice dele.
2. Cliente FIX registrado offline durante parte do dia: o seq dele segue contíguo, o Logon seguinte revela o buraco e o `ResendRequest` recupera as mensagens.
3. Queda simulada sem marcador `clean`: o seq de saída salta e nunca regride; o intervalo pulado volta como gap-fill.
4. Slots publicados fora de ordem (consumo e sessão) não geram buraco falso.
4a. Quatro modos do acceptor (Seção 3.4.7): `141=Y` abre nova época e a anterior deixa de ser reenviável; retomada com `789` reenvia `N..M-1`; `SkipToCurrent` envia gap-fill e não reenvia aplicação; `789` ou `BeginSeqNo` acima do que o router numerou recebe `Logout` e o router não adota o seq do cliente; `Logout` espera a confirmação até o timeout.
4b. `34` do cliente: maior aceita com aviso, menor sem `PossDupFlag` leva a `Logout`, menor com `PossDupFlag=Y` é ignorado, `ResendRequest` sempre processado.
5. Dispatcher offline durante parte do dia alcança pelo cursor ao voltar, sem buraco.
6. Restart entre envio e confirmação no broker reentrega a última mensagem com o mesmo header de idempotência.
7. Confirmações de broker fora de ordem não adiantam o cursor além da marca-d'água contígua.
8. Channel cheio vira lag recuperável, não descarte.

**Saída:** Testes demonstram reenvio pelo seqnum pedido, isolamento por `ClientId`, entrega ao menos uma vez e ausência de descarte por consumidor lento.

---

## Fase 10 — Egress WebSocket

Acceptor para aplicação web ou nativa. Tasks de T-49a a T-53. T-49a a T-49d não usam o log e partem de T-19 (`DispatcherBase`). Índice e replay continuam dependendo de T-44, TD-04 e TD-04a. A projeção não entra no router: o hot path continua entregando o `DispatchPayload` inteiro.

- [ ] T-49a — `WebSocketDispatcherOptions` no Egress e validação da Seção 6

**Tamanho:** P. **Depende de:** T-19.

**Objetivo:** Declarar as options do dispatcher WebSocket no Egress e validá-las pela Seção 6. O Host já tem a classe espelhada.

**Entradas:** `requirements.md` (FR-EGR-12), `design.md` (Seção 6 — `Type: WebSocket`), `src/NexusRouter.Host/Configuration/WebSocketDispatcherOptions.cs`

**Passos:**
1. Criar `src/NexusRouter.Egress/WebSocket/WebSocketDispatcherOptions.cs` com os campos da Seção 6: escuta, `AllowedOrigins`, heartbeat, timeouts, limites de frame e de campos, capacidade da fila por sessão, TLS de servidor opcional. Sem diretório de sequence store: o `seq` vem do `SessionLog` (Seção 6 do design).
2. Validar as options na subida, conforme a Seção 6. A classe do Host permanece a espelhada; esta fatia é a do Egress.
3. `WebSocketMessageDispatcher` estende `DispatcherBase<WebSocketDispatcherOptions>`. A escuta fica para T-49b.

**Saída:** Options do Egress alinhadas à Seção 6 e recusadas quando a configuração é inválida.

---

- [ ] T-49b — Upgrade WebSocket: TLS, HTTP, subprotocolo e `Origin`

**Tamanho:** P. **Depende de:** T-49a.

**Objetivo:** Aceitar o TCP, completar o upgrade e responder HTTP quando o handshake falha, sem criar sessão.

**Entradas:** `design.md` (Seção 3.3.1 — Upgrade), T-49a

**Passos:**
1. `ConnectAsync` abre `TcpListener`. Com TLS ligado, o socket aceito passa pelo wrap de servidor já usado no acceptor FIX, antes do HTTP.
2. Exigir `Upgrade: websocket` e subprotocolo `nexusrouter.v1`. `Origin` presente só passa se estiver em `AllowedOrigins`; lista vazia recusa qualquer `Origin`.
3. Falha de upgrade responde HTTP e fecha o TCP, sem criar sessão. O socket aceito vira `WebSocket` por `WebSocket.CreateFromStream`.
4. Testes: `Origin` fora de `AllowedOrigins` não completa o upgrade. Upgrade sem o subprotocolo `nexusrouter.v1` também não.

**Saída:** Um cliente com `Origin` aceita e subprotocolo `nexusrouter.v1` completa o upgrade. Origem recusada ou subprotocolo ausente recebe resposta HTTP e não vira sessão.

---

- [ ] T-49c — `WebSocketAcceptedSession` com channel bounded

**Tamanho:** P. **Depende de:** T-49b.

**Objetivo:** Cada conexão aceita vira uma sessão com fila própria. Channel cheio descarta só aquela sessão.

**Entradas:** `design.md` (Seção 3.3.1 — Upgrade), T-49b

**Passos:**
1. Cada conexão é uma `WebSocketAcceptedSession` com channel bounded próprio.
2. Channel cheio descarta só aquela sessão e incrementa métrica. O loop de accept e as outras sessões seguem.
3. Teste: fila de uma sessão cheia não impede a outra sessão de receber o frame.

**Saída:** Um cliente lento não impede o accept nem as outras sessões. A métrica conta o descarte daquela sessão.

---

- [ ] T-49d — Ping, eco de heartbeat e `StopAsync`

**Tamanho:** P. **Depende de:** T-49c.

**Objetivo:** Manter a sessão viva enquanto o cliente responde, derrubá-la depois do limite de pongs e fechá-la no stop.

**Entradas:** `design.md` (Seção 3.3.1 — Liveness), T-49c

**Passos:**
1. Ping de protocolo a cada `HeartbeatIntervalSeconds`. Após `MissedHeartbeatLimit` pongs ausentes, encerrar a sessão.
2. Frame `{ "type": "heartbeat" }` recebe eco.
3. `StopAsync` completa o channel do dispatcher, fecha as sessões e drena.

**Saída:** Um cliente permanece na sessão enquanto responde ao ping, cai depois do limite de pongs ausentes, e o `StopAsync` fecha e drena as sessões.

---

- [ ] T-50a — Primeiro frame `logon`: `clientId` e token

**Tamanho:** P. **Depende de:** T-49c, T-44.

**Objetivo:** O primeiro frame autentica o cliente cadastrado neste dispatcher. Falha fecha a sessão sem logar o token.

**Entradas:** `requirements.md` (FR-EGR-12, NFR-SEC-04), `design.md` (Seção 3.3.1 — Logon), T-44 (cadastro `Clients`)

**Passos:**
1. O primeiro frame de dados, dentro de `LogonTimeoutSeconds`, tem de ser texto JSON `logon`, no máximo `MaxControlFrameBytes`. Outro frame, ou silêncio até o timeout, fecha a sessão.
2. `clientId` tem de estar cadastrado para este `DispatcherId`. O `token` é comparado em tempo constante com o valor da variável nomeada em `TokenEnvVar`.
3. Token divergente ou cliente desconhecido devolve `{ "type": "reject", "reason": "unauthorized" }` e fecha. O valor do token não entra em log nem na URL.

**Saída:** Conexão sem cadastro ou com token errado recebe `reject` / `unauthorized` e fecha, sem mensagem de ordem e sem o token em log.

---

- [ ] T-50b — `encoding`, filtro de tags e `logonAck`

**Tamanho:** P. **Depende de:** T-50a.

**Objetivo:** O Logon fixa o que a conexão recebe. Até o ack, a sessão não recebe `exec`.

**Entradas:** `requirements.md` (FR-EGR-12), `design.md` (Seção 3.3.1 — Logon), T-50a

**Passos:**
1. `encoding` é `json` ou `binary`. `fields` omitido ou vazio significa todas as propriedades de aplicação. Lista presente é de tags FIX, no máximo `MaxFields`, sem duplicata, na ordem pedida.
2. As tags de sessão 8, 9, 10, 34, 35, 49, 52 e 56 saem da lista. Tag desconhecida no filtro é ignorada.
3. `replay` ausente vale `false`. Não há escolha diária: o log grava tudo. O reenvio ao reconectar fica para T-52c.
4. Sucesso: texto `logonAck` com `replay`, `encoding`, `fields` (ausente quando a projeção é completa) e `nextSeq`. Até o ack, a sessão não recebe `exec`.
5. Teste: o `logonAck` ecoa `replay` conforme o Logon dessa conexão. Não há escolha congelada por dia.

**Saída:** Duas conexões no mesmo dispatcher podem pedir projeções diferentes. O ack descreve a projeção desta conexão, e nenhum `exec` sai antes dele.

---

- [ ] T-50c — Novo Logon do mesmo `ClientId` continua o `seq`

**Tamanho:** P. **Depende de:** T-50b, TD-04, TD-04a.

**Objetivo:** Uma segunda conexão do mesmo cliente substitui a anterior e segue o `seq` do índice.

**Entradas:** `design.md` (Seção 3.3.1 — Logon), `IClientIndex`, T-50b

**Passos:**
1. Um novo Logon do mesmo `ClientId` fecha o socket anterior.
2. O `seq` continua do `IClientIndex` daquele cliente (`NextOut`).

**Saída:** A conexão nova herda o `seq` e a anterior está fechada.

---

- [ ] T-51a — Reservar `seq` sem sessão logada

**Tamanho:** P. **Depende de:** T-50c.

**Objetivo:** Sem sessão logada, o consume loop reserva o `seq` e publica no índice, sem tokenizar. O `seq` permanece contíguo.

**Entradas:** `requirements.md` (FR-EGR-12, FR-HLP-01), `design.md` (Seção 3.3.1 — Projeção), T-50c

**Passos:**
1. No consume loop, sem sessão logada, reservar o `seq` com `IClientIndex.ReserveSeq` e publicar a entrada com `PublishApp(seq, LogRef)`, sem tokenizar nem projetar. O payload fica no log.
2. O estado do `seq` vem do `IConsumerState` e do `IClientIndex`, no mesmo padrão do acceptor FIX. Não há `SequenceNumberStore` próprio.
3. Teste: sem sessão logada, o cliente conhecido continua recebendo `seq` contíguo no índice, de forma que recupera as mensagens ao reconectar.

**Saída:** O índice do cliente cadastrado avança contíguo mesmo sem sessão logada.

---

- [ ] T-51b — Encoder JSON da projeção

**Tamanho:** P. **Depende de:** T-51a.

**Objetivo:** Selecionar os campos e montar o frame JSON. Teste puro, sem socket.

**Entradas:** `design.md` (Seção 3.3.1 — Projeção), T-51a

**Passos:**
1. Criar `WebSocketFrameEncoder` para o texto JSON. Envelope com `type`, `seq`, `msgType` e `ts`. Tags como chaves string. Valores permanecem a string ASCII do FIX, sem conversão numérica.
2. Sem filtro, a ordem é a do wire. Com filtro, a ordem é a da lista.
3. Teste puro, sem socket: seletor de campos e o JSON resultante. Um payload com `fields: [55, 54, 38]` produz só essas tags, na ordem da lista.

**Saída:** O encoder JSON devolve o envelope e as tags pedidas, na ordem do wire ou da lista, sem abrir socket.

---

- [ ] T-51c — Encoder binário little-endian

**Tamanho:** P. **Depende de:** T-51b.

**Objetivo:** Montar o frame binário little-endian da Seção 3.3.1. Teste puro, sem socket.

**Entradas:** `design.md` (Seção 3.3.1 — Projeção), T-51b

**Passos:**
1. Binário sai em frame binário little-endian, no layout da Seção 3.3.1: `u8 type`, `u64 seq`, `u64 ts`, `msgType`, `u16 fieldCount`, depois `u16 tag` + `u16 valueLen` + bytes.
2. Teste puro, sem socket, do layout.

**Saída:** O frame binário bate o layout da Seção 3.3.1.

---

- [ ] T-51d — Projeção no consume loop, fora do router

**Tamanho:** P. **Depende de:** T-51c, T-49c.

**Objetivo:** Com sessão logada, tokenizar uma vez no consume loop, projetar por sessão e descartar só o frame acima do teto.

**Entradas:** `requirements.md` (FR-EGR-12, FR-HLP-01), `design.md` (Seção 3.3.1 — Projeção), T-51c, T-49c

**Passos:**
1. Com ao menos uma sessão logada, tokenizar o body uma vez. Usar o `FixMessage` quando o ingresso veio em `Parse`. Em `PassThrough`, tokenizar o frame cru neste loop. Essa tokenização não roda na thread do router.
2. Para cada sessão, gravar o envelope (`type=exec`, `seq`, `msgType`, `ts`) mais os campos selecionados, com o encoder da conexão. Reservar o `seq` com `IClientIndex.ReserveSeq`, publicar a entrada com `PublishApp(seq, LogRef)` e fazer `TryWrite` dos bytes projetados na fila da sessão. A task da sessão é quem escreve no socket.
3. Frame acima de `MaxOutboundFrameBytes` é descartado só naquela sessão.

**Saída:** Cada sessão logada recebe só o encoding e os campos do Logon dela. O descarte por tamanho não atinge as outras sessões. Nenhuma projeção altera o fan-out dos outros dispatchers.

---

- [ ] T-52a — Pedido `replay` na sessão

**Tamanho:** P. **Depende de:** T-50b, T-49d.

**Objetivo:** Aceitar o frame `replay` e encerrar a sessão diante de qualquer outro tipo.

**Entradas:** `requirements.md` (FR-RPL-08, FR-RPL-05), `design.md` (Seção 3.3.1 — Replay), T-50b, T-49d

**Passos:**
1. Não há escolha diária nem canal de replay. O log grava tudo. O `logonAck` (T-50b) mostra se o reenvio automático está ligado para a conexão.
2. Aceitar `{ "type": "replay", "fromSeq", "toSeq" }`. `toSeq` 0 significa até o último `seq` enviado (`NextOut - 1`). `fromSeq` e `toSeq` do pedido são inclusivos.
3. Frame de entrada que não seja `logon`, `heartbeat` ou `replay` encerra a sessão.

**Saída:** Um frame `replay` bem formado é aceito, com o intervalo inclusivo. Qualquer outro tipo encerra a sessão.

---

- [ ] T-52b — Reenvio pelo índice e `gap`

**Tamanho:** P. **Depende de:** T-52a, T-51d.

**Objetivo:** Ler o índice daquele `ClientId`, reler o frame pelo `LogRef` e reenviar na ordem do `seq`, na projeção da conexão. Onde não houver registro, devolver `gap` contíguo.

**Entradas:** `requirements.md` (FR-RPL-08, FR-RPL-05), `design.md` (Seção 3.3.1 — Replay; Seção 3.4.3 — Cliente WebSocket; Seção 3.4.4), T-52a, T-51d

**Passos:**
1. Ler o `IClientIndex` daquele `ClientId` no intervalo e, para cada entrada de aplicação, ler o frame de entrada pelo `LogRef` (`ISessionLogReader`), projetar com o `encoding` e o `fields` da conexão atual e reenviar na ordem do `seq`. Rodar no consume loop da sessão, nunca no hot path.
2. Entrada administrativa, slot sem registro (fora da época atual ou antes do início do log retido) ou trecho com falha de gravação vira `{ "type": "gap", "fromSeq", "toSeq" }`, com `fromSeq` inclusivo e `toSeq` exclusivo, agrupando o trecho contíguo. Slot `NotYet` espera até `SlotPublishTimeoutMs`.
3. Entrada de outro `ClientId` não sai nesta sessão.
4. Teste: o pedido `replay` devolve os frames do intervalo, projetados com o `encoding` e o `fields` da conexão. Entrada administrativa e slot sem registro voltam como `gap`.

**Saída:** O cliente recupera qualquer intervalo ainda retido pelo `seq`, com a projeção da conexão atual. Trecho sem registro volta como `gap` contíguo.

---

- [ ] T-52c — `replay: true` ao reconectar

**Tamanho:** P. **Depende de:** T-52b, T-50c.

**Objetivo:** Com `replay: true`, reemitir ao reconectar, do último `seq` entregue ao fim do log. Outro encoding ou outra lista de fields muda também o replay.

**Entradas:** `design.md` (Seção 3.3.1 — Replay), T-52b, T-50c

**Passos:**
1. Com `replay: true` no Logon, ao reconectar, reemitir automaticamente do último `seq` entregue ao fim do log.
2. Reconnect com outro `encoding` ou outro `fields` muda a projeção também do replay, porque o log guarda o frame de entrada.
3. Teste: reconnect com outra lista de campos — o `exec` novo e o replay de `seq` antigo seguem a lista nova.

**Saída:** A reconexão com `replay: true` cobre o trecho ainda não entregue, na projeção desta conexão.

---

- [ ] T-53 — Duas projeções da mesma ordem e token errado

**Tamanho:** P. **Depende de:** T-51d, T-52c.

**Objetivo:** Um teste de integração: duas projeções da mesma ordem e um terceiro cliente recusado.

**Entradas:** T-51d, T-52c

**Passos:**
1. Dois clientes no mesmo dispatcher: um `json` com `fields: [11, 55]`, outro `binary` sem filtro. Cada um recebe só a sua projeção da mesma ordem. Um terceiro com token errado recebe `reject` e nenhum `exec`.

**Saída:** O teste demonstra a projeção por Logon na mesma ordem e que o token errado não recebe `exec`.

---

## Fase 11 — Melhorias e débitos técnicos

Backlog de correções e lacunas achadas depois das fases 1–10. A fase fica no fim para que nenhuma task existente mude de número.

**Formato desta fase (difere das anteriores):**

- ID `TD-NN`, sequencial dentro da fase. Não reutiliza `T-NN`. `./scripts/task.sh nexusrouter TD-02` extrai o bloco.
- Ordem de execução é a ordem do arquivo. A linha **Depende de** lista só o que precisa estar `[x]` antes.
- Cada task tem **Origem** (nota ou revisão que a gerou), **Tamanho** (P, M ou G), **Objetivo**, **Entradas**, **Passos** e **Saída**.
- Task já entregue ganha **Entregue em** com o commit. Task aberta não tem esse campo.
- Mensagem de commit e nome de branch usam o ID: `[TD-02] ...` e `feature/TD-02-<descricao>`.
- Origem comum: as notas `notes/seqnum-rethink.md`, `notes/session-log-rethink.md` e `notes/replay-rethink.md`, consolidadas em `notes/backlog-sessao-fix.md` e removidas. Os itens §N citados nas tasks apontam para o texto original, que está no histórico do git.

- [x] TD-01 — Responder `ResendRequest` do initiator com `SequenceReset-GapFill` válido

**Origem:** `notes/seqnum-rethink.md` §7.1 item 2. **Tamanho:** P. **Depende de:** T-11.
**Entregue em:** commit `7e68d33`.

**Objetivo:** O `FixSession` deixa de enviar o frame inválido `35=4<SOH>` ao counterpart que pede reenvio.

**Entradas:** `notes/seqnum-rethink.md` §7.1, `b3-guidelines.md` §12 e §13.

**Passos:**
1. `FixSession.HandleResendRequest` lê `BeginSeqNo` (7) e `EndSeqNo` (16) e responde com `BuildSequenceReset`: header completo, `34`=BeginSeqNo, `43=Y`, `122`, `123=Y`, `36` (próximo seqnum de saída, limitado por `EndSeqNo+1`), `9` e `10` válidos.
2. Pedido sem BeginSeqNo válido, ou além do que já foi enviado, é ignorado com aviso.
3. Remover o stub de `FixSessionStateMachine.HandleResendRequest`.
4. `BuildSequenceReset` passa a incluir `43=Y` e `122` no gap-fill, o que vale também para o acceptor.

**Saída:** Gap-fill bem formado para o initiator, que só envia mensagens administrativas. Não resolve reenvio de mensagens de aplicação (TD-08), validação do seqnum de entrada (TD-04) nem `Reject` (TD-06). Testes: `FixMessageBuilderTests` e `FixSession_WhenPeerSendsResendRequest_RepliesWithWellFormedGapFill`.

---

- [x] TD-02 — `PassThroughRewriter` com mudança de tamanho

**Origem:** `notes/seqnum-rethink.md` §1. **Tamanho:** P a M. **Depende de:** T-13, T-22.
**Entregue em:** commit `refactor(router)` desta branch, logo após `7e68d33`.

**Objetivo:** O MsgSeqNum de saída deixa de depender da largura da tag 34 do frame de origem. Fim do `InvalidOperationException` quando a sessão passa de 4 para 5 dígitos, e fim do padding com espaços.

**Entradas:** `notes/seqnum-rethink.md` §1, `b3-guidelines.md` §15, `design.md` §3.

**Passos:**
1. Novo `PassThroughRewriter.TryRewriteTo(source, destination, newSeqNum, out written)`: copia o frame gravando a tag 34 com os dígitos exatos e recalcula `9` (BodyLength) e `10` (CheckSum). Zero allocation. Constante `MaxGrowthBytes` (32) para a folga do destino.
2. `Rewrite` in-place continua só para largura igual, usado pelo `RouterEngine` para recomputar o checksum. Largura diferente devolve `false` e nunca preenche com espaços.
3. `FixAcceptedSession.TryEnqueue` aluga `RawBuffer.Length + MaxGrowthBytes` no PassThrough e `Prepare` usa `TryRewriteTo`. A cópia que já existia passa a ser a da própria reescrita, sem custo extra.
4. Testes: `PassThroughRewriterTests` (largura maior, menor, BodyLength ganhando dígito, destino pequeno, frame malformado) e regressão em `FixOutboundDispatcherTests` com o contador em 37581 e frame de origem `34=1`. Benchmark `TryRewriteToWithResize`.
5. Atualizar `b3-guidelines.md` §15 e `design.md` §3 (reescrita de 9, 34 e 10 por cópia no egress FIX).

**Saída:** O dispatcher FIX entrega frames válidos (BodyLength e CheckSum corretos, tag 34 sem espaços) com qualquer seqnum de sessão.

---

- [x] TD-03 — Alinhar `b3-guidelines.md` ao que os PDFs da B3 dizem

**Origem:** `notes/seqnum-rethink.md` §2. **Tamanho:** P. **Depende de:** nenhuma.

**Objetivo:** O steering não contradiz a spec da B3 nem apresenta como regra da B3 o que é decisão do projeto.

**Entradas:** `notes/seqnum-rethink.md` §2, `b3-guidelines.md` §1 e §13, `docs/EntryPointMessageSpecs.pdf`.

**Passos:**
1. §1: remover `NextExpectedMsgSeqNum` (789) do Logon, campo retirado na v1.5.1 da spec.
2. §13: marcar a regra "Logout com MsgSeqNum too low leva a reset e `141=Y`" como decisão do projeto, não da B3, ou removê-la.
3. Conferir nos PDFs a exigência de reenvio real versus gap-fill e de campos obrigatórios do Logon (COD, credenciais), e registrar o resultado no steering. Esse resultado destrava TD-05 e TD-08.

**Saída:** Steering consistente com os PDFs, com a dúvida de certificação respondida por escrito.

---

- [x] TD-04 — Log de sessão em arquivo: segmentos, índice e estado por consumidor

**Origem:** `notes/session-log-rethink.md` §2, §3, §5. **Tamanho:** G. **Depende de:** TD-03. **Substitui parte de:** TD-09 (flush) e a persistência de seqnum do `SequenceNumberStore`.

**Objetivo:** Uma abstração de log append-only grava toda mensagem antes do fan-out, e o seqnum e o cursor de cada consumidor saem desse log, não mais de um contador solto.

**Entradas:** `design.md` (Seções 3.4.3, 3.4.4, 3.4.5, 4.8, 5.2.1, 7 e 9), `requirements.md` (FR-RPL-05, FR-RPL-06, FR-RPL-09), `notes/session-log-rethink.md` §3, §5, §8.1, `SequenceNumberStore`.

**Passos:**
1. Interfaces em `NexusRouter.Domain` (`ISessionLogStream`, `ISessionLogReader`, `IClientIndex`, `IConsumerState`, `LogRef`) e o novo projeto `NexusRouter.SessionLog` com seu projeto de teste, conforme a Seção 4.8 e a Seção 7.
2. `LogSegment` e `SessionLogStream`: segmento `.log` com registros `[len][seq][ts][crc][payload]` e `.index` denso (`seq - base` → offset), tamanho fixo pré-alocado, arquivo mapeado em memória. Escritor único por stream. Publicar `committed` com `Volatile.Write` depois de gravar registro e índice. Rotação por dia de pregão, por época e por tamanho; o próximo segmento é pré-alocado e tocado em background, com contador de trocas síncronas.
3. Layout de pastas e nomes da Seção 3.4.6 (`streams/`, `clients/`, `brokers/`, `state.json`, `cursor.json`, `clean`), com `state.json` gravado por arquivo temporário + rename. `ClientIndex` (`.cidx`, slots de 16 bytes) com `ReserveSeq`, `PublishApp`, `PublishAdmin` e `TryGet` (`Published`, `NotYet`, `Missing`). Publicação do slot por `kind` com `Volatile.Write`.
4. `ConsumerStateStore`: próximo seqnum de saída, próximo seqnum de entrada esperado, época e cursor por stream. Substitui o `SequenceNumberStore` atual, que sai do código.
5. `SessionLogManager` (`IHostedService`): abertura e recuperação (Seção 3.4.5: marcador `clean`, CRC do último registro, `max(persistido, último slot) + UncleanRestartSeqMargin`), task flusher (`Interval` padrão de 5 s, ou `PerMessage`), task de pré-alocação e retenção por dia de pregão (padrão de 5 dias). Absorve o antigo TD-09.
6. `SessionLogOptions` com validação, substituindo `ReplayOptions`, e registro no host com a ordem de shutdown (NFR-REL-04).
7. Falha de escrita devolve `AppendResult.Failed` sem lançar, marca o stream como degradado e emite métrica.
8. **Portão de latência (FR-RPL-09).** Benchmark BenchmarkDotNet do `Append` na carga de NFR-PERF-01 (50.000 msg/s por sessão), com p50, p99, p99,9 e trocas síncronas. Meta de p99 ≤ 3 µs. Se reprovar, aplicar as alternativas da Seção 9, na ordem, e só com o número medido. TD-04a só começa com o portão aprovado.
9. Testes: crash com escrita cortada, CRC inválido no fim, queda sem marcador (seq salta e não regride), dois escritores nos slots do `.cidx` fora de ordem, retenção.

**Saída:** O log grava e lê por seqnum em O(1), sobrevive a crash de processo e de SO sem regredir o seqnum de saída, e o benchmark mostra o append dentro da meta de latência.

---

- [x] TD-04a — Fan-out grava no log antes de entregar; dispatcher vira consumidor com cursor

**Origem:** `notes/session-log-rethink.md` §1, §2, §4. **Tamanho:** G. **Depende de:** TD-04.

**Objetivo:** Nenhuma mensagem é descartada por rota sem dispatcher ou por channel cheio. O dispatcher alcança pelo log quando estava atrasado.

**Entradas:** `design.md` (Seções 3.4.4, 4.8, 5.2.1, 7), `notes/session-log-rethink.md` §1, §2, §4, `FixSession`, `RouterEngine`, `FanOutEngine`, `DispatcherBase`, `FixOutboundDispatcher`.

**Passos:**
1. `FixSession` chama `ISessionLogStream.Append` logo após o `TryDecode`, antes do `SessionFSM` e do rewriter, com o frame original e a tag 34 como seq de entrada. O `LogRef` vai no `DispatchPayload` (`RouterEngine` propaga; `FanOutEngine` copia nos payloads extras).
2. `FanOutEngine`: rota sem dispatcher e `TryEnqueue` recusado deixam de descartar. Liberam o buffer, contam lag e seguem. A mensagem já está no log.
3. `StreamCatchUp` no `DispatcherBase`: tabela `expected[stream]` e as regras `igual`, `maior`, `menor` da Seção 3.4.4, o timer `CatchUpIdleMs` e o drenar de época anterior. O catch-up roda na task de consumo.
4. `FixOutboundDispatcher.SendAsync`: para cada cliente conhecido (com arquivos criados em Logon anterior), `ReserveSeq` e `PublishApp(seq, LogRef)`, mesmo sem sessão logada, e só envia se a sessão existe.
5. Métrica de lag por dispatcher (distância entre `committed` e `expected`), substituindo a de descarte por channel cheio.
6. Testes: channel cheio seguido de silêncio recupera pelo timer; payload `menor` não duplica; troca de época drena a anterior; o benchmark do TD-04 continua dentro da meta com o `LogRef` no payload.

**Saída:** Com um dispatcher offline ou lento, a mensagem fica no log e é entregue quando ele volta; nenhuma vira buraco de seqnum.

---

- [x] TD-04b — Entrega ao menos uma vez nos brokers, com header de idempotência

**Origem:** `notes/session-log-rethink.md` decisão 2, §4.1, §8.2. **Tamanho:** M. **Depende de:** TD-04a.

**Objetivo:** Kafka e RabbitMQ recebem cada mensagem ao menos uma vez e o consumidor deduplica por header.

**Entradas:** `notes/session-log-rethink.md` §4.1, §8.2, `KafkaMessageDispatcher`, `RabbitMQMessageDispatcher`, `KafkaDispatcherOptions`, `RabbitMQDispatcherOptions`.

**Passos:**
1. Carimbar cada mensagem com um header de identidade `<sessão de origem>:<época>:<seq de entrada>` (record header no Kafka, header de mensagem no RabbitMQ).
2. `BrokerCursorTracker`: marca-d'água contígua dos envios em voo por `(stream, seq)`. Confirmação fora de ordem fica pendente e o cursor só avança até o maior seq com todos os anteriores confirmados. Opção `CursorAckMode` por dispatcher de broker: `OnBrokerAck` (padrão, avança o cursor na confirmação do broker, garante ao menos uma vez) ou `OnEnqueue` (avança no envio, no máximo uma vez).
3. Validação de options: recusar `Acks=0` no Kafka com `OnBrokerAck`.
4. Testes: restart entre o envio e a confirmação reentrega a última mensagem com o mesmo header; o consumidor descarta a repetida comparando com o último seq visto por sessão.

**Saída:** Falha de restart não perde mensagem no broker, e o header permite o consumidor ignorar a duplicata.

---

- [x] TD-05 — Logon completo: credenciais, COD e `ResetSeqNumFlag`

**Origem:** `notes/seqnum-rethink.md` §7.1 item 4. **Tamanho:** M. **Depende de:** TD-03, TD-04. **Regime:** initiator da B3 segue o comportamento padrão do QuickFIX (design §3.4.7, FR-RPL-13); as exceções vêm do resultado do TD-03. TD-07 depende desta task para autenticar o Logon do acceptor.

**Objetivo:** `BuildLogon` envia os campos que a B3 exige, configuráveis por sessão.

**Entradas:** `notes/seqnum-rethink.md` §7.1, `b3-guidelines.md` §1, `SessionOptions`, `FixMessageBuilder.BuildLogon`, `docs/secrets.md`.

**Passos:**
1. `SessionOptions` ganha `Username` (553), `Password` (554, via `ISecretResolver`), `CancelOnDisconnectType` (35002), `CODTimeoutWindow` (35003) e o controle de `141`.
2. `BuildLogon` inclui os campos presentes e nunca registra a senha em log.
3. Conferir o eco de `141` e o `HeartBtInt` na resposta de Logon (§7.2 item 8).

**Saída:** Logon aceito pela B3 em homologação com COD configurado.

---

- [x] TD-06 — Validação de entrada e `Reject` (35=3) no initiator

**Origem:** `notes/seqnum-rethink.md` §7.2 itens 6 e 8. **Tamanho:** M. **Depende de:** TD-04. **Regime:** initiator segue o QuickFIX: `34` maior leva a `ResendRequest`; menor sem `PossDupFlag` leva a `Logout`. Não usa a política mista do acceptor (design §3.4.7).

**Objetivo:** Frames malformados ou fora das regras de sessão geram `Reject` em vez de entrarem no pipeline.

**Entradas:** `notes/seqnum-rethink.md` §7.2, `FixFrameScanner`, `b3-guidelines.md` §4.

**Passos:**
1. Validar `BodyLength`, `CheckSum`, `BeginString`, `SenderCompID`/`TargetCompID` e `SendingTime` (tolerância configurável).
2. Responder `Reject` com `RefSeqNum`, `RefTagID`, `SessionRejectReason` e `Text`.
3. Contadores de rejeição na métrica de sessão.

**Saída:** Frame inválido do counterpart é rejeitado e contado, sem chegar ao router.

---

- [x] TD-07 — Acceptor FIX: seqnum de entrada, `Logout` na recusa e detecção de silêncio

**Origem:** `notes/seqnum-rethink.md` §4 e §7.3. **Tamanho:** M. **Depende de:** TD-03, TD-04, TD-05 (autenticação do Logon, para que arquivos só sejam criados após Logon aceito).

**Objetivo:** O `FixAcceptedSession` trata o cliente como um counterpart de sessão FIX, não só como um socket.

**Entradas:** `notes/seqnum-rethink.md` §4 e §7.3, `notes/session-log-rethink.md` §3.4, `FixAcceptedSession`, `FixOutboundDispatcher`.

**Passos:**
1. Validar o `34` recebido do cliente com o `nextInExpected` do TD-04, pela política mista da Seção 3.4.7 (FR-RPL-12): maior aceita com aviso e métrica; menor sem `PossDupFlag` é `Logout` "MsgSeqNum too low"; menor com `PossDupFlag=Y` é ignorado; o `ResendRequest` é sempre processado.
2. Recusa de Logon responde `Logout` com `Text` antes de fechar. Resposta de Logon ecoa `141=Y` quando o cliente pede.
3. Respeitar o `HeartBtInt` do cliente e fechar a sessão após silêncio de `HeartBtInt` mais tolerância, com `TestRequest` antes.
4. `ResendRequest` respeita `EndSeqNo`.
5. Modos de conexão da Seção 3.4.7 (FR-RPL-10, FR-RPL-11): (1) `141=Y` com `AllowResetSeqNumFlag`, que abre nova época do cliente; (2) retomar de onde parou, com `789` (reenvio automático, depois `SequenceReset-GapFill` sobre o seq do Logon) ou sem `789` (atende o `ResendRequest`); (3) `SkipToCurrent`, com `SequenceReset-GapFill` (`34 = N`); (4) `789` ou `BeginSeqNo` acima do que o router numerou leva a `Logout` com `Text`, registra `WARN` e `acceptor_client_ahead_total`. O router nunca adota o seq maior do cliente.
6. Rotina de `Logout`: envia `Logout` com `Text`, espera o `Logout` de confirmação por `LogoutTimeoutSeconds` (responde só a `ResendRequest` nesse intervalo) e fecha.
7. Opções `Recovery.*` no `FixOutboundDispatcherOptions` com sobrescrita por `SenderCompID` (`ClientOverrides`) e validação.
8. Validar a ordem do `SequenceReset-GapFill` do modo 3 em relação ao seq do Logon de resposta contra o comportamento do QuickFIX (o dicionário FIX não a fixa).

**Saída:** Cliente que reconecta com seqnum incoerente recebe resposta explícita, e peer morto é derrubado pelo router.

---

- [x] TD-08 — Reenvio no `ResendRequest`: aplicação do log, gap-fill para administrativas

**Origem:** `notes/session-log-rethink.md` §4, §8.1; `notes/seqnum-rethink.md` §7.1 item 3 e §4. **Tamanho:** G. **Depende de:** TD-03, TD-04, TD-04a.

**Objetivo:** Reenvio real, à moda do QuickFIX: o `ResendRequest` reenvia as mensagens de aplicação guardadas no log e substitui as administrativas por gap-fill agrupado.

**Entradas:** resultado de TD-03, `notes/session-log-rethink.md` §4 e §8.1, `notes/seqnum-rethink.md` §4.

**Passos:**
0. Criar `ResendPlanner` (Seção 4.8): lê o `IClientIndex` entre `BeginSeqNo` e `EndSeqNo` e devolve a sequência de ações (reenviar aplicação por `LogRef`, gap-fill agrupado, esperar slot `NotYet`). O reenvio roda na task da sessão aceita e escreve na mesma fila de saída do tráfego ao vivo (Seção 3.4.4).
1. Ler o intervalo pedido no índice do consumidor (TD-04). Mensagem de aplicação sai com `PossDupFlag=Y` (43) e `OrigSendingTime` (122). Em `PassThrough`, o `PassThroughRewriter` com crescimento (TD-02) reescreve 34, insere 43 e 122, atualiza 52 e recalcula 10. Em `Parse`, o `FixMessageSerializer` reserializa a partir do frame lido do log.
2. Mensagem administrativa vira `SequenceReset-GapFill`, agrupando as vizinhas num único gap-fill.
3. Respeitar `EndSeqNo` e o limite de 10.000 mensagens por pedido (`PossMissingApplMsg=Y` acima disso).
4. Confirmar em `docs/FIX44Entrypoint.xml` quais tipos contam como administrativos.

**Saída:** Reenvio implementado e coberto pelo cenário A4 do roteiro de certificação.

---

- [x] TD-10a — Tag 440, `HeaderFields` e a decisão do Kafka

**Origem:** `notes/replay-rethink.md` §3. **Tamanho:** P. **Depende de:** TD-04b.

**Objetivo:** Confirmar a tag 440, expor `HeaderFields` e o template de routing key, e registrar a decisão do ponto em aberto. Sem config, a routing key continua fixa e só sai o header de idempotência. O router não decide quem recebe o quê.

**Entradas:** `notes/replay-rethink.md` §3, `KafkaDispatcherOptions`, `RabbitMQDispatcherOptions`, `docs/FIX44Entrypoint.xml`.

**Decisão (2026-11-11):** `HeaderFields` vazio por padrão. A conta é a tag 1 (`Account`); a 440 não está em `docs/FIX44Entrypoint.xml`. Kafka: record headers e a partição já configurada, sem tópico por critério.

**Passos:**
1. Confirmar no dicionário B3 (`docs/FIX44Entrypoint.xml`) que a tag 440 (ClearingAccount) é a certa para segregar por conta e quais outras tags valem como dimensão.
2. Options: lista de tags a carimbar (`HeaderFields`) nos dois brokers e, no RabbitMQ, um template de routing key hierárquica (por exemplo `fix.<440>.<55>`). Sem configuração, o comportamento atual é mantido (routing key fixa, só o header de idempotência).
3. Registrar no desenho a decisão do Kafka: record headers e a partição que já existe, sem tópico por critério.

**Saída:** As options existem e, sem `HeaderFields`, Kafka e RabbitMQ seguem como hoje. A decisão do tópico Kafka está registrada.

---

- [x] TD-10b — Scanner das tags configuradas no consume loop

**Origem:** `notes/replay-rethink.md` §3. **Tamanho:** P. **Depende de:** TD-10a.

**Objetivo:** Ler só as tags configuradas, no consume loop do dispatcher, e tratar valor ausente sem lançar.

**Entradas:** `KafkaMessageDispatcher`, `RabbitMQMessageDispatcher`, TD-10a.

**Passos:**
1. Ler só as tags configuradas no consume loop do dispatcher, por scanner de campos, sem parse completo. Nunca na thread do router. Reaproveitar o `FixMessage` quando o ingresso veio em `Parse`.
2. Valor ausente na mensagem usa um marcador fixo na routing key e omite o header, sem lançar.

**Saída:** O consume loop devolve o valor das tags pedidas, ou o marcador e a omissão do header quando a tag não está na mensagem.

---

- [x] TD-10c — Routing key e headers no RabbitMQ

**Origem:** `notes/replay-rethink.md` §3. **Tamanho:** P. **Depende de:** TD-10b.

**Objetivo:** O RabbitMQ publica a routing key e os headers por mensagem. A exchange e os bindings continuam com o operador.

**Entradas:** `RabbitMQMessageDispatcher`, `RabbitMQDispatcherOptions`, TD-10b.

**Passos:**
1. Montar a routing key e os headers por mensagem. A exchange e os bindings são do operador; o dispatcher só publica.
2. Testes: tag presente, tag ausente e o header de idempotência do TD-04b intacto.

**Saída:** Um consumidor RabbitMQ faz bind em `fix.ACCT1.#` e recebe só aquela conta. Mensagem sem a tag usa o marcador na routing key e omite o header. O header de idempotência permanece.

---

- [ ] TD-10d — Record headers no Kafka

**Origem:** `notes/replay-rethink.md` §3. **Tamanho:** P. **Depende de:** TD-10b.

**Objetivo:** O Kafka carimba as mesmas tags em record headers, sem mudar cursor nem entrega.

**Entradas:** `KafkaMessageDispatcher`, `KafkaDispatcherOptions`, TD-10b.

**Passos:**
1. Carimbar as tags configuradas em record headers, com a estratégia de partição já existente. Sem tópico por critério.
2. Teste: os headers levam as mesmas tags do scanner, o cursor não anda por causa do carimbo e a entrega ao menos uma vez permanece a do TD-04b.

**Saída:** Um consumidor Kafka filtra por header sem desserializar o payload. O cursor e a entrega ao menos uma vez não mudam.

---

- [ ] TD-11 — `SequenceReset` (35=4) recebido no initiator

**Origem:** `notes/seqnum-rethink.md` §7.2 item 7. **Tamanho:** P. **Depende de:** TD-06.

**Objetivo:** O initiator aplica o `NewSeqNo` de um `SequenceReset` do counterpart ao seqnum de entrada esperado, em vez de deixá-lo cair no `default` do `HandleAdmin`.

**Entradas:** `notes/seqnum-rethink.md` §7.2, `FixSession.HandleAdmin`, `IConsumerState` (`NextInExpected`), `docs/FIX44Entrypoint.xml`, `docs/EntryPointMessageSpecs.pdf`.

**Passos:**
1. Tratar `35=4` com `GapFillFlag` (123) `Y` e `N`. `NewSeqNo` (36) maior que o esperado avança `NextInExpected`. `NewSeqNo` menor leva a `Reject` com `SessionRejectReason`, conforme o TD-06. Registrar a regra equivalente do QuickFIX e o que a spec da B3 exige.
2. Persistir o novo valor pelo `IConsumerState`.
3. Gap-fill com `PossDupFlag` segue a mesma validação do `34` (regime do initiator, TD-06).
4. Testes: gap-fill avança o esperado, reset com valor menor é rejeitado, `NewSeqNo` ausente ou inválido gera `Reject`.

**Saída:** Depois de um gap-fill do counterpart, o seqnum de entrada esperado fica correto e a sessão segue sem pedir reenvio desnecessário.

---

- [ ] TD-12a — Registrar quem vira o dia de pregão

**Origem:** `notes/seqnum-rethink.md` §4 e §7.1 item 5; `notes/session-log-rethink.md` §3.2. **Tamanho:** P. **Depende de:** TD-04, TD-05.

**Objetivo:** Registrar em `design.md` §3.4.3 quem vira o dia e o fuso. Esta fatia não implementa o reset. Continua bloqueada até a decisão.

**Entradas:** `notes/seqnum-rethink.md` §2 e §4, `b3-guidelines.md`, `design.md` §3.4.3.

**Fatos documentados (PDFs da B3):** ao fim da sessão o gateway derruba a conexão e o seqnum volta a 1. No Drop Copy, o reset ocorre de terça a quinta às 23:00 BRT, com todos os clientes desconectados. De domingo para segunda o número não é resetado.

**Ponto em aberto (decidir com o usuário antes de registrar; a task fica bloqueada até lá):** quem define o dia de pregão: relógio UTC, calendário B3 ou o primeiro Logon com `141=Y`. Esta edição não escolhe a regra.

**Passos:**
1. Com a decisão do usuário, registrar em `design.md` §3.4.3 a regra de virada de dia e o fuso.

**Saída:** `design.md` §3.4.3 diz quem vira o dia e em qual fuso. Nenhum seqnum é zerado por esta fatia.

---

- [ ] TD-12b — `BeginEpoch` no primeiro Logon do dia

**Origem:** `notes/seqnum-rethink.md` §4 e §7.1 item 5; `notes/session-log-rethink.md` §3.2. **Tamanho:** P. **Depende de:** TD-12a.

**Objetivo:** No primeiro Logon do dia, abrir época nova e zerar os contadores, com `141=Y`. Restart no mesmo dia não zera.

**Entradas:** `design.md` §3.4.3, `SessionLogManager`, `FixSession`, `FixMessageBuilder.BuildLogon`, TD-12a.

**Passos:**
1. O `SessionLogManager` e o `FixSession` abrem época nova (`BeginEpoch`) e zeram `nextOut` e `nextInExpected` no primeiro Logon do dia, enviando `141=Y` (TD-05).
2. Restart no mesmo dia não zera.
3. Testes com relógio injetado: virada de dia zera e abre época; restart no mesmo dia continua de onde parou.

**Saída:** O initiator reconecta no dia seguinte com seqnum 1 e época nova, e no mesmo dia continua de onde parou.

---

- [ ] TD-12c — Domingo para segunda e retenção

**Origem:** `notes/seqnum-rethink.md` §4; `notes/session-log-rethink.md` §3.2. **Tamanho:** P. **Depende de:** TD-12b.

**Objetivo:** Domingo para segunda não zera o seqnum. A retenção por dia de pregão usa a mesma regra de virada.

**Entradas:** `design.md` §3.4.3, `SessionLogManager`, TD-12b.

**Passos:**
1. Sessões fora do horário da B3 (domingo para segunda no Drop Copy) não zeram.
2. Retenção por dia de pregão passa a usar a mesma regra.
3. Teste com relógio injetado: de domingo para segunda o `seq` e a época permanecem, e a retenção corta pelo mesmo dia de pregão.

**Saída:** A passagem de domingo para segunda não abre época nova, e a retenção segue a regra registrada em TD-12a.

---

- [x] TD-13 — Payload JSON nos dispatchers de broker (`PayloadEncoding`)

**Origem:** FR-EGR-13. **Tamanho:** M. **Depende de:** TD-04b.

**Objetivo:** Kafka e RabbitMQ publicam por padrão um envelope JSON legível em vez do frame FIX bruto, com opção de voltar ao bruto.

**Entradas:** FR-EGR-13, `KafkaMessageDispatcher`, `RabbitMQMessageDispatcher`, options de Egress e de Host.

**Passos:**
1. `BrokerPayloadEncoding` (`Json` default, `Raw`) nas options de Egress e de Host, validado na subida.
2. `FixJsonProjector`: projeta o frame no envelope `{"type","msgType","ts","fields"}`, na ordem do wire, sem as tags 8, 9, 10 e 35; tag repetida vira array.
3. Projeção no consume loop do dispatcher, nunca no hot path. Log de sessão e cursor seguem sobre o frame bruto. No RabbitMQ, `Json` marca `content_type=application/json`.
4. `appsettings.json` roteia a sessão também para `rabbitmq-fix-raw`. README documenta o `PayloadEncoding`.
5. Testes: `FixJsonProjectorTests` e `FanOutEndToEndTests`.

**Saída:** Um consumidor de Kafka ou RabbitMQ recebe JSON por padrão e frame bruto com `PayloadEncoding: Raw`.

---

- [x] TD-14 — ClientProbe: seqnum persistido e entrega JSON do RabbitMQ

**Origem:** TD-13. **Tamanho:** M. **Depende de:** TD-13.

**Objetivo:** O ClientProbe (`utils/client`) acompanha o payload JSON publicado pelo RabbitMQ e mantém o seqnum FIX entre execuções.

**Entradas:** `utils/client/src/ClientProbe` (`Fix/`, `Rabbit/`, `Probe/`, `Telemetry/`, `ClientApp/`), TD-13.

**Passos:**
1. `FixSeqStore`: persiste o seqnum do initiator do probe, usado por `FixInitiatorAdapter` e `FixSessionWriter`.
2. `RabbitConsumerAdapter` e `ShortFieldExtractor` leem o corpo JSON do TD-13; perfis (`FixProfile`, `RabbitProfile`, `ProbeProfileParser`) e `appsettings.json` ganham as opções correspondentes.
3. Painel web (`ConfigModal`, `FixScreen`, `config.ts`, `profile.ts`, `client.ts`) expõe as novas opções.
4. Testes: `FixInitiatorAdapterTests`, `ShortFieldExtractorTests`, `RabbitConsumerAdapterTests`.

**Saída:** O probe lê mensagens JSON do RabbitMQ e retoma o seqnum FIX da execução anterior.

---

### TD-15 — Storage diário e modo de entrega no Logon (contexto comum de TD-15a a TD-15d)

**Origem:** teste manual de 2026-11-11 com o ClientProbe.

**Problema observado:** o `FixOutboundDispatcher.SendAsync` só numera uma mensagem no índice dos clientes já conhecidos (`_known`). O cliente só entra nessa lista no primeiro Logon (`Remember`). Mensagens recebidas antes disso ficam apenas no log de ingress. Com `141=Y`, o `ResetClient` abre uma época vazia. Com `789`, não há nada a reenviar porque o índice do cliente está vazio. Resultado: um cliente que conecta no meio do dia não recebe o que já passou, em nenhuma combinação de flags.

**Objetivo:** Um cliente FIX (ClientProbe ou QuickFIX) que conecta durante o dia escolhe entre receber do Logon em diante (`FromLogon`) ou receber tudo do dia (`StartOfDay`). O dia segue o QuickFIX: à meia-noite o router abre um storage novo para o dia. Os brokers continuam com o fluxo atual pelo cursor.

**Decisões do usuário (2026-11-11):**
- A escolha do modo vem da configuração do acceptor, por dispatcher e por `SenderCompID` (`ClientOverrides`), e pode ser sobrescrita por uma tag do Logon.
- O dia vira à meia-noite, como no QuickFIX, e o router cria um storage novo para o dia.
- `141=Y` sempre zera os seqnums dos dois lados. O modo decide se a época nova começa com o histórico do dia ou vazia. Reconexão sem `141=Y` segue a recuperação normal (`789` / `ResendRequest`, TD-07 e TD-08).

A tag `ReplayMode` (19001), os blocos reservados e o uso pelos clientes estão no README, seção "Tags FIX customizadas do NexusRouter", que é a fonte de verdade.

Cada fatia abaixo registra a própria regra em `requirements.md` §2.4.1 e `design.md` §3.4.3/§3.4.7. A virada do storage do router é independente do seqnum do initiator com a B3, que continua com TD-12a/b/c.

---

- [ ] TD-15a — Storage diário: virada à meia-noite

**Tamanho:** M. **Depende de:** TD-07, TD-08, T-44.

**Objetivo:** À meia-noite o router abre um storage novo para o dia, como o QuickFIX.

**Entradas:** contexto (`./scripts/task.sh nexusrouter TD-15`), `design.md` §3.4.3, §3.4.5 e §3.4.6, `SessionLogManager`, `ClientIndex`, `StreamCatchUp`, `BrokerCursorTracker`, `FixOutboundDispatcher`, `FixAcceptedSession`.

**Passos:**
1. Registrar a regra de virada (novo FR-RPL-14, parte 1) e o fuso no `design.md` §3.4.3.
2. Opção `SessionLog.DayRolloverTimeZone` (default `America/Sao_Paulo`), validada na subida. Relógio injetável.
3. À meia-noite: época nova no log de ingress e nos índices por cliente (`{yyyyMMdd}-e{epoch}` do §3.4.6). As sessões aceitas conectadas recebem `Logout` com `Text` e voltam com seq 1.
4. Restart no mesmo dia não abre storage novo.
5. Brokers atravessam a virada pela drenagem de época do `StreamCatchUp`, sem perda e sem duplicata além do "ao menos uma vez" do TD-04b.
6. Testes com relógio injetado: (a) virada abre storage novo, derruba as sessões aceitas e o dia novo começa vazio; (b) restart no mesmo dia não abre storage novo; (c) broker entrega tudo do dia anterior e segue no dia novo.

**Saída:** O storage do router vira à meia-noite, e um restart no mesmo dia continua no storage do dia.

---

- [ ] TD-15b — `ReplayMode`: opção, tag 19001 e modo `FromLogon`

**Tamanho:** P. **Depende de:** TD-15a.

**Objetivo:** O acceptor decide o modo de cada Logon. Esta fatia entrega a decisão e o `FromLogon`. O `StartOfDay` vem na TD-15c.

**Entradas:** contexto (`./scripts/task.sh nexusrouter TD-15`), README "Tags FIX customizadas do NexusRouter", `design.md` §3.4.7, `AcceptorRecoveryOptions`, `AcceptorClientOverride`, options do Host, `FixOutboundDispatcher` (`TryAcceptLogon`, `DecideLogon`).

**Passos:**
1. Registrar os modos e a precedência (FR-RPL-14, parte 2) no `design.md` §3.4.7, incluindo o default.
2. `Recovery.ReplayMode` (`FromLogon` | `StartOfDay`) em `AcceptorRecoveryOptions`, sobrescrita em `AcceptorClientOverride` e no options do Host, com validação.
3. Ler a tag 19001 no Logon (`1` = `FromLogon`, `2` = `StartOfDay`). Ela prevalece sobre a configuração. Valor desconhecido leva a `Logout` com `Text`.
4. O modo só vale quando o Logon abre uma época do cliente: com `141=Y`, ou no primeiro Logon do cliente no dia (índice do dia inexistente). Reconexão sem `141=Y` com índice do dia mantém o TD-07 e o TD-08.
5. Até a TD-15c, `StartOfDay` é aceito e se comporta como `FromLogon`, com `WARN` no log.
6. Métrica e log do modo aplicado por Logon.
7. Testes: (a) `FromLogon` recebe só o tráfego ao vivo; (b) a tag sobrescreve a configuração; (c) valor inválido recebe `Logout`; (d) reconexão sem `141=Y` segue o TD-07 e o TD-08.

**Saída:** O modo de cada Logon é resolvido e aplicado. `FromLogon` funciona de ponta a ponta.

---

- [ ] TD-15c — Modo `StartOfDay`: backfill do dia no índice do cliente

**Tamanho:** P. **Depende de:** TD-15b.

**Objetivo:** Um cliente com `StartOfDay` recebe tudo do dia a partir do seq 1 e depois o tráfego ao vivo.

**Entradas:** contexto (`./scripts/task.sh nexusrouter TD-15`), `design.md` §3.4.4 (escritores do `.cidx`, transição channel ↔ log), `FixOutboundDispatcher.SendAsync`, `ClientIndex`, `ISessionLog` (leitura do log de ingress), `FixAcceptedSession`.

**Passos:**
1. Ao abrir a época do cliente com `StartOfDay`, numerar no `.cidx`, a partir de 1, todas as mensagens de aplicação do log de ingress do dia, na ordem do log. Só depois o cliente entra na numeração ao vivo do `SendAsync`.
2. Na junção entre backfill e ao vivo, não pode haver buraco nem duplicata para uma mensagem que chegue durante o backfill. Registrar o mecanismo no `design.md` §3.4.4.
3. A entrega do backfill usa o caminho de reenvio da sessão (fora do hot path).
4. Métrica e log da quantidade de mensagens do backfill.
5. Remover o `WARN` provisório da TD-15b.
6. Testes: (a) cliente novo com `StartOfDay` recebe as N mensagens anteriores e depois as ao vivo, em ordem e sem duplicata; (b) `141=Y` com `StartOfDay` no meio do dia reentrega o dia desde o seq 1; (c) mensagem que chega durante o backfill não se perde nem duplica.

**Saída:** `StartOfDay` entrega o dia inteiro e emenda com o ao vivo sem buraco.

---

- [ ] TD-15d — ClientProbe: seletor do `ReplayMode`

**Tamanho:** P. **Depende de:** TD-15b.

**Objetivo:** O ClientProbe envia a tag 19001 no Logon.

**Entradas:** README "Tags FIX customizadas do NexusRouter", `utils/client/src/ClientProbe` (`Probe/FixProfile.cs`, `Probe/ProbeProfileParser.cs`, `Fix/FixSessionWriter.cs`, `Fix/FixInitiatorAdapter.cs`, `appsettings.json`, painel web).

**Passos:**
1. Opção do modo no `FixProfile`, no `ProbeProfileParser` e no `appsettings.json` (sem valor = não envia a tag).
2. O `FixSessionWriter` envia `19001` no Logon quando o modo está definido.
3. Seletor no painel web, ao lado de "zerar sequência no próximo logon".
4. Mudar o status da tag 19001 no README para "disponível" quando a TD-15c também estiver entregue.
5. Testes: `FixSessionWriter` com e sem a tag; parser do perfil.

**Saída:** Pelo probe, o usuário escolhe receber do Logon em diante ou o dia inteiro.

---

### TD-16 — Log de mensagens FIX legível nas duas pontas (contexto comum de TD-16a e TD-16b)

**Origem:** pedido do usuário em 2026-11-11, para auditoria.

**Problema:** o `.log` binário guarda só o que entrou da B3. O `.cidx` guarda ponteiros, e as administrativas do acceptor ficam sem payload (`kind=2`). O que o initiator envia à B3 não é gravado. Hoje uma auditoria não prova o que o router enviou nem para a B3 nem para os clientes. O Timescale (`ingress_fix_messages`) cobre só a entrada.

**Decisões do usuário (2026-11-11):**
- Log separado, legível como o `messages.log` do QuickFIX (frame com SOH, uma mensagem por linha), sem mexer no formato binário de recuperação.
- Nenhuma mensagem pode ficar fora do log de auditoria. Descartar está proibido.

**Desenho que cumpre as duas decisões:** o texto é uma **projeção** de logs binários duráveis, nunca alimentado por channel em memória. Um writer em background lê o que já está `committed` e grava o texto, com cursor persistido. Se ele atrasar, o atraso vira métrica de lag, sem perda e sem bloquear o hot path. Depois de uma queda, ele retoma do cursor. Para isso, a saída também precisa estar num log binário (TD-16a).

---

- [ ] TD-16a — Log binário de saída: acceptor e initiator

**Tamanho:** M. **Depende de:** TD-15a.

**Objetivo:** Todo frame que o router escreve num socket FIX fica gravado num log binário durável, antes do envio.

**Entradas:** contexto (`./scripts/task.sh nexusrouter TD-16`), `design.md` §3.4.3 a §3.4.6, `SessionLogManager`/`SessionLogStream`, `FixAcceptedSession` (escritor do socket), `FixSession` (envio do initiator), `SessionLogAppendBenchmark`.

**Passos:**
1. Registrar no `design.md` §3.4.6 o stream de saída por sessão (pastas, época e dia seguindo a TD-15a) e a regra write-ahead: grava antes de escrever no socket. Um frame gravado e não enviado por queda é aceitável, como no QuickFIX. Enviado sem gravar, nunca.
2. Acceptor: os bytes finais de cada frame (aplicação reescrita, administrativas, gap-fill e reenvio com `43`/`122`) são gravados pelo escritor único do socket, fora do hot path de ingress.
3. Initiator: os frames enviados à B3 (Logon, Heartbeat, TestRequest, ResendRequest, Reject, Logout) são gravados no stream de saída da sessão.
4. Falha de append segue o §3.4.4: estado degradado, métrica e alerta, e a entrega continua (decisão do usuário, 2026-11-11). O frame enviado sem gravação não entra no log de saída, e o trecho fica marcado como degradado para a auditoria. A prevenção é o alerta de disco da TD-17.
5. Benchmark: custo do append por frame de saída e latência de entrega ao cliente antes e depois.
6. Testes: cada tipo de frame enviado aparece no log de saída, na ordem do socket; queda simulada entre append e envio.

**Saída:** O que o router envia, nas duas pontas, fica num log binário recuperável.

---

- [ ] TD-16b — Projeção legível `messages.log` com cursor

**Tamanho:** M. **Depende de:** TD-16a.

**Objetivo:** Um arquivo de texto por sessão e por dia, legível como o do QuickFIX, com entrada e saída, sem perda.

**Entradas:** contexto (`./scripts/task.sh nexusrouter TD-16`), `design.md` §3.4.6, resultado da TD-16a, `SessionLogManager`, options do `SessionLog`.

**Passos:**
1. Registrar no `design.md` o formato da linha (timestamp UTC, direção `IN`/`OUT`, sessão, frame com SOH preservado) e o caminho dos arquivos por dia.
2. Writer em background por sessão que lê os streams de entrada e de saída até o `committed`, intercala por ordem de gravação e grava o texto. Cursor persistido por stream. Na abertura, confere a última linha para não duplicar.
3. Métrica de lag do writer (mensagens e tempo atrás do `committed`) e alerta.
4. A retenção do texto segue a do log binário, por dia (TD-15a). Opção para retenção maior do texto, se necessária para auditoria.
5. Testes: (a) toda mensagem de entrada e de saída aparece uma vez, na ordem; (b) queda do processo e restart não perdem nem duplicam linhas; (c) writer atrasado não afeta o hot path e alcança depois.

**Saída:** `messages.log` legível por sessão e por dia, com tudo o que entrou e saiu, reconstruível a partir dos logs binários.

---

- [ ] TD-17 — Alerta de disco perto do limite (80% moderado, 90% crítico)

**Origem:** pedido do usuário em 2026-11-11, junto com a decisão do TD-16a (falha de append mantém a entrega). **Tamanho:** P. **Depende de:** TD-04, T-33, T-34.

**Problema:** o alerta do §3.4.4 só dispara quando o append já falhou, com o disco cheio. A partir daí o trecho fica sem gravação para recuperação e para auditoria. Não há aviso antes.

**Objetivo:** Avisar com antecedência quando o volume do storage se aproxima do limite: 80% de uso é alerta moderado, 90% é crítico.

**Entradas:** `design.md` §3.4.4 (Falha de escrita) e §3.4.6, `SessionLogManager` (task de pré-alocação e rotação), options do `SessionLog`, instrumentação do T-33 e endpoint do T-34.

**Passos:**
1. Registrar a regra no `design.md` §3.4.4, ao lado de "Falha de escrita".
2. Opções `SessionLog.DiskWarningPercent` (default 80), `SessionLog.DiskCriticalPercent` (default 90) e `SessionLog.DiskCheckIntervalSeconds`, validadas na subida (warning < critical ≤ 100).
3. Task de background (nunca o hot path) mede o volume de `SessionLog.Directory` e de toda pasta do storage que esteja em outro volume (inclusive a do TD-16b).
4. Métricas: gauge `sessionlog_disk_used_ratio` e `sessionlog_disk_free_bytes` por volume, e gauge de nível (`0` ok, `1` moderado, `2` crítico).
5. Log na troca de nível: `Warning` ao passar de 80%, `Critical` ao passar de 90%, `Information` ao voltar. O nível também vai como evento para o channel de auditoria. Com o nível ativo, repetir o log em intervalo configurável, sem inundar.
6. Os segmentos são pré-alocados (`SegmentSizeMB`). Antes de pré-alocar o próximo segmento, a task confere se ele cabe sem cruzar o nível crítico e, se não couber, emite o crítico na hora.
7. Documentar no README as métricas e um exemplo de regra Prometheus para os dois níveis. O Alertmanager e o roteamento do alerta ficam fora do repositório.
8. Testes com medidor de disco injetado: 79% sem alerta; 80% moderado; 90% crítico; volta abaixo de 80% normaliza; pré-alocação que cruzaria 90% emite crítico; opções inválidas falham na subida.

**Saída:** O operador recebe alerta moderado a 80% e crítico a 90% de uso do volume do storage, antes de o append falhar.

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
                          │    ├── T-22 (FixOutbound)
                          │    │    ├── T-23 (Serializer)
                          │    │    └── T-24 (TLS)
                          │    │         └── T-25 (SeqNum Store)
                          │    └── T-49a (options WebSocket)
                          │         └── T-49b (upgrade)
                          │              └── T-49c (sessão e fila)
                          │                   ├── T-49d (liveness)
                          │                   └── T-50a (token) ◄── também T-44
                          │                        └── T-50b (encoding e fields)
                          │                             ├── T-50c (seq) ◄── também TD-04, TD-04a
                          │                             │    └── T-51a (seq sem sessão)
                          │                             │         └── T-51b (encoder JSON)
                          │                             │              └── T-51c (encoder binário)
                          │                             │                   └── T-51d (projeção) ◄── também T-49c
                          │                             └── T-52a (pedido replay) ◄── também T-49d
                          │                                  └── T-52b (reenvio) ◄── também T-51d
                          │                                       └── T-52c (replay ao reconectar) ◄── também T-50c
                          │                                            └── T-53 (integração) ◄── também T-51d
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
 └── T-44 (cadastro/UUID) ◄── também depende de TD-04, TD-04a, TD-04b, TD-08 (Fase 11)
      ├── T-47 (broker recupera pelo cursor)
      └── T-48 (testes de retransmissão)
      [T-45 e T-46 removidas]

Fase 11 (melhorias e débitos)
T-11 ── TD-01 (GapFill no ResendRequest) [feito]
T-13 + T-22 ── TD-02 (rewriter com mudança de tamanho) [feito]
TD-03 (steering x PDFs B3)
 ├── TD-04 (log de sessão: segmentos, índice, estado) ◄── absorve TD-09
 │    ├── TD-04a (fan-out grava no log; dispatcher com cursor)
 │    │    ├── TD-04b (ao menos uma vez no broker + header)
 │    │    └── TD-08 (reenvio: aplicação do log, gap-fill admin)
 │    ├── TD-05 (Logon completo)
 │    └── TD-06 (validação e Reject)
 └── TD-07 (acceptor FIX) ◄── TD-04 + TD-05 (Logon autenticado antes de criar arquivos)

TD-04b ── TD-10a (tag 440, options, decisão)
          └── TD-10b (scanner no consume loop)
               ├── TD-10c (routing key e headers no RabbitMQ)
               └── TD-10d (record headers no Kafka)
TD-06 ── TD-11 (SequenceReset recebido no initiator)
TD-04 + TD-05 ── TD-12a (registrar dia e fuso; bloqueada na decisão)
                 └── TD-12b (BeginEpoch no primeiro Logon)
                      └── TD-12c (domingo e retenção)
TD-07 + TD-08 + T-44 ── TD-15a (storage diário, virada à meia-noite)
                        ├── TD-15b (ReplayMode: opção, tag 19001, FromLogon)
                        │    ├── TD-15c (StartOfDay: backfill do dia)
                        │    └── TD-15d (ClientProbe: seletor do modo)
                        └── TD-16a (log binário de saída, acceptor e initiator)
                             └── TD-16b (projeção legível messages.log)
TD-04 + T-33 + T-34 ── TD-17 (alerta de disco 80% / 90%)

TD-04 inclui o portão de latência do append (FR-RPL-09); TD-04a só começa com ele aprovado.

Ordem de execução: TD-01 a TD-08 estão concluídos. T-44, T-47 e T-48 também.
Pendentes: Fase 10 (T-49a a T-53) e, independentes entre si, TD-10a a TD-10d, TD-11, TD-12a a TD-12c, TD-15a a TD-15d, TD-16a a TD-16b e TD-17.
```
