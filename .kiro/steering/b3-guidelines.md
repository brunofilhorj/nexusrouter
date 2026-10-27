---
inclusion: auto
name: b3-guidelines
description: B3 EntryPoint FIX 4.4 protocol rules, message catalog, field constraints, and business logic requirements. Activate whenever implementing FIX session logic, message parsing, order routing, or any B3-specific protocol behavior.
---

# NexusRouter — Diretrizes do Protocolo B3 EntryPoint

> Especificação de referência: **EntryPoint B3 v2.40** (`docs/EntryPointMessageSpecs.pdf`)  
> Dicionário de dados FIX: `docs/FIX44Entrypoint.xml`  
> Versão do protocolo: **FIX 4.4** (`BeginString = FIX.4.4`)

Estas diretrizes são **normativas** para toda a implementação do protocolo FIX neste projeto.  
Quando a spec e estas diretrizes conflitarem, a documentação oficial da B3 em PDF tem precedência — atualize este arquivo para refletir a divergência.

---

## 1. Configuração de Sessão — Identidade do B3 EntryPoint

| Parâmetro | Tag FIX | Descrição |
|-----------|---------|-----------|
| `BeginString` | 8 | Sempre `FIX.4.4` para o B3 EntryPoint |
| `SenderCompID` | 49 | Identificador atribuído ao cliente (fornecido pela B3 no onboarding) |
| `TargetCompID` | 56 | Identificador do gateway B3 — depende do ambiente (`ENTRYPOINT-FIRM-0001`, etc.) |
| `HeartBtInt` | 108 | Intervalo de heartbeat em segundos — **deve ser 20** para produção no B3 EntryPoint |
| `Username` | 553 | Login do operador — fornecido pela B3 |
| `Password` | 554 | Senha do operador — **deve vir de injeção de secrets, nunca hardcoded** |
| `EncryptMethod` | 98 | Sempre `0` (None) — a B3 usa TLS na camada de transporte, não criptografia a nível FIX |
| `ResetSeqNumFlag` | 141 | `Y` no primeiro Logon do dia de negociação; `N` na reconexão dentro da sessão |
| `NextExpectedMsgSeqNum` | 789 | Enviado no Logon ao continuar uma sessão sem resetar sequence numbers |

---

## 2. Campos de Header Específicos da B3

O B3 EntryPoint estende o header padrão FIX 4.4 com campos proprietários. Estes **devem** ser corretamente populados em todas as mensagens:

| Tag | Nome do Campo | Tipo | Descrição |
|-----|--------------|------|-----------|
| 10016 | `BusinessSeqNum` | STRING | Sequence number de nível de negócio da B3, atribuído pela bolsa. Somente leitura em mensagens recebidas. Não gere no lado do cliente. |
| 35020 | `AdministrativeMessage` | BOOLEAN | Definido pela B3 para indicar mensagens administrativas (não relacionadas a negociação). |
| 5024 | `InternalSeqNo` | SEQNUM | Sequence number interno da B3 — usado para solicitações de retransmissão. |
| 35035 | `InternalSeqNoVerID` | STRING | Identificador de versão do `InternalSeqNo`. |
| 35036 | `ProducerID` | STRING | Identificador de produtor/canal da B3. |

**Regra:** Esses campos devem ser preservados no modo `PassThrough` — eles fazem parte do buffer bruto e não devem ser removidos. No modo `Parse`, devem ser capturados no conjunto de campos de `FixMessage` para fins de trilha de auditoria.

---

## 3. Cancel-on-Disconnect (COD)

O B3 EntryPoint suporta cancelamento automático de ordens na desconexão de sessão. Esse comportamento é configurado via campos da mensagem de Logon:

| Tag | Nome do Campo | Valores | Significado |
|-----|--------------|---------|-------------|
| 35002 | `CancelOnDisconnectType` | `0` | NÃO cancelar na desconexão ou logout |
| | | `1` | Cancelar somente na desconexão |
| | | `2` | Cancelar somente no logout |
| | | `3` | Cancelar na desconexão OU no logout |
| 35003 | `CODTimeoutWindow` | INT (segundos) | Período de carência antes do COD disparar. Padrão B3: 5 segundos. |

