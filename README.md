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
│   │       └── tasks.md              — Plano de tarefas incremental (T-01 → T-53)
│   └── steering/
│       ├── coding-standards.md       — Convenções .NET 10 C#, regras do hot path
│       ├── b3-guidelines.md          — Regras de protocolo FIX 4.4 do B3 EntryPoint
│       └── git-workflow.md           — Trunk-based: branch curta, commit, push e PR para main
├── docs/
│   ├── EntryPointMessageSpecs.pdf    (referência; pode estar ausente localmente)
│   └── FIX44Entrypoint.xml           (existente)
├── docker-compose.yml                — Kafka, RabbitMQ, TimescaleDB (local)
├── docker/postgres/init.sql          — hypertable de mensagens FIX
├── .github/workflows/
│   ├── build-and-test.yml            — CI em pull request e push para main
│   ├── deploy-to-development.yml     — deploy manual (workflow_dispatch)
│   ├── deploy-to-staging.yml         — deploy em tag vX.Y.Z-rc.N
│   └── deploy-to-production.yml      — deploy em tag vX.Y.Z
├── .env.example
├── Makefile                          — atalhos de build/test
├── README.md
├── NexusRouter.slnx                  — solução principal
├── src/                              — Audit, Domain, Egress, Host, Ingress, Router, SessionLog
├── tests/                            — testes unitários, integração e benchmarks
└── utils/
    ├── sim/                          — simulador do gateway B3 (ver seção abaixo)
    └── client/                       — ClientProbe: cliente FIX/RabbitMQ de apoio, com painel web
