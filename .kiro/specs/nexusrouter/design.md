# NexusRouter — Documento de Design de Arquitetura

> **Status do Documento:** Rascunho v1.0  
> **Classificação:** Especificação de Engenharia Interna  
> **Domínio:** High-Frequency Trading (HFT) — Motor de Execução de Ordens / DropCopy Distribution Engine

---

## 1. Filosofia Arquitetural

O NexusRouter é projetado em torno de uma única restrição fundamental: **o hot path deve ter latência determinística sub-50µs do recebimento do byte até o handoff ao dispatcher**. Cada decisão arquitetural é avaliada primeiro contra essa restrição. O design separa responsabilidades em quatro tiers com **regras de acoplamento estritamente assimétricas**: tiers inferiores (Ingress, Core Router) não podem bloquear sincronamente em tiers superiores (Egress, Audit). A informação flui em uma única direção — de entrada para o router, de saída para os dispatchers — sem que back-pressure atravesse um limite de tier de volta para o hot path.

---

## 2. Arquitetura Conceitual de Alto Nível

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                                   NexusRouter Process                                │
│                                                                                     │
│  ┌──────────────────────────────────────────────────────────────────────────────┐   │
│  │                          INGRESS TIER                                        │   │
│  │                                                                              │   │
│  │   ┌─────────────────┐   ┌─────────────────┐   ┌─────────────────┐          │   │
│  │   │ Session A       │   │ Session B       │   │ Session N       │          │   │
│  │   │ (FIX 4.4)       │   │ (FIX 5.0/FIXT) │   │ (FIX 4.2)       │          │   │
│  │   │                 │   │                 │   │                 │          │   │
│  │   │ TcpClient       │   │ TcpClient       │   │ TcpClient       │          │   │
│  │   │ + PipeReader    │   │ + PipeReader    │   │ + PipeReader    │          │   │
│  │   │ + SessionFSM    │   │ + SessionFSM    │   │ + SessionFSM    │          │   │
│  │   │ + FrameDecoder  │   │ + FrameDecoder  │   │ + FrameDecoder  │          │   │
│  │   └────────┬────────┘   └────────┬────────┘   └────────┬────────┘          │   │
│  │            │                     │                     │                   │   │
│  └────────────┼─────────────────────┼─────────────────────┼───────────────────┘   │
│               │  IngressFrame (ref struct, rented buffer)  │                       │
│               └─────────────────────┬───────────────────── ┘                       │
│                                     │                                               │
│  ┌──────────────────────────────────▼───────────────────────────────────────────┐   │
│  │                          CORE ROUTER / HOT PATH                              │   │
│  │                                                                              │   │
│  │   ┌─────────────────────────────────────────────────────────────────────┐   │   │
│  │   │  RouterEngine                                                       │   │   │
│  │   │                                                                     │   │   │
│  │   │   IngressFrame                                                      │   │   │
│  │   │       │                                                             │   │   │
│  │   │       ▼                                                             │   │   │
│  │   │   ┌──────────────────────────────────────────┐                     │   │   │
│  │   │   │  RouteResolver                           │                     │   │   │
│  │   │   │  (immutable routing table, lock-free     │                     │   │   │
│  │   │   │   dictionary lookup by SessionId)        │                     │   │   │
│  │   │   └──────────────────────┬───────────────────┘                     │   │   │
│  │   │                          │ RouteContext                             │   │   │
│  │   │                          ▼                                         │   │   │
│  │   │   ┌──────────────────────────────────────────┐                     │   │   │
│  │   │   │  DeliveryMode Switch                     │                     │   │   │
│  │   │   │  ┌──────────────┐  ┌───────────────────┐ │                     │   │   │
│  │   │   │  │ PassThrough  │  │  Parse            │ │                     │   │   │
│  │   │   │  │ (SeqNum +    │  │  (FixTokenizer →  │ │                     │   │   │
│  │   │   │  │  Checksum    │  │   FixMessage      │ │                     │   │   │
│  │   │   │  │  rewrite on  │  │   value type)     │ │                     │   │   │
│  │   │   │  │  Span<byte>) │  │                   │ │                     │   │   │
│  │   │   │  └──────┬───────┘  └─────────┬─────────┘ │                     │   │   │
│  │   │   └─────────┼─────────────────── ┼────────────┘                     │   │   │
│  │   │             └──────────┬──────────┘                                 │   │   │
│  │   │                        │ DispatchPayload                            │   │   │
│  │   │                        ▼                                            │   │   │
│  │   │   ┌──────────────────────────────────────────┐                     │   │   │
│  │   │   │  FanOut Engine                           │                     │   │   │
│  │   │   │  (TryWrite to each dispatcher's          │                     │   │   │
│  │   │   │   bounded Channel — non-blocking)        │                     │   │   │
│  │   │   └──────────────────────────────────────────┘                     │   │   │
│  │   └─────────────────────────────────────────────────────────────────────┘   │   │
│  │                                                                              │   │
│  │   ┌─────────────────────────────────────────────────────────────────────┐   │   │
│  │   │  Audit Channel (TryWrite — non-blocking)                            │   │   │
│  │   └─────────────────────────────────────────────────────────────────────┘   │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
│                                     │                                           │
│  ┌──────────────────────────────────▼───────────────────────────────────────┐   │
│  │                          EGRESS TIER                                     │   │
│  │                                                                          │   │
│  │  ┌────────────────────┐  ┌──────────────────────┐  ┌──────────────────┐ │   │
│  │  │ KafkaMessage       │  │ RabbitMQ             │  │ FixOutbound      │ │   │
│  │  │ Dispatcher         │  │ MessageDispatcher    │  │ Dispatcher       │ │   │
│  │  │                    │  │                      │  │                  │ │   │
│  │  │ Channel<Payload>   │  │ Channel<Payload>     │  │ Channel<Payload> │ │   │
│  │  │ ↓                  │  │ ↓                    │  │ ↓                │ │   │
│  │  │ Confluent.Kafka    │  │ RabbitMQ.Client      │  │ PipeWriter       │ │   │
│  │  │ IProducer          │  │ IChannel             │  │ (outbound FIX)   │ │   │
│  │  └────────────────────┘  └──────────────────────┘  └──────────────────┘ │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
│                                                                                  │
│  ┌───────────────────────────────────────────────────────────────────────────┐  │
│  │                          COLD PATH / AUDIT TIER                           │  │
│  │                                                                           │  │
│  │  Channel<AuditRecord> (bounded) → AuditWriterService                      │  │
│  │       ├─► Serilog Sink (arquivo / console — metadados operacionais)       │  │
│  │       └─► PostgresAuditSink (Npgsql COPY → TimescaleDB hypertable)        │  │
│  └───────────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Detalhamento dos Tiers

