---
inclusion: auto
name: coding-standards
description: Coding conventions, style rules, and engineering standards for NexusRouter (.NET 10 C#). Activate when writing, reviewing, or refactoring any C# code in the project.
---

# NexusRouter — Padrões de Código e Convenções de Engenharia

> Estas regras são **obrigatórias** para todo o código produzido ou revisado neste workspace.  
> Em caso de conflito com preferências pessoais, estas regras têm precedência.

---

## 1. Linguagem e Runtime

| Configuração | Valor |
|-------------|-------|
| Target Framework | `net10.0` |
| Versão da Linguagem | `latest` (C# 14) |
| Nullable Reference Types | `enable` (imposto — sem `#nullable disable` em código do hot path) |
| Implicit Usings | `enable` |
| Treat Warnings As Errors | `true` em todos os projetos |
| Platform target | `AnyCPU` (alvo primário: Linux x86-64) |

---

## 2. Convenções de Nomenclatura de Projetos

| Papel do Projeto | Padrão de Nomenclatura | Exemplo |
|-----------------|----------------------|---------|
| Bibliotecas do núcleo | `NexusRouter.<Layer>` | `NexusRouter.Domain` |
| Entry point | `NexusRouter.Host` | — |
| Projetos de teste unitário | `NexusRouter.<Layer>.Tests` | `NexusRouter.Router.Tests` |
| Projeto de benchmark | `NexusRouter.Benchmarks` | — |
| Raiz do namespace | Corresponde ao nome do projeto | `namespace NexusRouter.Domain.Models` |

---

## 3. Nomenclatura de Arquivos e Símbolos

### 3.1 Geral
- **Um tipo público por arquivo.** Nome do arquivo = nome do tipo (ex.: `FixSession.cs` para `class FixSession`).
- **PascalCase** para todos os tipos, métodos, propriedades, eventos e constantes.
- **camelCase** com underscore inicial para campos privados: `_dispatcherChannel`, `_seqNum`.
- **camelCase** para variáveis locais e parâmetros de método.
- **ALL_CAPS** é proibido — usar `static readonly` ou `const` com PascalCase: `const int MaxRetries = 5`.

### 3.2 Interfaces
- Prefixar com `I`: `IMessageDispatcher`, `ISessionFrameDecoder`.
- Evitar `IFoo` em classes base abstratas — usar `FooBase`.

### 3.3 Métodos Async
- Todos os métodos `async` terminam em `Async`: `StartAsync`, `ConnectAsync`, `SendAsync`.
- Exceção: `IHostedService.StartAsync` / `StopAsync` — seguir a nomenclatura do framework.
- Preferir `ValueTask` / `ValueTask<T>` a `Task` / `Task<T>` para métodos async do hot path que frequentemente completam de forma síncrona.

### 3.4 Enumerações
- Nome do tipo enum é singular: `DeliveryMode`, `SessionState`.
- Membros do enum usam PascalCase: `PassThrough`, `Active`, `LogonSent`.

---

## 4. Regras de Código do Hot Path (CRÍTICO)

Estas regras se aplicam a qualquer código no **hot path** (recebimento no socket → router → handoff ao dispatcher). Violações serão rejeitadas em code review.

### 4.1 Zero Allocation
- **Sem `new` em código de hot path em regime estacionário.** Usar `ArrayPool<byte>`, `MemoryPool<byte>`, `ObjectPool<T>` ou `stackalloc`.
- **Sem LINQ no hot path.** LINQ aloca iteradores, closures e coleções intermediárias.
- **Sem concatenação ou interpolação de strings no hot path.** Usar `Span<char>` ou arrays de bytes pré-formatados.
- **Sem boxing.** Evitar passar value types como `object` ou `interface`. Usar constraints genéricos onde necessário.
- **Sem closures que capturam objetos heap** em delegates `Action<T>` / `Func<T>` no hot path.

### 4.2 Sincronização
- **Sem instrução `lock` no hot path.** Usar `Interlocked`, `System.Threading.Channels` ou tipos `System.Collections.Concurrent`.
- **Sem `await` em `RouterEngine.Route()` ou `FanOutEngine.Dispatch()`.** Esses devem ser síncronos (retornar `void`).
- **Sem `Thread.Sleep` ou `Task.Delay` em nenhum caminho não administrativo.**
- Preferir `Volatile.Read` / `Volatile.Write` ou `Interlocked` à palavra-chave `volatile` para documentação explícita do modelo de memória.

### 4.3 I/O
- **Sem I/O de arquivo síncrono (`File.Write`, `StreamWriter.Write`)** em qualquer lugar no hot path ou nos caminhos de envio dos dispatchers. Usar I/O async exclusivamente.
- **Sem chamadas a banco de dados no hot path.** A persistência de sequence number (SequenceNumberStore) é somente cold-path.
- Usar `System.IO.Pipelines` para todo o I/O de socket. Não usar `Stream.Read(byte[])` ou leituras brutas de `NetworkStream`.

### 4.4 Disciplina com `ref struct`
- Tipos `ref struct` (`IngressFrame`, `FixMessage`) **não devem** ser capturados em lambdas, armazenados em campos ou passados além de boundaries de `await` async.
- Sempre receber parâmetros `ref struct` como `in` (ref somente leitura) quando o chamado não os muta.

---

## 5. Convenções Gerais de C#

### 5.1 Null Safety
- Nunca usar `null!` (operador null-forgiving) a menos que seja para suprimir uma limitação conhecida do compilador (ex.: padrões de injeção de DI com `[NotNull]`). Adicionar comentário explicando o motivo.
- Preferir `ArgumentNullException.ThrowIfNull(param)` a verificações manuais de nulo.
- Usar `required` em propriedades obrigatórias em POCOs de options/config.

### 5.2 Pattern Matching
- Preferir expressões `switch` a cadeias de `if/else if` para despacho de valores (ex.: switch em `DeliveryMode`).
- Preferir pattern matching com `is` a `as` + verificação de nulo.

### 5.3 `record` e `struct`
- Usar `readonly record struct` para value objects imutáveis compartilhados entre threads (ex.: `RouteContext`, `SessionId`).
- Usar `sealed class` para implementações não projetadas para herança.
- Marcar todas as classes que não são explicitamente projetadas para herança como `sealed`.

### 5.4 Exceções
- Usar classes de exceção tipadas. Não usar `throw new Exception("message")` — criar um tipo de exceção de domínio.
- Não capturar e engolir exceções silenciosamente. No mínimo, registrá-las via `ILogger` e anotar no channel de auditoria.
- `catch (Exception ex)` é permitido apenas em consume loops de dispatchers e handlers de exceções não tratadas — nunca no hot path.

### 5.5 IDisposable / IAsyncDisposable
- Implementar `IDisposable` em todas as classes que possuem recursos não gerenciados ou buffers alugados.
- Usar guarda `Interlocked.Exchange(ref _disposed, 1)` para segurança contra double-dispose.
- Preferir `IAsyncDisposable` + `await using` para caminhos de limpeza async.

---

## 6. Documentação XML

- **Todos os tipos, métodos e propriedades `public` e `internal` devem ter comentários de documentação XML.**
- Tags mínimas obrigatórias: `<summary>`. Tags adicionais recomendadas: `<param>`, `<returns>`, `<exception>`, `<remarks>`.
- Em interfaces, documentar o **contrato** (o que os chamadores podem assumir), não o detalhe de implementação.
- Requisito especial para métodos do hot path: incluir tag `<remarks>` declarando explicitamente as garantias de alocação — ex.: `<remarks>Zero allocation. Safe to call on the hot path.</remarks>`.

---

## 7. Convenções de Testes

- O projeto de teste espelha o namespace do projeto fonte: `NexusRouter.Router.Tests` testa `NexusRouter.Router`.
- Nomenclatura de classe de teste: `{SistemaEmTeste}Tests` — ex.: `PassThroughRewriterTests`.
- Nomenclatura de método de teste: `{NomeDoMetodo}_{Cenario}_{ResultadoEsperado}` — ex.: `Rewrite_ValidFix44Message_RewritesSeqNumAndChecksum`.
- Usar `xUnit` como framework de testes. Usar `FluentAssertions` para assertions (nunca comparações brutas `Assert.Equal` para tipos complexos).
- Usar `NSubstitute` para mocking de interfaces. Não usar `Moq`.
- Não usar `[Theory]` com `InlineData` para mais de 5 casos — usar `MemberData` com propriedade estática para conjuntos de dados maiores.
- Arquivos de teste não devem conter código de produção ou utilitários auxiliares além do necessário para o teste.

---

## 8. Gerenciamento de Dependências

- Todas as versões de pacotes NuGet devem ser **fixadas** em uma versão exata no `.csproj` ou `Directory.Packages.props`. Sem ranges flutuantes (`*` ou `+`).
- Justificativa do versionamento fixado: builds determinísticos são um requisito rígido para um sistema de trading.
- Usar `Directory.Build.props` na raiz da solução para configurações cross-project.
- Usar `Directory.Packages.props` (Central Package Management) se a solução crescer além de 6 projetos.
- Lista de bibliotecas externas (aprovadas para uso em produção):
  - `Confluent.Kafka` — producer Kafka (Docker, Kafka auto-gerenciado, Amazon MSK via `ProducerConfig`)
  - `AWS.MSK.Auth` (ou equivalente fixado) — **somente** quando `SaslMechanism=AwsMskIam`; não é dependência default
  - `RabbitMQ.Client` — cliente AMQP RabbitMQ
  - `Npgsql` — cliente PostgreSQL / TimescaleDB (apenas cold path; sem SQL no hot path)
  - `Serilog` + sinks (`Serilog.Sinks.Console`, `Serilog.Sinks.File`) — logging estruturado
  - `Microsoft.Extensions.*` — hosting, DI, configuration, object pool
  - `System.Collections.Frozen` — coleções imutáveis (built-in, .NET 8+)
  - `OpenTelemetry.*` / `prometheus-net` — observabilidade
  - `BenchmarkDotNet` — apenas para benchmarks (projeto `tests/`)
  - `xunit`, `FluentAssertions`, `NSubstitute` — apenas para projetos de teste

---

## 9. Configuração e Secrets

- **Sem valores hardcoded** para endereços IP, portas, credenciais ou identificadores de bolsa em qualquer lugar no código-fonte.
- Credenciais **nunca** são armazenadas em `appsettings.json` ou qualquer arquivo versionado. São injetadas via:
  - Variáveis de ambiente (produção)
  - `.NET User Secrets` (`dotnet user-secrets`) para desenvolvimento local
- Chaves de config que referenciam secrets devem apontar para o **nome da variável de ambiente**, não para o valor.
- Usar options fortemente tipadas com `IOptions<T>` / `IOptionsMonitor<T>`. Sem `IConfiguration.GetValue<string>("path:to:key")` espalhado na lógica de negócio.

---

## 10. Convenções de Logging

- Usar `Microsoft.Extensions.Logging.ILogger<T>` injetado via DI. Nunca usar loggers estáticos ou `Console.WriteLine` em código de produção.
- Somente logging estruturado: usar templates de mensagem com parâmetros nomeados. Sem interpolação de strings em chamadas de log.
  - ✅ `_logger.LogInformation("Session {SessionId} connected to {Host}:{Port}", id, host, port)`
  - ❌ `_logger.LogInformation($"Session {id} connected to {host}:{port}")`
- Níveis de log:
  - `Trace` — eventos do hot path por mensagem (desabilitado em produção via config; nunca amostrado a 100%)
  - `Debug` — detalhes de ciclo de vida de nível de sessão
  - `Information` — transições de estado, status de dispatcher, startup/shutdown
  - `Warning` — anomalias recuperáveis (channel cheio, heartbeat perdido, aviso de decode)
  - `Error` — falhas de dispatcher, exceções inesperadas
  - `Critical` — erros irrecuperáveis que requerem reinicialização do processo
- Nunca registrar bytes brutos de mensagem FIX em nível `Information` ou acima. Usar `Debug` / `Trace` apenas, com limites de tamanho.

---

## 11. Convenções de Git e Branches

- Nomenclatura de branch: `feature/<id-da-tarefa>-<descricao-curta>` — ex.: `feature/T-13-pass-through-rewriter`
- Mensagens de commit: `[T-XX] Descrição imperativa curta` — ex.: `[T-13] Implement PassThroughRewriter with in-place checksum`
- Todo commit em `main` deve passar em `dotnet build` e `dotnet test`.
- Nunca commitar arquivos gerados (artefatos de build, `*.user`, `bin/`, `obj/`, `*.nupkg`).