```

---

## DropCopy Simulator

Para testar o NexusRouter sem depender da B3, o repositório inclui um simulador do gateway FIX 4.4
(B3 EntryPoint) em [`utils/sim/`](utils/sim/README.md) — um acceptor FIX com API de controle e um painel web
(SimPanel) para operar a simulação, injetar falhas e acompanhar o fluxo em tempo real.

O simulador é **completamente isolado** do NexusRouter: tem a própria solução, CPM e `Directory.Build.props`,
e remover `utils/sim/` não altera a build principal nem a CI. Instruções de como rodar, configurar e testar
backend e frontend estão em [`utils/sim/README.md`](utils/sim/README.md).

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

### Habilitando dispatchers (egress)

Os destinos de saída são configurados em `src/NexusRouter.Host/appsettings.json`, em duas listas dentro de `NexusRouter` que se cruzam pelo `Id`:

- **`Dispatchers`** — declara cada destino. O campo `Type` seleciona o tipo e os campos válidos:

  | `Type`        | Destino               | Status                  |
  |---------------|-----------------------|-------------------------|
  | `Kafka`       | Producer Kafka        | disponível              |
  | `RabbitMQ`    | Publisher RabbitMQ    | disponível              |
  | `FixOutbound` | Acceptor FIX outbound | disponível              |
  | `WebSocket`   | Acceptor WebSocket    | disponível              |

- **`Sessions[].Routes`** — cada sessão de ingresso lista, por `DispatcherId`, para quais dispatchers suas mensagens fazem fan-out.

**Um dispatcher só recebe tráfego quando alguma `Routes` (ou `Clients`) referencia o seu `Id`.** Declarar em `Dispatchers` sem rotear sobe o dispatcher, mas ele fica ocioso.

Para habilitar **um** dispatcher numa sessão, aponte uma rota para o `Id` dele. Para habilitar **vários** (fan-out concorrente), liste todos:

```json
"Sessions": [
  {
    "SessionId": "B3-ENTRYPOINT-01",
    "Routes": [
      { "DispatcherId": "kafka-fix-raw" },
      { "DispatcherId": "rabbitmq-fix-raw" }
    ]
  }
],
"Dispatchers": [
  { "Id": "kafka-fix-raw", "Type": "Kafka", "BootstrapServers": "127.0.0.1:19092", "Topic": "fix.ordexec.raw", "PayloadEncoding": "Raw" },
  { "Id": "rabbitmq-fix-raw", "Type": "RabbitMQ", "Host": "127.0.0.1", "Exchange": "fix.orders", "RoutingKey": "fix.raw", "PayloadEncoding": "Json" }
]
```

**Formato do corpo (Kafka e RabbitMQ).** `PayloadEncoding` define o que é publicado: `Json` (default) ou `Raw`. `Json` publica o envelope `{"type":"exec","msgType":"8","ts":<ticks UTC>,"fields":{"11":"...",...}}`, com valores como string ASCII do FIX, na ordem do wire, sem as tags 8, 9, 10 e 35 em `fields`; tag repetida (grupo) vira array. `Raw` publica o frame FIX bruto. No RabbitMQ, `Json` marca `content_type=application/json`. Quem consome o frame bruto precisa declarar `"PayloadEncoding": "Raw"`. O log de sessão e o cursor seguem sobre o frame bruto nos dois casos.

A configuração é validada na subida (fail-closed): é obrigatório ao menos uma sessão e um dispatcher, `Id` de dispatcher não pode repetir, um `DispatcherId` não pode repetir na mesma sessão, e toda rota deve apontar para um `Id` existente. Credenciais (Kafka SASL, RabbitMQ) vêm por variáveis de ambiente, nunca no `appsettings.json`.

Consulte `docs/EntryPointMessageSpecs.pdf` e `.kiro/steering/b3-guidelines.md` para detalhes completos de configuração do protocolo B3.

### Tags FIX customizadas do NexusRouter

O NexusRouter reserva dois blocos de tags user-defined. Toda tag nova do projeto sai deles e é registrada na tabela abaixo.

| Bloco         | Uso                                                                 | Base FIX                                   |
|---------------|---------------------------------------------------------------------|--------------------------------------------|
| `19000–19099` | Clientes internos da MonteBravo (a tag não sai da firma)            | 10000–19999, uso interno de uma firma      |
| `21000–21099` | Clientes parceiros (contraparte externa, acordo bilateral)          | 20000–39999, user-defined bilateral        |

Os blocos ficam longe das faixas que a B3 já ocupa no `docs/FIX44Entrypoint.xml`: `5000–9999`, `100xx` e `35001–35551`. Os clientes recebem os frames da B3 em pass-through e carregam as tags da B3 e as nossas no mesmo dicionário, então um número em comum quebraria o parse. Não use tags fora destes blocos. Antes de alocar um número novo, confira que ele não existe no dicionário da B3.

| Tag     | Nome         | Mensagem         | Bloco   | Status                 |
|---------|--------------|------------------|---------|------------------------|
| `19001` | `ReplayMode` | Logon (`35=A`)   | interno | disponível |

#### `ReplayMode` (19001): o que o cliente recebe ao conectar

Um cliente FIX que conecta ao acceptor (`FixOutbound`) durante o dia escolhe o que recebe:

| Valor | Modo         | Efeito                                                                 |
|-------|--------------|------------------------------------------------------------------------|
| `1`   | `FromLogon`  | Só as mensagens que chegarem depois do Logon. O que passou é ignorado. |
| `2`   | `StartOfDay` | Todas as mensagens do dia, desde a meia-noite, a partir do seq 1, seguidas do tráfego ao vivo. |

Regras:

- O dia vira à meia-noite (fuso `America/Sao_Paulo` por padrão), como no QuickFIX: o router abre um storage novo e o seq dos clientes volta a 1.
- O modo vale quando o Logon abre uma época nova do cliente: com `ResetSeqNumFlag` (`141=Y`) ou no primeiro Logon do cliente no dia. `141=Y` sempre zera o seq dos dois lados; o modo só decide se a época começa com o histórico do dia ou vazia.
- Reconexão sem `141=Y` continua de onde parou pela recuperação normal (`789` ou `ResendRequest`); uma tag válida `19001` é ignorada nesse caso. Valor fora da tabela sempre recebe `Logout` com `Text`.
- Sem a tag, vale o `Recovery.ReplayMode` configurado no dispatcher ou no `ClientOverrides` do `SenderCompID` (default `FromLogon`). Com a tag, ela prevalece quando uma época nova é aberta.

Exemplo de Logon pedindo o dia inteiro:

```text
8=FIX.4.4|9=...|35=A|34=1|49=OMS1|56=NEXUSROUTER|52=...|98=0|108=30|141=Y|19001=2|10=...
```

**QuickFIX/n (C#):** adicione a tag no `ToAdmin` e declare o campo no dicionário do cliente.

```csharp
public void ToAdmin(Message message, SessionID sessionId)
{
    if (message.Header.GetString(Tags.MsgType) == MsgType.LOGON)
    {
        message.SetField(new ResetSeqNumFlag(true));
        message.SetField(new IntField(19001, 2)); // 1 = FromLogon, 2 = StartOfDay
    }
}
```

```xml
<!-- No dicionário do cliente: campo em <fields> e referência na mensagem Logon -->
<field number="19001" name="ReplayMode" type="INT" />
<message name="Logon" msgtype="A" msgcat="admin">
  <!-- campos existentes -->
  <field name="ReplayMode" required="N" />