### 3.1 Ingress Tier

Cada sessão upstream é representada por uma instância de `FixSession`, que é um `IHostedService` rodando em sua própria thread dedicada (via `Task.Factory.StartNew` com `TaskCreationOptions.LongRunning` para fixá-la fora do thread-pool).

**Leitura do Socket:** `System.IO.Pipelines` é usado exclusivamente. Um `PipeWriter` é alimentado pelo loop `Socket.ReceiveAsync` usando buffers pré-alocados de `MemoryPool<byte>.Shared`. O `PipeReader` na outra ponta enquadra as mensagens. Isso elimina as cópias duplas que ocorreriam com uma abordagem ingênua de buffer `byte[]`.

**Decodificação de Frames:** Uma interface plugável `ISessionFrameDecoder` trata o framing específico do protocolo (ex.: framing delimitado por FIX BeginString + BodyLength, ou SBE com prefixo SOFH). Ela retorna um `IngressFrame` — um `ref struct` contendo um slice `ReadOnlySequence<byte>` no buffer do pipe, mais metadados (SessionId, timestamp de ingresso).

**Session FSM:** O `FixSessionStateMachine` gerencia os estados do ciclo de vida da sessão: `Disconnected → Connecting → LogonSent → Active → LogoutSent → Disconnected`. Apenas mensagens administrativas de nível de sessão (Heartbeat, TestRequest, ResendRequest, Logout) são processadas dentro da FSM. Mensagens de aplicação são passadas imediatamente para o router sem parsing adicional no modo `PassThrough`.

**Reconexão:** Cada `FixSession` possui um objeto de valor `ExponentialBackoffReconnectPolicy` contendo `BaseDelay`, `MaxDelay` e `JitterFactor`. Na desconexão, a sessão re-entra no estado `Connecting` após o delay calculado.

---

### 3.2 Core Router / Hot Path

O `RouterEngine` é o coração do sistema. É stateless do ponto de vista de roteamento — todo o estado de roteamento é imutável após a inicialização e reside em um `FrozenDictionary<SessionId, RouteContext>` (usando `System.Collections.Frozen`, disponível desde o .NET 8 e otimizado no .NET 10) para leituras lock-free O(1) com comportamento de cache otimizado.

**Lookup de Roteamento:** A cada chegada de `IngressFrame`, o router resolve o `RouteContext` para o `SessionId`. Trata-se de um único lookup no dicionário, sem locks necessários graças à coleção frozen imutável.

