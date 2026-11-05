# Review — Secrets via IConfiguration (Opção B1)

Commit revisado: `49cf0f0` (range `5f77279..HEAD`) na worktree `secrets-iconfiguration`.

## Veredito: APPROVED

## Evidência de verificação (lida das mensagens de commit, não reexecutada)
- `dotnet build NexusRouter.slnx` => compilação com êxito, 0 avisos, 0 erros.
- `dotnet test` por projeto: Domain=9, Router=18, Audit=12, Egress=19, Host=22, Ingress=40 OK.
- 1 teste `FixSession_WhenPeerStopsResponding` falha por timing tanto na worktree quanto no checkout base intocado => falha pré-existente, não relacionada (TLS off, sem leitura de segredo). Fora de escopo.

## Verificação dos invariantes B1

1. **Indireção `...EnvVar` preservada.** `git diff --stat -- "src/**/*Options.cs"` retornou vazio: nenhuma classe de options foi alterada. Os campos `UsernameEnvVar`/`PasswordEnvVar` (RabbitMQ, Postgres), `SaslUsernameEnvVar`/`SaslPasswordEnvVar` (Kafka) e `CertificatePasswordEnvVar`/`TlsCertificatePasswordEnvVar` (TLS) continuam sendo a chave de lookup; os validadores (`EnsureValid`) não mudaram. A senha não é materializada nas Options nem logada — resolvida sob demanda em `Open()`/`StartAsync`/`BuildConfig`/`LoadCertificate`.

2. **Três pontos de leitura passaram a resolver via `IConfiguration`.**
   - `EnvironmentValue.cs` (usado por RabbitMQ e Kafka dispatchers) foi removido; ambos recebem `ISecretResolver` e chamam `ResolveRequired(Options.*EnvVar)`.
   - `PostgresAuditSink` removeu o `ReadRequired` próprio e usa `_secretResolver.ResolveRequired(...)` em `StartAsync`.
   - `TlsSocketFactory.WrapAsync`/`WrapServerAsync` recebem `ISecretResolver`; `FixSession` e `FixOutboundDispatcher` propagam o resolvedor. O DI é respeitado: `AddSecretResolver` registra `ISecretResolver` com `TryAddSingleton` sobre a `IConfiguration` da app, e as factories em `ServiceCollectionExtensions` injetam `sp.GetRequiredService<ISecretResolver>()`.

3. **Compatibilidade preservada + teste.** `ConfigurationSecretResolver` lê `configuration[key]`; o provider de env vars do .NET (carregado por `WebApplication.CreateBuilder`) expõe a variável do processo pelo nome plano, logo `RABBITMQ_DEFAULT_PASS` resolve tanto por env var do processo quanto por User Secrets/appsettings/AWS. O teste `ConfigurationSecretResolver_ResolvesKeyDefinedAsProcessEnvironmentVariable` define a chave como env var do processo, monta `IConfiguration` com `AddEnvironmentVariables()` e prova a resolução.

4. **launchSettings.json sem senhas.** O arquivo versionado mantém apenas `ASPNETCORE_ENVIRONMENT=Development`; `RABBITMQ_DEFAULT_USER/PASS` e `NEXUS_PG_USER/PASSWORD` foram removidos (confirmado contra a versão base e por grep).

5. **docs/secrets.md cobre os três cenários.** User Secrets (dev local, com aviso explícito de "nunca para produção"), AWS Secrets Manager (prod/staging, via provider de config ou env var injetada) e GitHub Actions Secrets (CI). Usa os nomes de chave reais e documenta a convenção `:` ↔ `__`.

6. **Padrões de código / idioma.** `sealed`, XML docs em tipos públicos, `ArgumentNullException.ThrowIfNull`, mensagens e comentários em português. O novo pacote `Microsoft.Extensions.Configuration.Abstractions` está fixado em versão exata (CPM). Os testes usam `Assert` bruto, mas isso espelha a convenção já existente no arquivo base (`PostgresAuditSinkTests` original) — não é regressão.

## Observações (não bloqueantes)
- Os testes TLS-off usam um `ThrowingSecretResolver` local, garantindo que o resolvedor não é tocado quando não deveria. Boa prática fail-closed.

## Findings
Nenhum blocker.