**Regra de Implementação:** `CancelOnDisconnectType` deve ser configurável por sessão via `SessionOptions`. O padrão nos exemplos de configuração deve ser `0` (padrão seguro — sem cancelamentos não intencionais). O valor de COD enviado no Logon DEVE corresponder ao perfil de risco do operador e deve ser explicitamente documentado nos comentários de configuração.

---

## 4. Sequence Numbers de Aplicação (ApplSeqNum)

A B3 usa um esquema de numeração de sequência dupla:

1. **FIX Session SeqNum (`MsgSeqNum`, tag 34):** Sequence number padrão de nível de sessão FIX. Gerenciado por conexão de sessão. Resetado no Logon de novo dia de negociação (`ResetSeqNumFlag=Y`).
2. **Application SeqNum (`ApplSeqNum`, tag 1181 / `BusinessSeqNum`, tag 10016):** Atribuído pela bolsa, monotonicamente crescente por `ApplID`. Usado para recuperação de mensagens a nível de aplicação via `ApplicationMessageRequest` (MsgType=BW).

**Regra:** `MsgSeqNum` é gerenciado pelo `FixSessionStateMachine`. `BusinessSeqNum` é somente leitura nas mensagens recebidas e deve ser preservado nos registros de auditoria. Não confunda ou conflate essas duas sequências.

---

## 5. Catálogo Completo de Mensagens do B3 EntryPoint

Todas as 31 mensagens em `docs/FIX44Entrypoint.xml`:

### 5.1 Mensagens Administrativas (Nível de Sessão)

| MsgType | Nome da Mensagem | Direção | Descrição |
|---------|-----------------|---------|-----------|
| `0` | Heartbeat | Ambos | Keepalive. Enviar em intervalos de `HeartBtInt` se nenhuma outra mensagem for enviada. |
| `1` | TestRequest | Cliente → B3 | Sonda para verificar conexão ativa. A B3 responde com Heartbeat contendo `TestReqID`. |
| `2` | ResendRequest | Ambos | Solicita retransmissão de mensagens perdidas por intervalo de SeqNum. |
| `3` | Reject | B3 → Cliente | Rejeição de nível de sessão (mensagem malformada, tag inválida). |
| `4` | SequenceReset | Ambos | Gap-fill ou reset absoluto de sequence number de sessão. `GapFillFlag=Y` para gap-fill; `N` para reset. |
| `5` | Logout | Ambos | Encerramento gracioso de sessão. |
| `A` | Logon | Ambos | Estabelecimento de sessão. Contém credenciais de autenticação e parâmetros de sessão. |
| `BD` | NetworkStatusResponse | B3 → Cliente | Reporta o status dos componentes de rede da B3 (gateways, engines). |

### 5.2 Mensagens de Gestão de Ordens (Nível de Aplicação)

| MsgType | Nome da Mensagem | Direção | Campos Obrigatórios Principais |
|---------|-----------------|---------|-------------------------------|
| `D` | NewOrderSingle | Cliente → B3 | `ClOrdID(11)`, `Instrument`, `Side(54)`, `OrdType(40)`, `TransactTime(60)`, `OrderQty(38)` |
| `G` | OrderCancelReplaceRequest | Cliente → B3 | `OrigClOrdID(41)`, `ClOrdID(11)`, `OrderID(37)`, `Instrument`, `Side(54)`, `TransactTime(60)` |
| `F` | OrderCancelRequest | Cliente → B3 | `OrigClOrdID(41)`, `ClOrdID(11)`, `OrderID(37)`, `Side(54)`, `TransactTime(60)` |
| `s` | NewOrderCross | Cliente → B3 | `CrossID(548)`, `CrossType(549)`, grupo `NoSides`, `Instrument`, `TransactTime(60)` |
| `CA` | OrderMassActionRequest | Cliente → B3 | `MassActionType(1373)`, `MassActionScope(1374)`, `TransactTime(60)` |
| `8` | ExecutionReport | B3 → Cliente | `OrderID(37)`, `ExecID(17)`, `ExecType(150)`, `OrdStatus(39)`, `Side(54)`, `Instrument` |
| `9` | OrderCancelReject | B3 → Cliente | `OrderID(37)`, `ClOrdID(11)`, `OrigClOrdID(41)`, `OrdStatus(39)`, `CxlRejReason(102)` |
| `BZ` | OrderMassActionReport | B3 → Cliente | `MassActionReportID(1369)`, `MassActionResponse(1375)`, `MassActionType(1373)` |