**Despacho por DeliveryMode:**
- No modo `PassThrough`: o router invoca o `PassThroughRewriter`, que opera no `Span<byte>` bruto, encontrando e reescrevendo apenas a `tag 34` (MsgSeqNum) e a `tag 10` (CheckSum) usando um scanner de campos zero-allocation.
- No modo `Parse`: o router invoca o `FixTokenizer`, que produz um `FixMessage` — um `ref struct` que encapsula um array stack-allocated ou poolizado de entradas `FixField` (pares tag-valor como pares `int`/`ReadOnlySpan<byte>`). Nenhuma alocação de string gerenciada ocorre.

**FanOut:** Para cada dispatcher registrado no span `RouteContext.Dispatchers`, o router chama `TryWrite` no `Channel<DispatchPayload>` do dispatcher. Isso é não bloqueante por design. Se um channel estiver cheio, o overflow é registrado no channel de auditoria (também via `TryWrite`) e a mensagem é descartada apenas para aquele dispatcher. Os demais dispatchers do fan-out não são afetados.

---

### 3.3 Egress Tier

Todos os dispatchers compartilham o mesmo padrão operacional:

1. Uma `Task` de background (iniciada em `StartAsync`) faz loop em `ChannelReader<DispatchPayload>.ReadAllAsync`.
2. O reader retira payloads da fila e invoca a lógica de envio específica do protocolo.
3. Erros são capturados, registrados no logger estruturado, e não são relançados de volta ao consume loop.
4. Em `StopAsync`, o writer do `Channel` do dispatcher é completado e o consume loop drena os itens restantes antes de retornar.

**`FixOutboundDispatcher`:** Mantém seu próprio `PipeWriter` outbound por cliente FIX downstream conectado. Gerencia seu próprio contador outbound `MsgSeqNum` (usando `Interlocked.Increment`). Em cenários `PassThrough`, o buffer reescrito é gravado diretamente no pipe outbound; no modo `Parse`, ele serializa o `FixMessage` de volta para o formato wire.

**`KafkaMessageDispatcher`:** Encapsula um `Confluent.Kafka.IProducer<Null, byte[]>`. Payloads são publicados em um tópico configurado. A estratégia de chave de partição (ex.: por `ClOrdID`, `SenderCompID` ou round-robin) é uma `IKafkaPartitionStrategy` configurável injetada na construção.

O producer é **agnóstico ao provedor**. `ConnectAsync` monta um `ProducerConfig` a partir de `KafkaDispatcherOptions` e não ramifica em “AWS vs local”. O mesmo código cobre:

| Ambiente | `SecurityProtocol` | `SaslMechanism` | Bootstrap típico |
|----------|--------------------|-----------------|------------------|
| Docker Compose / Kafka auto-gerenciado PLAINTEXT | `Plaintext` | `None` | `kafka:9092` (rede Compose) ou `localhost:19092` (host) |
| Kafka auto-gerenciado com TLS | `Ssl` | `None` | brokers internos `:9093` |
| Kafka auto-gerenciado / Confluent Cloud SASL | `SaslSsl` | `Plain` ou `ScramSha512` | bootstrap do cluster |
| Amazon MSK SASL/SCRAM | `SaslSsl` | `ScramSha512` | `b-#.xxx.kafka.region.amazonaws.com:9096` |
| Amazon MSK TLS mútuo | `Ssl` | `None` | bootstrap `:9094` |
| Amazon MSK IAM (opt-in) | `SaslSsl` | `AwsMskIam` | bootstrap `:9098` |

`AwsMskIam` é o único caso que carrega um callback OAUTHBEARER (pacote AWS MSK Auth). Não entra no DI a menos que o mechanism esteja selecionado. Credenciais SASL nunca ficam no POCO — só nomes de variáveis de ambiente.

**`RabbitMQMessageDispatcher`:** Encapsula um `IModel` (cliente clássico) ou `IChannel` (futuro cliente async v7+). Cada instância de dispatcher mantém um channel dedicado para evitar contenção. O flag mandatory e as confirmações de publisher são configuráveis — desabilitados por padrão para latência mínima.

---

### 3.4 Cold Path / Audit Tier

Um `AuditWriterService` (implementando `IHostedService`) possui um `Channel<AuditRecord>` com capacidade limitada (configurável, ex.: 65.536 itens). Drena o channel via `ReadAllAsync` e grava registros em lotes nos sinks registrados (`IAuditSink`). O tamanho do lote e o intervalo de flush são configuráveis.

O hot path apenas chama `auditChannel.Writer.TryWrite(record)` — não bloqueante. A cópia do payload FIX para `AuditRecord.RawFix` (quando `PersistMode=Full` ou amostra em `Sampled`) ocorre **depois** do fan-out, ainda na thread da sessão, mas apenas um `ArrayPool.Rent` + memcpy + `TryWrite`. Sem SQL, sem socket de banco, sem `await`. Um registro descartado por channel cheio é rastreado via `System.Diagnostics.Metrics` e nunca pode afetar o roteamento.

