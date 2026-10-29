# NexusRouter

**Motor de Ingestão, Roteamento e Distribuição de Alta Performance para Streams de Execução de Ordens / DropCopy**

> Plataforma: .NET 10 (C#) · Protocolo: FIX 4.4 (B3 EntryPoint v2.40) · Alvo: Linux x86-64

---

## Visão Geral

O NexusRouter é um broker de mensagens de baixa latência construído especificamente para conectividade com o mercado financeiro brasileiro via gateway FIX da B3 EntryPoint. Ele ingere streams FIX 4.4 de execução de ordens e DropCopy da B3, roteia mensagens por um hot path zero-allocation e as distribui de forma concorrente para consumidores downstream — clientes FIX, tópicos Kafka e exchanges RabbitMQ — com alvo de latência p99 no hot path de ≤ 50 µs.

### Capacidades Principais

| Capacidade | Descrição |
|-----------|-----------|
| **Ingestão Multi-Sessão** | Sessões FIX concorrentes e independentes com reconexão automática (exponential backoff) |
| **Hot Path de Ultra-Baixa Latência** | Zero I/O síncrono, zero alocação heap em regime estacionário, `System.IO.Pipelines` para leituras de socket |
| **Modo Raw Pass-Through** | Encaminha buffers FIX brutos para clientes FIX downstream, reescrevendo apenas `MsgSeqNum` e `CheckSum` in-place |
| **Modo Parse** | Tokenização completa de campos FIX em um `FixMessage` stack-allocated para roteamento semântico |
| **Egress Polimórfico** | Abstração `IMessageDispatcher` — fan-out concorrente para Kafka, RabbitMQ e sessões FIX outbound |
| **Log de Auditoria Assíncrono** | Cold path baseado em `System.Threading.Channels` — auditoria completa de mensagens e ciclo de vida sem bloqueio do hot path |
| **Conformidade com Protocolo B3** | FIX 4.4 conforme spec B3 EntryPoint v2.40 — Cancel-on-Disconnect, numeração dupla de sequência, recuperação de nível de aplicação |

---

## Estrutura do Repositório

```
nexusrouter/
├── AGENTS.md                         — Instruções para Kiro e Cursor (aponta para .kiro/)
├── .kiro/
│   ├── specs/
│   │   └── nexusrouter/
│   │       ├── requirements.md       — Requisitos Funcionais e Não Funcionais
│   │       ├── design.md             — Documento de Design de Arquitetura
│   │       └── tasks.md              — Plano de tarefas incremental (T-01 → T-43)
│   └── steering/
│       ├── coding-standards.md       — Convenções .NET 10 C#, regras do hot path
│       ├── b3-guidelines.md          — Regras de protocolo FIX 4.4 do B3 EntryPoint
│       └── git-workflow.md           — Branch, commit, push e PR
├── docs/
│   ├── EntryPointMessageSpecs.pdf    (referência; pode estar ausente localmente)
│   └── FIX44Entrypoint.xml           (existente)
├── docker-compose.yml                — Kafka, RabbitMQ, TimescaleDB (local)
├── docker/postgres/init.sql          — hypertable de mensagens FIX
├── .env.example
├── README.md
└── (src/, tests/, NexusRouter.slnx — criados na Fase 1, Tarefa T-01)
```

---

## Arquitetura Resumida

```
[Gateway FIX B3 EntryPoint]
         │ TCP / TLS
         ▼
  ┌─────────────────┐
  │  FixSession(s)  │  ← System.IO.Pipelines, ISessionFrameDecoder, FixSessionStateMachine
  └────────┬────────┘
           │ IngressFrame (ref struct, zero alloc)
           ▼
  ┌─────────────────┐
  │  RouterEngine   │  ← Lookup em FrozenDictionary, PassThroughRewriter / FixTokenizer
  └────────┬────────┘
           │ DispatchPayload (poolizado, buffer com ref-count)
           ▼
  ┌─────────────────────────────────────────────────┐
  │  FanOutEngine                                   │
  │  ├── KafkaMessageDispatcher (Channel<T>)        │
  │  ├── RabbitMQMessageDispatcher (Channel<T>)     │
  │  └── FixOutboundDispatcher (Channel<T>)         │
  └─────────────────────────────────────────────────┘
           │ (apenas TryWrite não bloqueante)
           ▼
  ┌─────────────────┐
  │  AuditWriter    │  ← Channel<AuditRecord>, sink Serilog, cold path
  └─────────────────┘
```

**Regra do hot path:** Sem `await`, sem `lock`, sem alocação heap entre o recebimento no socket e o `TryEnqueue` do dispatcher. Todas as primitivas de sincronização usadas são lock-free (`Interlocked`, `Channel.TryWrite`, `FrozenDictionary`).

---

## Primeiros Passos

### Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://docs.docker.com/get-docker/) + Docker Compose v2 (stack local)
- Acesso ao B3 EntryPoint (credenciais de certificação ou produção), quando for conectar na bolsa

### Build

```bash
dotnet build NexusRouter.slnx
```

### Executar Testes

```bash
dotnet test NexusRouter.slnx
```

### Executar Benchmarks

```bash
cd tests/NexusRouter.Benchmarks
dotnet run -c Release
```

### Stack local (Docker Compose)

Sobe Kafka (KRaft, PLAINTEXT), RabbitMQ e PostgreSQL com TimescaleDB — o mesmo cliente Kafka (`Confluent.Kafka`) usado contra Amazon MSK, só mudando options.

```bash
cp .env.example .env
docker compose up -d
docker compose ps
```

| Serviço | Na rede Compose | No host (`dotnet run`) |
|---------|-----------------|------------------------|
| Kafka | `kafka:9092` | `localhost:19092` |
| RabbitMQ AMQP | `rabbitmq:5672` | `localhost:5672` |
| RabbitMQ UI | — | http://localhost:15672 (`nexus` / `.env`) |
| TimescaleDB | `timescaledb:5432` | `localhost:5432` |
| Kafka UI (opcional) | — | `docker compose --profile tools up -d` → http://localhost:8080 |

Amazon MSK: não use este Compose. Aponte `BootstrapServers` para o bootstrap MSK (`:9096` SCRAM ou `:9098` IAM), `SecurityProtocol: SaslSsl` e as env vars `NEXUS_KAFKA_SASL_*`. Ver `design.md` Seção 6.

### Configuração

Copie `src/NexusRouter.Host/appsettings.json` e configure os parâmetros da sua sessão. **Não armazene credenciais em `appsettings.json`** — use variáveis de ambiente:

```bash
export NEXUS_FIX_PASSWORD="sua-senha-b3"
export NEXUS_CERT_PWD="sua-senha-de-certificado"
export NEXUS_PG_USER="nexus"
export NEXUS_PG_PASSWORD="nexus"
```

Mensagens FIX recebidas são persistidas de forma assíncrona na hypertable `ingress_fix_messages` (cold path). O hot path não fala com o banco.

Consulte `docs/EntryPointMessageSpecs.pdf` e `.kiro/steering/b3-guidelines.md` para detalhes completos de configuração do protocolo B3.

---

## Spec-Driven Development

Este projeto segue o **Spec-Driven Development (SDD)**. A fonte da verdade é `.kiro/`; o Cursor e o Kiro devem ler os mesmos arquivos (ver `AGENTS.md`).

Todas as tarefas de implementação estão em `.kiro/specs/nexusrouter/tasks.md` (T-01 → T-43 em 8 fases). O comportamento do agente é governado por:

- `.kiro/steering/coding-standards.md` — Convenções C#, regras do hot path, padrões de teste
- `.kiro/steering/b3-guidelines.md` — Regras do protocolo B3 EntryPoint, catálogo de mensagens, restrições de campos
- `.kiro/steering/git-workflow.md` — Branch, commits confirmados pelo usuário, push e PR

No Kiro, abra a spec `nexusrouter` e execute a próxima tarefa no task runner. No Cursor, execute a próxima tarefa `- [ ]` em `tasks.md` e marque `- [x]` ao concluir.

---

## Metas de Performance

| Métrica | Alvo |
|---------|------|
| Latência do hot path p50 | ≤ 10 µs |
| Latência do hot path p99 | ≤ 50 µs |
| Latência do hot path p999 | ≤ 200 µs |
| Throughput agregado | ≥ 200.000 msg/s (4 cores) |
| Alocação heap em regime estacionário | 0 bytes/mensagem |
| Frequência de GC Gen0 | ≤ 1/minuto |
| Frequência de GC Gen2 | 0 (nunca durante operação normal) |

---

## Licença

Software proprietário interno. Todos os direitos reservados.
