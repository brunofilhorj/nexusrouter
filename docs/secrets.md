# Gestão de Segredos — NexusRouter

O NexusRouter **nunca** guarda credenciais em arquivos versionados. A configuração versionada
(`appsettings.json`) só carrega a **chave de lookup** do segredo — o nome sob o qual o valor é
resolvido — e nunca o valor em si. Por exemplo:

```json
"UsernameEnvVar": "RABBITMQ_DEFAULT_USER",
"PasswordEnvVar": "RABBITMQ_DEFAULT_PASS"
```

Em tempo de execução, o valor é resolvido **sob demanda** via `IConfiguration` e repassado direto ao
cliente (RabbitMQ, Npgsql, Kafka, carregamento do certificado TLS). O valor resolvido não é gravado nas
classes de options nem registrado em log.

## Como a resolução funciona (Opção B1)

A resolução usa `IConfiguration` como fonte única, através da abstração
`NexusRouter.Domain.Secrets.ISecretResolver` (implementação `ConfigurationSecretResolver`). A chave de
lookup é lida direto pelo indexador (`configuration["RABBITMQ_DEFAULT_PASS"]`).

O ponto central de compatibilidade: o **provider de variáveis de ambiente** do .NET expõe cada variável
do processo sob o seu nome exato dentro de `IConfiguration`. Logo, uma chave como `RABBITMQ_DEFAULT_PASS`
é resolvível das duas formas, sem mudança de código:

- quando definida como **variável de ambiente do processo** (como no `docker-compose.yml` e no CI); e
- quando definida em **User Secrets**, **appsettings** ou **AWS Secrets Manager**.

### Convenção de chave e o tratamento `:` vs `__`

- Use o **nome plano** já adotado pelo projeto: `RABBITMQ_DEFAULT_USER`, `RABBITMQ_DEFAULT_PASS`,
  `NEXUS_PG_USER`, `NEXUS_PG_PASSWORD`, `NEXUS_KAFKA_SASL_USERNAME`, `NEXUS_KAFKA_SASL_PASSWORD` e a chave
  apontada por `CertificatePasswordEnvVar` da sessão/acceptor TLS. Esses nomes são a chave de lookup e
  continuam funcionando exatamente como antes.
- Para chaves **hierárquicas** do .NET (formato `Secao:Chave`), o separador em variável de ambiente é o
  **duplo underscore** `__`. O provider do .NET converte `Secao__Chave` (variável de ambiente) em
  `Secao:Chave` (`IConfiguration`). Essa é a regra a usar caso um dia uma chave de segredo passe a ser
  hierárquica. Hoje todas as chaves de segredo do NexusRouter são planas, então não há conversão: o nome
  da variável de ambiente é igual à chave de lookup.

### Chaves de segredo em uso

| Credencial | Chave de lookup (nome da variável) | Onde aparece |
|---|---|---|
| RabbitMQ usuário | `RABBITMQ_DEFAULT_USER` | dispatcher RabbitMQ |
| RabbitMQ senha | `RABBITMQ_DEFAULT_PASS` | dispatcher RabbitMQ |
| Postgres (audit sink) usuário | `NEXUS_PG_USER` | sink Postgres de auditoria |
| Postgres (audit sink) senha | `NEXUS_PG_PASSWORD` | sink Postgres de auditoria |
| Kafka SASL usuário | `NEXUS_KAFKA_SASL_USERNAME` | dispatcher Kafka (só com SASL) |
| Kafka SASL senha | `NEXUS_KAFKA_SASL_PASSWORD` | dispatcher Kafka (só com SASL) |
| Senha do certificado TLS | valor de `CertificatePasswordEnvVar` da sessão/acceptor | TLS do ingress e do acceptor FIX |
| Senha da sessão FIX (RawData 96) | valor de `PasswordEnvVar` da sessão | Logon do initiator contra a B3 |

> O `appsettings.json` versionado pode mudar o **nome** da chave (ex.: apontar `PasswordEnvVar` para
> outro nome), mas nunca deve conter o **valor** do segredo.

---

## 1. Desenvolvimento local — .NET User Secrets

User Secrets guarda os valores **fora** da árvore do repositório, no perfil do usuário, então eles não
são versionados nem aparecem em diff. O projeto `NexusRouter.Host` já tem `<UserSecretsId>` habilitado,
e o host (`WebApplication.CreateBuilder`) carrega User Secrets automaticamente quando o ambiente é
`Development` (o `launchSettings.json` define `ASPNETCORE_ENVIRONMENT=Development`).