**Sinks v1:**

1. **`SerilogAuditSink`** — metadados estruturados (ciclo de vida, drops, erros de dispatcher). Não é o store de mensagens.
2. **`PostgresAuditSink`** — persistência canônica das mensagens FIX recebidas via `Npgsql` `COPY` binário (ou batch insert) na hypertable `ingress_fix_messages`.

#### 3.4.1 Por que PostgreSQL + TimescaleDB

Antes desta revisão o cold path só previa Serilog em arquivo (ou um “banco de série temporal” sem produto). Isso **não salvava as mensagens FIX** de forma consultável.

O padrão de dados é append-only, indexado por tempo (`IngressTimestamp`), com consultas do tipo “sessão S no intervalo T”, retenção por idade e ingestão em lote. PostgreSQL puro com uma tabela heap cresce sem bound, o VACUUM compete com o ingest e particionamento manual por dia vira operação. **TimescaleDB** (extensão no mesmo PostgreSQL) resolve isso com:

- **Hypertables** particionadas por `ingress_ts` (chunks diários no default).
- **Compression** nativa de chunks frios (política, ex.: 7 dias).
- **Retention** declarativa (ex.: 90 dias) sem job externo de `DELETE`.
- **Índices** `(session_id, ingress_ts)` e `(cl_ord_id, ingress_ts)` por chunk.

Timescale **é necessário** para este store — não como produto separado, e sim como extensão obrigatória da instância PostgreSQL. A imagem Docker `timescale/timescaledb` *é* PostgreSQL 16 com a extensão. Em AWS o equivalente é RDS PostgreSQL + Timescale (onde licenciado) ou Timescale Cloud / EC2 self-managed com a mesma extensão. O cliente .NET é só `Npgsql`; não há cliente Timescale específico.

**Orçamento de ingestão:** 50k msg/s × ~300 B ≈ 15 MB/s; 200k msg/s ≈ 60 MB/s de payload. Um único Timescale com `COPY` em lote de 1–5k linhas sustenta o laboratório e certificação. Em produção no teto de throughput, `PersistMode=MetadataOnly` ou `Sampled` evita transformar o banco no gargalo; o Kafka continua sendo o bus de alto volume. Persistência nunca é entrega — é auditoria/consulta.

#### 3.4.2 Schema (`ingress_fix_messages`)

```
ingress_ts        timestamptz NOT NULL
session_id        text        NOT NULL
msg_type          text
cl_ord_id         text
msg_seq_num       bigint
delivery_mode     text        NOT NULL
dispatcher_ids    text[]
routing_sequence  bigint
raw_fix           bytea       -- NULL quando MetadataOnly
was_dropped       boolean     NOT NULL DEFAULT false
drop_reason       text
```

Hypertable em `ingress_ts`, `chunk_time_interval = 1 day`. Script de bootstrap em `docker/postgres/init.sql`.

---

## 4. Principais Abstrações e Interfaces

### 4.1 `IMessageDispatcher`

```
IMessageDispatcher
├── Propriedades
│   ├── DispatcherId : string          // Nome único, usado em config de roteamento e registros de auditoria
│   └── IsHealthy    : bool            // Flag de saúde em tempo real, legível sem lock
├── Métodos
│   ├── StartAsync(CancellationToken) : ValueTask
│   ├── StopAsync(CancellationToken)  : ValueTask
│   └── TryEnqueue(DispatchPayload)   : bool    // Enqueue não bloqueante; retorna false se o channel estiver cheio
└── Eventos
    └── OnDispatchError : event        // Disparado em falha de publicação (consumido pelo audit writer)
```

**Justificativa do design:** `TryEnqueue` retorna `bool` em vez de `ValueTask<bool>` para manter o ponto de chamada no hot path estritamente síncrono e livre de alocações. O I/O async real para o broker downstream ocorre dentro do consume loop de background privado do dispatcher, completamente desacoplado do hot path.

### 4.2 `ISessionFrameDecoder`

```
ISessionFrameDecoder
└── TryDecode(ref ReadOnlySequence<byte> buffer, out IngressFrame frame) : bool
    // Tenta decodificar um frame completo do buffer.
    // Avança o buffer além dos bytes consumidos em caso de sucesso.
    // Retorna false se o buffer ainda não contiver um frame completo.
    // Zero allocation: IngressFrame é um ref struct contendo um slice do buffer do pipe.
```

### 4.3 `IngressFrame` (ref struct)