</message>
```

**QuickFIX/J:** mesmo padrão em `toAdmin`, com `message.setInt(19001, 2)`.

**ClientProbe:** o perfil FIX tem um seletor do modo. Use-o junto com "zerar sequência no próximo logon".

---

## Git e ambientes

O repositório usa **trunk-based development**. `main` é a única branch de longa duração e deve permanecer sempre integrável. Não há `develop` nem branch `release/*`.

O dia a dia é uma branch curta a partir de `main` (`feature/*`, `bugfix/*`, `chore/*`), pull request para `main` e merge. O workflow `Build and test` roda no pull request e de novo no push para `main`.

Cada ambiente tem o seu workflow e o seu trigger. Crie os GitHub Environments `development`, `staging` e `production` em **Settings → Environments** do repositório para guardar segredos e, em production, exigir revisores.

| Ambiente | Workflow | Trigger |
|----------|----------|---------|
| CI | `build-and-test.yml` | pull request e push em `main` |
| Development | `deploy-to-development.yml` | manual (`workflow_dispatch`) |
| Staging | `deploy-to-staging.yml` | tag `vX.Y.Z-rc.N` |
| Production | `deploy-to-production.yml` | tag `vX.Y.Z` |

O filtro de production é `v[0-9]+.[0-9]+.[0-9]+`. Uma tag `v1.0.0-rc.1` não entra nesse workflow. O filtro `v*` pegaria as duas e publicaria o release candidate em production.

### Development

O merge em `main` só roda `Build and test`. O deploy acontece quando você dispara o workflow na mão, no commit que escolher (em geral `main`):

**Actions → Deploy to Development → Run workflow**

Sem esse disparo, nada é publicado em development. Staging e production continuam independentes: sem a tag, o ambiente correspondente também não recebe deploy.

### Staging

A tag de release candidate sai de `main` atualizada. O `N` aumenta a cada candidato (`rc.1`, `rc.2`).

```bash
git checkout main
git pull origin main
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

### Production

A tag estável aponta para o mesmo commit já validado em staging. Não reutilize um nome de tag: o `push` da tag é o que dispara o workflow.

```bash
git checkout main
git pull origin main
git tag v1.0.0
git push origin v1.0.0
```

---

## Spec-Driven Development

Este projeto segue o **Spec-Driven Development (SDD)**. A fonte da verdade é `.kiro/`; o Cursor e o Kiro devem ler os mesmos arquivos (ver `AGENTS.md`).

Todas as tarefas de implementação estão em `.kiro/specs/nexusrouter/tasks.md` (T-01 → T-53). O comportamento do agente é governado por:

- `.kiro/steering/coding-standards.md` — Convenções C#, regras do hot path, padrões de teste
- `.kiro/steering/b3-guidelines.md` — Regras do protocolo B3 EntryPoint, catálogo de mensagens, restrições de campos
- `.kiro/steering/git-workflow.md` — Trunk-based: branch curta a partir de `main`, commits confirmados pelo usuário, push e PR para `main`

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