Inicialize uma vez (o Id já existe no `.csproj`, então `init` é idempotente):

```bash
cd src/NexusRouter.Host
dotnet user-secrets init
```

Defina os segredos usando a **mesma chave de lookup** (o nome plano):

```bash
cd src/NexusRouter.Host
dotnet user-secrets set "RABBITMQ_DEFAULT_USER" "nexus"
dotnet user-secrets set "RABBITMQ_DEFAULT_PASS" "nexus"
dotnet user-secrets set "NEXUS_PG_USER" "nexus"
dotnet user-secrets set "NEXUS_PG_PASSWORD" "nexus"
# Só se o dispatcher Kafka usar SASL:
# dotnet user-secrets set "NEXUS_KAFKA_SASL_USERNAME" "..."
# dotnet user-secrets set "NEXUS_KAFKA_SASL_PASSWORD" "..."
```

Onde fica o arquivo (`secrets.json`), por sistema operacional:

- Windows: `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`
- Linux / macOS: `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`

O `<UserSecretsId>` está no `src/NexusRouter.Host/NexusRouter.Host.csproj`.

### O `launchSettings.json` não contém mais senhas

O `launchSettings.json` versionado **não** carrega mais `RABBITMQ_DEFAULT_PASS`, `NEXUS_PG_PASSWORD`
nem os usuários correspondentes. Ele define apenas `ASPNETCORE_ENVIRONMENT=Development`. As credenciais
de dev vêm de User Secrets (recomendado) ou de variáveis de ambiente do processo.

### Alternativa: variável de ambiente do processo (docker-compose)

Quem usa o `docker-compose.yml` local pode continuar exportando as variáveis (ou usando o `.env` a partir
do `.env.example`). Como a resolução passa por `IConfiguration`, o nome da variável de ambiente é a
própria chave de lookup, então nada muda do ponto de vista do app.

### `.env` (infra) vs. User Secrets (app) — não confundir

São **duas pontas distintas** da mesma conexão, e o `.env` **continua necessário** mesmo depois da
Opção B1:

- O **`.env`** (gitignored; template em `.env.example`) alimenta o **`docker-compose.yml`**. É o lado do
  **servidor**: define quais credenciais o RabbitMQ e o Postgres **criam** ao subir os containers
  (`RABBITMQ_DEFAULT_USER/PASS`, `POSTGRES_USER/PASSWORD/DB`). Essas chaves `POSTGRES_*` são consumidas
  só pelos containers e não têm relação com a leitura da aplicação.
- O **User Secrets** é o lado do **cliente**: é de onde a aplicação .NET lê as credenciais para **se
  conectar** a esses mesmos serviços.

Os valores das duas pontas precisam **coincidir** para a app conectar (por padrão, `nexus`/`nexus` dos
dois lados). O `.env` nunca é versionado; apenas o `.env.example`, com placeholders de dev, vai para o
repositório.

| Arquivo | Ponta | Para quê | Versionado? |
|---|---|---|---|
| `.env` | infra (servidor) | credenciais que o docker-compose cria nos containers | não (gitignored) |
| `.env.example` | infra (template) | modelo de onboarding, com placeholders | sim |
| User Secrets | app (cliente) | credenciais que a aplicação usa para conectar, em dev | não (fora do repo) |

---

## 2. Produção e staging — AWS Secrets Manager

Em produção e staging, os segredos vêm do **AWS Secrets Manager** (baseline de segurança da organização:
segredos sensíveis residem no AWS Secrets Manager ou HSM, nunca em texto claro no repositório).
**User Secrets é só para desenvolvimento local — nunca para produção.**

Há duas formas de os valores entrarem no `IConfiguration`, ambas compatíveis com a Opção B1:

### a) Provider de configuração do AWS Secrets Manager

Adicione um provider de configuração do AWS Secrets Manager ao builder (biblioteca
`Kralizek.Extensions.Configuration.AWSSecretsManager` ou equivalente aprovado e com versão fixada). O
provider carrega os segredos do Secrets Manager como entradas de `IConfiguration`, e o
`ConfigurationSecretResolver` os resolve pela mesma chave de lookup, sem mudança de código.