```
IngressFrame (ref struct)
├── SessionId        : SessionId           // Value type — sem alocação heap
├── RawBuffer        : ReadOnlySequence<byte>  // Slice no buffer do PipeReader
├── IngressTimestamp : long                // Environment.TickCount64 ou timestamp RDTSC
└── FrameLength      : int
```

### 4.4 `DispatchPayload`

```
DispatchPayload
├── SessionId        : SessionId
├── DeliveryMode     : DeliveryMode        // PassThrough | Parse
├── RawBuffer        : IMemoryOwner<byte>  // Alugado do ArrayPool; dispatcher responsável pelo dispose
├── ParsedMessage    : FixMessage?         // Não nulo apenas quando DeliveryMode == Parse
├── IngressTimestamp : long
└── RoutingSequence  : long               // Sequência interna para ordenação/dedup
```

**Regra de ownership do buffer:** O router aluga um buffer de `ArrayPool<byte>`, copia o segmento do pipe alugado nele e o encapsula em um `IMemoryOwner<byte>` (usando um struct leve `ArrayPoolMemoryOwner<byte>`). O ownership é transferido para o `DispatchPayload`. O consume loop de cada dispatcher é responsável por chamar `Dispose()` no `IMemoryOwner` após a entrega. Em cenários de fan-out, o buffer é contado por referência usando `Interlocked` — todos os dispatchers recebem o mesmo buffer alugado; o último a chamar `Dispose` o devolve ao pool.

### 4.5 `FixMessage` (ref struct)

```
FixMessage (ref struct)
├── MsgType          : ReadOnlySpan<byte>
├── Fields           : ReadOnlySpan<FixField>   // Stack-allocated, backed by stackalloc ou array poolizado
└── TryGetField(int tag, out ReadOnlySpan<byte> value) : bool
```

### 4.6 `RouteContext`

```
RouteContext (readonly record struct)
├── SessionId        : SessionId
├── DeliveryMode     : DeliveryMode
└── Dispatchers      : ImmutableArray<IMessageDispatcher>
```

Armazenado em um `FrozenDictionary<SessionId, RouteContext>` no `RouterEngine`. O dicionário é construído uma única vez na inicialização e nunca é mutado durante a operação normal.

### 4.7 `IAuditSink`

```
IAuditSink
├── Name : string
├── StartAsync(CancellationToken) : ValueTask
├── WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken) : ValueTask
└── StopAsync(CancellationToken) : ValueTask
```

`AuditWriterService` não conhece Serilog nem Npgsql — apenas `IReadOnlyList<IAuditSink>`. Falha em um sink não aborta os demais.

---

## 5. Modelo de Concorrência

### 5.1 Topologia de Threads

| Componente | Modelo de Thread |
|------------|-----------------|
| `FixSession` (por sessão) | Thread dedicada de longa duração (`TaskCreationOptions.LongRunning`). Uma thread por sessão para o loop de leitura do Pipe. |
| `RouterEngine.Route()` | Chamado na thread dedicada da sessão — sem despacho cross-thread para o hot path em si. |
| Consumidores de `IMessageDispatcher` | Cada dispatcher tem sua própria `Task` de background no thread-pool, consumindo do seu `Channel`. |
| `AuditWriterService` | Única `Task` de background no thread-pool. |

Essa topologia significa que o **hot path nunca sofre context-switch**: uma mensagem chega na thread da sessão, é roteada na mesma thread (lookup no FrozenDictionary é somente leitura), sofre `TryWrite` nos channels dos dispatchers (que são internamente filas concorrentes lock-free), e o controle retorna imediatamente ao loop de leitura do pipe. A thread da sessão possui o pipeline desde o recebimento no socket até a conclusão do fan-out.

### 5.2 Garantias Lock-Free

| Recurso Compartilhado | Mecanismo de Sincronização |
|----------------------|---------------------------|
| Tabela de roteamento (`FrozenDictionary`) | Imutável após inicialização — sem necessidade de sincronização |
| Escritas no channel do dispatcher (`TryWrite`) | `System.Threading.Channels` — implementação MPSC lock-free |
| Escritas no channel de auditoria (`TryWrite`) | `System.Threading.Channels` — implementação MPSC lock-free |
| Contadores de sequence number outbound | `Interlocked.Increment` em `long` |
| Transições de estado de sessão | `Interlocked.CompareExchange` no valor do enum de estado |
| Flag de saúde do dispatcher | `volatile bool` — single writer (thread do dispatcher), múltiplos readers |
| Contagem de referências do buffer (fan-out) | `Interlocked.Decrement` — último decremento para 0 dispara `ArrayPool.Return` |