### 5.3 Mensagens de Cotação (Nível de Aplicação)

| MsgType | Nome da Mensagem | Direção | Observações |
|---------|-----------------|---------|-------------|
| `R` | QuoteRequest | Cliente → B3 | Solicitação de cotação para um instrumento |
| `S` | Quote | Ambos | Submissão de cotação (cliente) ou disseminação de cotação (B3) |
| `Z` | QuoteCancel | Cliente → B3 | Cancela cotações em aberto |
| `AG` | QuoteRequestReject | B3 → Cliente | Rejeita um QuoteRequest |
| `AI` | QuoteStatusReport | B3 → Cliente | Atualização de status de uma cotação submetida |

### 5.4 Mensagens de Recuperação de Aplicação

| MsgType | Nome da Mensagem | Direção | Observações |
|---------|-----------------|---------|-------------|
| `BW` | ApplicationMessageRequest | Cliente → B3 | Solicita retransmissão de mensagens de aplicação por intervalo de `ApplSeqNum` |
| `BX` | ApplicationMessageRequestAck | B3 → Cliente | Confirmação da solicitação |
| `BY` | ApplicationMessageReport | B3 → Cliente | Notificação de eventos de nível de aplicação (ex.: reenvio concluído) |

### 5.5 Mensagens de Pós-Negociação / Alocação (Nível de Aplicação)

| MsgType | Nome da Mensagem | Direção | Observações |
|---------|-----------------|---------|-------------|
| `J` | AllocationInstruction | Cliente → B3 | Instrução de alocação de negócio |
| `AS` | AllocationReport | B3 → Cliente | Resultado do processamento de alocação |
| `j` | BusinessMessageReject | B3 → Cliente | Rejeição de nível de aplicação (lógica de negócio inválida). Tag `372` = MsgType rejeitado. |

### 5.6 Mensagens de Posição e Dados de Referência

| MsgType | Nome da Mensagem | Direção | Observações |
|---------|-----------------|---------|-------------|
| `AL` | PositionMaintenanceRequest | Cliente → B3 | Solicitação de ajuste de posição |
| `AM` | PositionMaintenanceReport | B3 → Cliente | Resultado do ajuste de posição |
| `c` | SecurityDefinitionRequest | Cliente → B3 | Solicita detalhes do instrumento |
| `d` | SecurityDefinition | B3 → Cliente | Resposta de definição do instrumento |

---

## 6. Regras Críticas de Campos para Mensagens de Ordem

### 6.1 `ClOrdID` (tag 11) — ID de Ordem do Cliente
- **DEVE** ser único por ordem, por dia de negociação e por sessão.
- **NÃO DEVE** ser reutilizado entre sessões ou dias de calendário.
- Comprimento máximo: **20 caracteres** (restrição da B3).
- Caracteres permitidos: alfanumérico, hífen (`-`) e underscore (`_`) apenas.
- Estratégia de geração: `{YYYYMMDD}-{SessionId}-{ContadorMonotônico:D8}` é recomendado.

### 6.2 `TransactTime` (tag 60) — Timestamp da Transação
- **DEVE** estar em UTC, formato: `YYYYMMDD-HH:MM:SS.sss` (precisão mínima em milissegundos).
- **DEVE** estar dentro de ±2 segundos do relógio do gateway da B3 (a B3 impõe sincronização de relógio).
- Gerar usando `DateTime.UtcNow` no hot path — NÃO chamar `DateTimeOffset.Now` (incorre em lookup de fuso horário).
- Pré-formatar o prefixo de data por segundo em um buffer cacheado; atualizar apenas a parte do horário por mensagem para performance.

### 6.3 `OrdType` (tag 40) — Tipo de Ordem
| Valor | Descrição | Observações |
|-------|-----------|-------------|
| `1` | Market | `Price` não obrigatório |
| `2` | Limit | `Price(44)` obrigatório |
| `3` | Stop | `StopPx(99)` obrigatório |
| `4` | Stop Limit | Ambos `Price(44)` e `StopPx(99)` obrigatórios |
| `K` | Market With Leftover as Limit | Específico da B3 |
| `P` | Pegged | `PegOffsetValue` obrigatório |