- **Nome plano** (ex.: um segredo chamado `RABBITMQ_DEFAULT_PASS`) resolve direto pela chave.
- **Nome hierárquico**: o AWS Secrets Manager costuma usar `/` como separador no nome do segredo. Ao
  configurar o provider, mapeie o separador do segredo para o separador do .NET (`:`). Em variáveis de
  ambiente a mesma hierarquia usa `__` (que o .NET converte para `:`). Mantenha o mapeamento alinhado com
  as chaves de lookup planas que o NexusRouter usa hoje.

### b) Injeção como variável de ambiente

Se a plataforma (ECS/EKS/Lambda) injetar o segredo do Secrets Manager como **variável de ambiente** do
processo (ex.: `secrets` numa task definition do ECS), nada mais é preciso: o provider de variáveis de
ambiente do .NET já expõe o valor sob o nome plano, e o resolvedor o encontra pela chave de lookup.
Para hierarquia, use `__` no nome da variável.

Em ambos os casos, o `appsettings.json` versionado continua carregando apenas a chave de lookup
(`PasswordEnvVar`, etc.), nunca o valor.

---

## 3. Pipeline do GitHub Actions — GitHub Actions Secrets

Os workflows reais estão em `.github/workflows/`:

- `build-and-test.yml` — roda em PR/push para `main`: `dotnet build` + `dotnet test` em Release.
- `deploy-to-development.yml` / `deploy-to-staging.yml` / `deploy-to-production.yml` — validam build e
  testes e então publicam (`dotnet publish`). Os jobs de deploy usam GitHub **Environments**
  (`development`, `staging`, `production`), que é onde ficam os *Environment secrets*.

Registre os valores em **Settings → Secrets and variables → Actions** (como *Repository secret* ou, de
preferência, como *Environment secret* do environment correspondente).

Injete cada secret como variável de ambiente no step que roda o processo. Use o **nome plano** como chave
de lookup; para chaves hierárquicas, use `__` no nome da variável de ambiente.

```yaml
      - name: Release to Development Server
        env:
          RABBITMQ_DEFAULT_USER: ${{ secrets.RABBITMQ_DEFAULT_USER }}
          RABBITMQ_DEFAULT_PASS: ${{ secrets.RABBITMQ_DEFAULT_PASS }}
          NEXUS_PG_USER: ${{ secrets.NEXUS_PG_USER }}
          NEXUS_PG_PASSWORD: ${{ secrets.NEXUS_PG_PASSWORD }}
          # Só com SASL no Kafka:
          # NEXUS_KAFKA_SASL_USERNAME: ${{ secrets.NEXUS_KAFKA_SASL_USERNAME }}
          # NEXUS_KAFKA_SASL_PASSWORD: ${{ secrets.NEXUS_KAFKA_SASL_PASSWORD }}
        run: |
          echo "Deploy de ${{ github.sha }}..."
          # comandos reais de publicação
```

Como o deploy real deve alinhar com o baseline da organização, prefira que o CI **não carregue os valores
de segredo** diretamente: deixe que o ambiente de destino (ECS/EKS) leia do AWS Secrets Manager
(cenário 2). Os GitHub Actions Secrets ficam reservados para credenciais do próprio pipeline (ex.: role de
deploy na AWS via OIDC). Use GitHub Actions Secrets para injetar valores de aplicação apenas quando o
destino não tiver acesso direto ao Secrets Manager.

> Os steps de `build-and-test.yml` não precisam de credenciais: eles compilam e rodam a suíte, que não
> depende de broker nem banco reais.

---

## Resumo

- A chave de lookup vai no `appsettings.json`; o **valor** nunca.
- `.env` (infra, servidor) e User Secrets (app, cliente) são pontas distintas e complementares; o `.env`
  segue necessário para o docker-compose e os valores das duas pontas devem coincidir.
- Dev local: **.NET User Secrets** (ou env var via docker-compose). Nunca em produção.
- Produção/staging: **AWS Secrets Manager**, via provider de `IConfiguration` ou injeção como env var.
- CI: **GitHub Actions Secrets** injetados como env var no job; de preferência, deixe o destino ler do
  AWS Secrets Manager.
- Nome plano = chave direta; nome hierárquico usa `__` (env var) ↔ `:` (`IConfiguration`).