Nenhum `lock`, `Monitor`, `Mutex`, `SemaphoreSlim` ou `ReaderWriterLockSlim` aparece no hot path. Essas construções são permitidas apenas na inicialização do cold path e em operações administrativas.

### 5.3 Considerações sobre o Modelo de Memória

- Todas as estruturas de dados do hot path são dimensionadas para caber nas linhas de cache L1/L2 onde possível. `RouteContext` é um struct compacto, e a referência `ImmutableArray<IMessageDispatcher>` é um único deref de ponteiro.
- O array de `FixField` em `FixMessage` usa `stackalloc` para mensagens com ≤ 64 campos (o caso comum); recorre a array poolizado para mensagens maiores.
- `DispatchPayload` é uma classe (alocada no heap) para permitir compartilhamento seguro entre channels. O objetivo de zero-allocation refere-se à lógica de processamento de mensagens, não ao envelope do payload em si — o objeto de payload é obtido de um `ObjectPool<DispatchPayload>` e resetado/reciclado.

---

## 6. Modelo de Configuração

```
NexusRouter:
  Sessions:
    - SessionId: "EXCH_A_ORDEXEC"
      Host: "fix-gateway-a.exchange.com"
      Port: 9876
      SenderCompId: "CLIENT1"
      TargetCompId: "EXCHANGE_A"
      HeartbeatIntervalSeconds: 30
      DeliveryMode: PassThrough        # ou Parse
      FrameDecoder: Fix44              # ou Fix50, Sbe
      TLS:
        Enabled: true
        CertificatePath: "/certs/client.pfx"
        CertificatePasswordEnvVar: "NEXUS_CERT_PWD"
      ReconnectPolicy:
        BaseDelayMs: 500
        MaxDelayMs: 30000
        JitterFactor: 0.2
      Routes:
        - DispatcherId: "kafka-main"
        - DispatcherId: "fix-out-client1"

  Dispatchers:
    # Local / Kafka auto-gerenciado (Docker Compose). App no host usa localhost:19092.
    - Id: "kafka-main"
      Type: Kafka
      BootstrapServers: "localhost:19092"
      Topic: "fix.ordexec.raw"
      ClientId: "nexusrouter"
      Acks: 0
      SecurityProtocol: Plaintext
      SaslMechanism: None
      PartitionStrategy: ByClOrdId
      ChannelCapacity: 16384

    # Exemplo Amazon MSK SASL/SCRAM (produção). Secrets só via env.
    # - Id: "kafka-msk"
    #   Type: Kafka
    #   BootstrapServers: "b-1.xxx.kafka.us-east-1.amazonaws.com:9096,b-2.xxx.kafka.us-east-1.amazonaws.com:9096"
    #   Topic: "fix.ordexec.raw"
    #   ClientId: "nexusrouter"
    #   Acks: 1
    #   SecurityProtocol: SaslSsl
    #   SaslMechanism: ScramSha512
    #   SaslUsernameEnvVar: "NEXUS_KAFKA_SASL_USERNAME"
    #   SaslPasswordEnvVar: "NEXUS_KAFKA_SASL_PASSWORD"
    #   EnableSslCertificateVerification: true
    #   PartitionStrategy: ByClOrdId
    #   ChannelCapacity: 16384

    - Id: "fix-out-client1"
      Type: FixOutbound
      Host: "downstream-oms.internal"
      Port: 6001
      SenderCompId: "NEXUS"
      TargetCompId: "OMS1"
      ChannelCapacity: 8192

    - Id: "rabbitmq-dropcopy"
      Type: RabbitMQ
      Host: "rabbitmq.internal"
      VirtualHost: "/trading"
      Exchange: "dropcopy.fanout"
      RoutingKey: ""
      ChannelCapacity: 8192

  Audit:
    ChannelCapacity: 65536
    OverflowMode: DropOldest
    BatchSize: 1000
    FlushIntervalMs: 100
    PersistMode: Full                 # Full | MetadataOnly | Sampled
    SampleRate: 1.0
    Sinks:
      - Type: Serilog
        FilePath: "/var/log/nexusrouter/audit-.log"
        RollingInterval: Hour
      - Type: Postgres
        Host: "localhost"
        Port: 5432
        Database: "nexusrouter"
        UsernameEnvVar: "NEXUS_PG_USER"
        PasswordEnvVar: "NEXUS_PG_PASSWORD"
        SslMode: Disable              # Disable local; Require / VerifyFull em AWS
        CommandTimeoutSeconds: 15
```

---

## 7. Estrutura do Projeto