### 6.4 `TimeInForce` (tag 59) — Duração da Ordem
| Valor | Descrição | Restrição B3 |
|-------|-----------|-------------|
| `0` | Day | Padrão para ordens de renda variável |
| `1` | GTC (Good Till Cancel) | Requer elegibilidade `InstrAttribType=34` no instrumento |
| `3` | IOC (Immediate Or Cancel) | |
| `4` | FOK (Fill Or Kill) | |
| `6` | GTD (Good Till Date) | `ExpireDate(432)` obrigatório; requer elegibilidade |

### 6.5 `Side` (tag 54)
| Valor | Descrição |
|-------|-----------|
| `1` | Buy (Compra) |
| `2` | Sell (Venda) |
| `5` | Sell Short (Venda a descoberto) |
| `6` | Sell Short Exempt |

### 6.6 `ExecType` (tag 150) — Classificação do ExecutionReport
| Valor | Descrição | Observações |
|-------|-----------|-------------|
| `0` | New | Ordem aceita |
| `1` | PartialFill | Ordem parcialmente executada |
| `2` | Fill | Ordem totalmente executada |
| `4` | Canceled | Ordem cancelada |
| `5` | Replaced | Ordem modificada (substitui ordem anterior) |
| `8` | Rejected | Ordem rejeitada pela B3 |
| `C` | Expired | Ordem expirada (GTD/GTC) |
| `D` | Restated | Ordem reafirmada (sem ação necessária do cliente) |
| `E` | PendingReplace | Modificação pendente |
| `F` | Trade | Execução de negócio (alias de Fill em alguns reports) |

**Regra:** `ExecType(150)` e `OrdStatus(39)` são campos independentes. Sempre usar `ExecType` para determinar o _tipo de evento_ e `OrdStatus` para determinar o _estado atual_ da ordem. Podem diferir (ex.: um partial fill produz `ExecType=1` mas `OrdStatus=1` se mais quantidade estiver pendente).

---

## 7. `ExecutionReport` (MsgType=8) — Regras de Processamento

O `ExecutionReport` é a mensagem recebida mais crítica. O NexusRouter deve:

1. **Preservar o buffer bruto completo** no modo `PassThrough` sem modificação (exceto reescrita de SeqNum/Checksum para sessões FIX outbound).
2. **Extrair para auditoria** (campos mínimos independente do DeliveryMode):
   - `OrderID (37)`, `ClOrdID (11)`, `ExecID (17)`, `ExecType (150)`, `OrdStatus (39)`, `Side (54)`, `LastQty (32)`, `LastPx (31)`, `LeavesQty (151)`, `CumQty (14)`, `BusinessSeqNum (10016)`.
3. **Campo específico B3 `BTSFinalTxOrdStatus` (10029):** Indica o status final da ordem conforme processado internamente pela B3. Difere de `OrdStatus` em cenários específicos (ex.: partial fills próximos ao fim do dia). Registrar este campo em todos os registros de auditoria de `ExecutionReport`.
4. **`MMProtectionReset` (9773):** Indicador de reset de proteção de Market Maker. Se `Y`, rotear para um channel de monitor de risco dedicado, além do roteamento normal.

---

## 8. Identificação de Instrumentos

Instrumentos da B3 são identificados usando:

| Tag | Campo | Descrição |
|-----|-------|-----------|
| 48 | `SecurityID` | ID numérico do instrumento na B3 (preferido) |
| 22 | `SecurityIDSource` | Deve ser `8` (Exchange Symbol) ao usar a tag 48 |
| 55 | `Symbol` | Símbolo do ticker (ex.: `PETR4`, `VALE3`) |
| 207 | `SecurityExchange` | Sempre `BVMF` para a B3 |
| 9219 | `InstrumentID` | ID interno de instrumento da B3 (campo proprietário) |
| 1151 | `SecurityGroup` | Grupo de ativo (ex.: `IBOV`, `SMAL`) |

**Regra:** Sempre preferir `SecurityID (48)` + `SecurityIDSource=8` em vez de `Symbol (55)` para roteamento de ordens. Símbolos podem ser ambíguos entre segmentos de mercado. `InstrumentID (9219)` pode ser usado para lookups na tabela de roteamento interno.