```
NexusRouter/
├── src/
│   ├── NexusRouter.Domain/               # Value types, interfaces e enums do núcleo
│   │   ├── Abstractions/
│   │   │   ├── IMessageDispatcher.cs
│   │   │   ├── ISessionFrameDecoder.cs
│   │   │   └── IReconnectPolicy.cs
│   │   ├── Models/
│   │   │   ├── IngressFrame.cs           # ref struct
│   │   │   ├── FixMessage.cs             # ref struct
│   │   │   ├── FixField.cs               # readonly struct
│   │   │   ├── DispatchPayload.cs
│   │   │   ├── AuditRecord.cs
│   │   │   └── RouteContext.cs           # readonly record struct
│   │   └── Enums/
│   │       ├── DeliveryMode.cs
│   │       └── SessionState.cs
│   │
│   ├── NexusRouter.Ingress/              # Gerenciamento de sessões e decodificação de frames
│   │   ├── Sessions/
│   │   │   ├── FixSession.cs
│   │   │   ├── FixSessionStateMachine.cs
│   │   │   └── ExponentialBackoffReconnectPolicy.cs
│   │   └── Decoders/
│   │       ├── Fix44FrameDecoder.cs
│   │       ├── Fix50FrameDecoder.cs
│   │       └── SbeFrameDecoder.cs        # Stub para uso futuro
│   │
│   ├── NexusRouter.Router/               # Motor de roteamento do hot path
│   │   ├── RouterEngine.cs
│   │   ├── RouteResolver.cs
│   │   ├── PassThroughRewriter.cs
│   │   ├── FixTokenizer.cs
│   │   └── FanOutEngine.cs
│   │
│   ├── NexusRouter.Egress/               # Implementações de dispatchers
│   │   ├── Kafka/
│   │   │   ├── KafkaMessageDispatcher.cs
│   │   │   └── IKafkaPartitionStrategy.cs
│   │   ├── RabbitMQ/
│   │   │   └── RabbitMQMessageDispatcher.cs
│   │   └── Fix/
│   │       └── FixOutboundDispatcher.cs
│   │
│   ├── NexusRouter.Audit/                # Log de auditoria do cold path
│   │   ├── AuditWriterService.cs
│   │   ├── AuditChannelSingleton.cs
│   │   └── Sinks/
│   │       ├── IAuditSink.cs
│   │       ├── SerilogAuditSink.cs
│   │       └── PostgresAuditSink.cs      # Npgsql COPY → TimescaleDB
│   │
│   └── NexusRouter.Host/                 # Entry point, wiring de DI, configuração
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Configuration/
│       │   ├── NexusRouterOptions.cs
│       │   ├── SessionOptions.cs
│       │   └── DispatcherOptions.cs
│       └── Extensions/
│           └── ServiceCollectionExtensions.cs
│
├── tests/
│   ├── NexusRouter.Domain.Tests/
│   ├── NexusRouter.Ingress.Tests/
│   ├── NexusRouter.Router.Tests/
│   ├── NexusRouter.Egress.Tests/
│   └── NexusRouter.Benchmarks/           # Benchmarks de hot path com BenchmarkDotNet
│
├── docker/
│   └── postgres/
│       └── init.sql                      # CREATE EXTENSION timescaledb + hypertable
├── docker-compose.yml                    # Kafka KRaft, RabbitMQ, TimescaleDB
├── .env.example
└── NexusRouter.slnx
```

---

## 8. Fluxo de Dados — Sequência End-to-End

```
[Exchange TCP Socket]
        │
        │ bytes brutos (segmento TCP)
        ▼
[FixSession — PipeWriter.WriteAsync]
        │
        │ segmento do pipe (slice zero-copy)
        ▼
[ISessionFrameDecoder.TryDecode]
        │ retorna IngressFrame (ref struct, sem alloc)
        ▼
[SessionFSM — verificação de mensagem administrativa]
        │ (Heartbeat/TestRequest tratados inline, retorna)
        │ (Mensagem de aplicação passa adiante)
        ▼
[RouterEngine.Route(IngressFrame)]
        │
        ├─► [FrozenDictionary<SessionId, RouteContext> lookup]  — O(1), sem lock
        │
        ├─► [DeliveryMode == PassThrough?]
        │       │ Sim → PassThroughRewriter.Rewrite(Span<byte>)  — in-place, sem alloc
        │       │ Não → FixTokenizer.Tokenize(...)               — stack-alloc, sem heap alloc
        │
        ├─► [Aluga buffer do ArrayPool, copia frame, encapsula em DispatchPayload do ObjectPool]
        │
        └─► [FanOutEngine — para cada dispatcher em RouteContext.Dispatchers]
                │
                ├─► dispatcher_A.TryEnqueue(payload)  — Channel TryWrite, não bloqueante
                ├─► dispatcher_B.TryEnqueue(payload)  — Channel TryWrite, não bloqueante
                └─► dispatcher_N.TryEnqueue(payload)  — Channel TryWrite, não bloqueante
                        │
                        └─► [AuditChannel.TryWrite(auditRecord)]  — não bloqueante

--- FRONTEIRA DO HOT PATH — controle retorna ao loop do PipeReader ---

[Dispatcher Consumer Task — thread do thread-pool em background]
        │
        ├─► KafkaMessageDispatcher: IProducer.ProduceAsync(topic, message)
        ├─► RabbitMQMessageDispatcher: IModel.BasicPublish(exchange, routingKey, body)
        └─► FixOutboundDispatcher: PipeWriter.WriteAsync(frame reescrito)

[AuditWriterService Consumer Task — thread do thread-pool em background]
        ├─► SerilogAuditSink (metadados, lote async)
        └─► PostgresAuditSink.WriteBatchAsync — Npgsql COPY na hypertable
```

---

## 9. Análise do Orçamento de Latência

Contribuições estimadas dos componentes do hot path para a latência end-to-end (alvo p99: 50 µs):

| Componente | Contribuição Estimada | Observações |
|------------|----------------------|-------------|
| Recebimento no socket (`PipeWriter.WriteAsync`) | 2–8 µs | Cópia do kernel para userspace; irredutível |
| `TryDecode` (varredura de limite de frame) | 0,1–0,5 µs | Varredura sequencial em `Span<byte>` contíguo |
| Lookup no `FrozenDictionary` | < 0,1 µs | Hash probe em dicionário imutável |
| `PassThroughRewriter` (reescrita das tags 34 + 10) | 0,2–1 µs | Duas varreduras sequenciais em `Span<byte>` |
| `FixTokenizer` (parse completo, 20 campos) | 1–5 µs | Stack-allocated, sem heap |
| `ArrayPool.Rent` + cópia de buffer | 0,5–2 µs | Memcpy de mensagem FIX típica (~200 bytes) |
| `ObjectPool.Get` (DispatchPayload) | < 0,1 µs | Pool pré-aquecido na inicialização |
| `Channel.TryWrite` × N dispatchers | 0,2 µs cada | Push em fila lock-free |
| `AuditChannel.TryWrite` | 0,1 µs | Não bloqueante |
| **Total (PassThrough, 3 dispatchers)** | **~5–15 µs** | Bem dentro do orçamento p99 |
| **Pressão de GC** | 0 µs (alvo) | Zero alloc em regime estacionário |

---

## 10. Principais Decisões de Design e Justificativas

| Decisão | Alternativa Considerada | Justificativa |
|---------|------------------------|---------------|
| `System.IO.Pipelines` para I/O de socket | `Socket.Receive` bruto com `byte[]` | Pipelines eliminam cópia dupla; back-pressure integrado; slicing zero-copy em `ReadOnlySequence<byte>` |
| `FrozenDictionary` para tabela de roteamento | `ConcurrentDictionary` | FrozenDictionary tem menor overhead de leitura (sem CAS no hit-path); seguro pois a tabela de roteamento é imutável em runtime |
| `ref struct` para `IngressFrame` / `FixMessage` | Objetos de mensagem baseados em classe | Garante alocação em stack; previne captura acidental em boundaries async; reforça disciplina zero-allocation em tempo de compilação |
| `TryWrite` (não bloqueante) para fan-out | `await WriteAsync(...)` | Mantém o hot path síncrono e livre de alocações; back-pressure é absorvida pelos channels dos dispatchers |
| Thread dedicada de longa duração por sessão | Tasks do thread-pool | Elimina latência de scheduling do thread-pool para o hot path; afinidade de CPU previsível (future CPU pinning possível) |
| Contagem de referências do buffer (fan-out) | Cópia de buffer por dispatcher | Evita cópias O(N) no fan-out; ciclo único de rent/return independente da contagem de dispatchers |
| `ObjectPool<DispatchPayload>` | `new DispatchPayload()` por mensagem | Elimina alocação em regime estacionário; `DispatchPayload` é uma classe (necessária para compartilhamento entre channels) mas poolizada |
| `Confluent.Kafka` + options de segurança | Cliente AWS-only ou QuickFIX-style engine | Um producer cobre Docker, Kafka auto-gerenciado e Amazon MSK; IAM é opt-in |
| TimescaleDB (extensão PostgreSQL) para FIX persistido | Só Serilog arquivo, ou PG heap sem particionar | Store consultável no cold path; chunks, compressão e retenção batem com ingestão temporal; `Npgsql` único cliente |
| Kafka PLAINTEXT no Compose + dual listener | Um listener `kafka:9092` | App no host usa `localhost:19092`; outros containers usam `kafka:9092`; evita advertised.listeners quebrado |