---

## 9. Componente Parties (`<component name="Parties">`)

O grupo repetido Parties (NoPartyIDs, tag 453) é obrigatório na maioria das mensagens da B3. Papéis de party principais:

| `PartyRole (452)` | Descrição | Observações |
|-------------------|-----------|-------------|
| `3` | Client ID | Identificador do cliente final |
| `4` | Clearing Firm | ID do membro liquidante |
| `7` | Entering Firm | Firma que está inserindo a ordem |
| `12` | Executing Trader | Trader responsável pela ordem |
| `17` | Contra Firm | Contraparte em uma operação cruzada |
| `36` | Entering Trader | Alias para operador de inserção |
| `54` | Sender Location | Localização/mesa de negociação |

**Regra:** `PartyRole=4` (Clearing Firm) é obrigatório no `NewOrderSingle` para o mercado de renda variável da B3. A ausência causa um `BusinessMessageReject`. Isso deve ser validado na camada de aplicação.

---

## 10. `BusinessMessageReject` (MsgType=j) — Regras de Tratamento

Quando a B3 rejeita uma mensagem na camada de aplicação:
- `RefMsgType (372)`: o MsgType da mensagem rejeitada.
- `BusinessRejectReason (380)`: código numérico de motivo de rejeição.
- `Text (58)`: motivo legível por humano.
- `SanityValidation (10031)`: Boolean — se `Y`, a B3 realizou apenas uma verificação de sanidade (a mensagem não foi totalmente processada). **Este é um fluxo específico da B3 para testes**.

**Regra de Implementação:** `BusinessMessageReject` deve sempre ser:
1. Roteado para o channel de auditoria com `WasDropped=false` e captura completa de campos.
2. Publicado para todos os dispatchers registrados (igual a qualquer outra mensagem de aplicação).
3. **Nunca descartado silenciosamente.**

---

## 11. `OrderMassActionRequest` (CA) / `OrderMassActionReport` (BZ)

A B3 suporta cancelamento em massa de ordens via `OrderMassActionRequest`:
- `MassActionType (1373)`:
  - `2` = Liberar ordens da suspensão
  - `3` = Cancelar ordens
  - `4` = Cancelar e suspender ordens
- `MassActionScope (1374)`: Apenas o valor `6` (Todas as Ordens de uma Sessão de Negociação) é suportado pela B3.

**Regra:** Solicitações de ação em massa devem disparar um registro de auditoria imediatamente no envio, não apenas quando o `OrderMassActionReport` for recebido. Tanto a solicitação quanto o report devem aparecer na trilha de auditoria.

---

## 12. Recuperação de Sequence Number de Sessão

A B3 suporta dois mecanismos de recuperação:

### 12.1 Gap-Fill FIX Padrão (`ResendRequest` / `SequenceReset`)
- Retransmissão padrão de nível de sessão FIX 4.4.
- Usado quando uma lacuna no `MsgSeqNum` é detectada.
- A B3 responderá com `SequenceReset (GapFillFlag=Y)` para mensagens que não pode retransmitir.

### 12.2 Recuperação de Nível de Aplicação (`ApplicationMessageRequest` BW)
- Usado para recuperar **mensagens de aplicação** por intervalo de `BusinessSeqNum` (não por SeqNum de sessão FIX).
- Permite recuperação entre desconexões de sessão (ex.: recuperar `ExecutionReport`s de uma conexão anterior).
- `ApplReqType (1347)`: valor `0` = Retransmissão.
- A B3 responde com `ApplicationMessageRequestAck (BX)`, depois as mensagens retransmitidas, encerrando com `ApplicationMessageReport (BY)`.

**Regra:** O NexusRouter deve rastrear o último `BusinessSeqNum (10016)` recebido por `ProducerID` e persistir no `SequenceNumberStore` (cold path). Isso habilita solicitações de recuperação automáticas na reconexão.

---

## 13. Códigos de Erro da B3 e Comportamento de Reconexão

| Cenário | Comportamento Esperado | Observação de Implementação |
|---------|----------------------|----------------------------|
| `Reject (3)` com `SessionRejectReason=1` (tag inválida) | Registrar erro, não reconectar | Provavelmente um bug de código — alertar |
| `Reject (3)` com `SessionRejectReason=5` (valor fora do range) | Registrar erro, não reconectar | Validar valores de campo antes do envio |
| `Logout (5)` com `Text` contendo "MsgSeqNum too low" | Resetar SeqNum, reconectar com `ResetSeqNumFlag=Y` | Recuperação de dessincronização de sequência |
| `Logout (5)` com `Text` contendo "Password will expire" | Registrar aviso, alertar operador | Usar `DaysBeforePwdExpiration (6931)` do Logon |
| `ApplicationLogonRspCode (6930)` não zero | Logon falhou — não tentar novamente sem intervenção | Códigos: `0`=sucesso, `1`=credenciais inválidas, `2`=não autorizado |
| Desconexão TCP (sem Logout) | Reconectar com exponential backoff | COD pode ter disparado — registrar e alertar |

---

## 14. Ambiente de Teste vs. Produção

| Configuração | Teste (Certificação) | Produção |
|-------------|---------------------|---------|
| `TestMessageIndicator (464)` | `Y` | `N` ou ausente |
| `SanityValidation (10031)` | Pode ser `Y` | Nunca `Y` |
| `TargetCompID (56)` | ID do gateway de certificação atribuído pela B3 | ID do gateway de produção atribuído pela B3 |
| Certificado TLS | CA de teste da B3 | CA de produção da B3 |
| `HeartBtInt` | 20 (igual) | 20 (obrigatório) |

**Regra:** Mensagens com `TestMessageIndicator=Y` nunca devem ser roteadas para consumidores downstream de produção. O motor de roteamento deve filtrar essas mensagens para um dispatcher exclusivo de testes ou descartá-las com base na configuração de `DeliveryMode`.

---

## 15. Cálculo do Checksum FIX (Padrão B3)

A B3 segue o checksum padrão FIX:

```
CheckSum = (soma de todos os valores de bytes da mensagem, do byte 0 até, mas NÃO incluindo "10=") mod 256
Formatado como: exatamente 3 dígitos ASCII, com zero à esquerda (ex.: "037", "255", "003")
```

O campo `CheckSum` (tag 10) é sempre o último campo em uma mensagem FIX, seguido por `SOH (0x01)`.

**Regra Crítica:** Ao reescrever `MsgSeqNum (34)` no modo `PassThrough`:
1. Reescrever o valor da tag 34 in-place (mesmo número de bytes ou preencher com espaços).
2. Recomputar o checksum sobre todo o buffer modificado.
3. Escrever o novo checksum de 3 dígitos no campo `10=XXX` in-place.
4. NÃO modificar BodyLength (tag 9) se a reescrita do SeqNum mantiver a mesma contagem de bytes.
5. Se o novo SeqNum tiver mais dígitos que o original, BodyLength TAMBÉM DEVE ser atualizado — esta é uma restrição conhecida. Projete o `PassThroughRewriter` para lidar com isso reservando espaço de preenchimento.

---

## 16. `FIX44Entrypoint.xml` como Referência Autoritativa de Campos

O arquivo `docs/FIX44Entrypoint.xml` é o dicionário de dados FIX legível por máquina para este projeto. Ele define:
- Todos os números de campo, nomes e tipos.
- Todas as definições de mensagem (campos obrigatórios/opcionais e grupos repetidos).
- Todos os valores enumerados para campos tipados.

**Ao implementar qualquer handler ou builder de mensagem FIX, consulte diretamente o `FIX44Entrypoint.xml`.** Agentes do Kiro devem referenciar este arquivo usando `#[[file:docs/FIX44Entrypoint.xml]]` nas specs quando a implementação exigir detalhes de nível de campo.

Os seguintes intervalos de campo são proprietários da B3 (não estão no FIX 4.4 padrão):

| Intervalo | Categoria |
|-----------|-----------|
| 5001–5975 | Campos específicos da bolsa B3 |
| 6032–6937 | Campos de dados de mercado e sessão da B3 |
| 7534, 9219, 9701, 9757, 9773 | Campos de instrumento e ordem da B3 |
| 10016–10033 | Campos de sequência de negócio e status da B3 |
| 35001–35551 | Extensões proprietárias do B3 EntryPoint |
