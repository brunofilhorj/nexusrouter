/**
 * DTOs TypeScript que espelham o contrato do SimCore (NFR-MAINT-03).
 *
 * A API HTTP e o feed de telemetria WebSocket serializam com os defaults "web" do
 * System.Text.Json: nomes de propriedade em camelCase e enums como nomes de string
 * (ex.: "Running", "InvalidCredentials", "RoundRobin"). Os tipos abaixo refletem
 * exatamente esse formato de fio.
 */

// ---------------------------------------------------------------------------
// Enums (serializados como nomes de string)
// ---------------------------------------------------------------------------

/** FSM de controle da simulação: Idle → Running → Paused. */
export type SimulationState = 'Idle' | 'Running' | 'Paused';

/** Estratégia de seleção de conta do gerador de ordens. */
export type AccountSelection = 'RoundRobin' | 'Random';

/** Modo de resposta do acceptor ao próximo Logon do cliente (fault-injection). */
export type LogonMode = 'Success' | 'InvalidCredentials' | 'NotAuthorized';

// ---------------------------------------------------------------------------
// Configuração da simulação
// ---------------------------------------------------------------------------

/** Instrumento disponível ao gerador (SecurityID / Symbol / SecurityExchange). */
export interface Instrument {
  /** Identificador numérico B3 escrito como FIX tag 48 (SecurityID). */
  securityId: number;
  /** Símbolo de negociação escrito como FIX tag 55 (Symbol), ex.: PETR4. */
  symbol: string;
  /** Identificador de mercado escrito como FIX tag 207 (SecurityExchange), ex.: BVMF. */
  securityExchange: string;
}

/**
 * Mix percentual de desfechos de ciclo de vida produzidos pelo gerador.
 * Os pesos inteiros devem somar 100.
 */
export interface OutcomeMix {
  /** Percentual de ordens que completam como fill total. */
  fillPct: number;
  /** Percentual de ordens que passam por um ou mais partial fills. */
  partialFillPct: number;
  /** Percentual de ordens que terminam canceladas. */
  canceledPct: number;
  /** Percentual de ordens rejeitadas. */
  rejectedPct: number;
}

/**
 * Snapshot imutável da configuração da simulação, tal como retornado por
 * GET /v1/state e PUT /v1/config.
 */
export interface SimulationConfig {
  /** Total de ordens a gerar na rodada; 0 = ilimitado. Travado durante Running. */
  totalOrders: number;
  /** Taxa alvo de envio em mensagens por segundo. Hot — ajustável durante Running. */
  ratePerSecond: number;
  /** Identificadores de conta/cliente. Travado durante Running. */
  accounts: string[];
  /** Estratégia de seleção de conta. Travado durante Running. */
  accountSelection: AccountSelection;
  /** Instrumentos disponíveis ao gerador. Travado durante Running. */
  instruments: Instrument[];
  /** Distribuição de desfechos. Hot — ajustável durante Running. */
  outcome: OutcomeMix;
  /** Número máximo de partial fills antes do fill final. Travado durante Running. */
  partialFillsMax: number;
  /** Quando true, mensagens de aplicação setam TestMessageIndicator (464) = Y. Travado durante Running. */
  testMessageIndicator: boolean;
  /** MsgTypes FIX que o gerador pode emitir. Travado durante Running. */
  enabledMessageTypes: string[];
}

/**
 * Corpo de PUT /v1/config e corpo opcional de POST /v1/sim/start.
 *
 * Todo campo é opcional: um campo omitido mantém o valor da configuração vigente,
 * permitindo enviar um delta parcial (ex.: apenas ratePerSecond durante Running).
 */
export type SimulationConfigRequest = Partial<SimulationConfig>;

// ---------------------------------------------------------------------------
// Estado consolidado (GET /v1/state)
// ---------------------------------------------------------------------------

/** Visão da sessão do acceptor FIX dentro de StateResponse. */
export interface SessionStateView {
  /** Nome do estado da FSM da sessão (ex.: Active, Disconnected). */
  state: string;
  /** SenderCompID do acceptor (o lado B3 simulado). */
  senderCompId: string;
  /** TargetCompID esperado do cliente (o lado NexusRouter). */
  targetCompId: string;
  /** Próximo MsgSeqNum (FIX tag 34) de saída que a sessão alocará. */
  nextOutboundMsgSeqNum: number;
  /** Próximo MsgSeqNum de entrada que a sessão espera do cliente. */
  nextExpectedInboundMsgSeqNum: number;
}

/** Visão da FSM da simulação e contadores da rodada dentro de StateResponse. */
export interface SimulationStateView {
  /** Estado da FSM de controle (Idle, Running, Paused). */
  state: SimulationState;
  /** Total de mensagens de aplicação enviadas na rodada ativa ou mais recente. */
  totalSent: number;
  /** Erros de envio/geração na rodada ativa ou mais recente. */
  errors: number;
  /** Contagens por ExecType, chaveadas pelo caractere da FIX tag 150. */
  byExecType: Record<string, number>;
  /** Milissegundos decorridos desde o início da rodada atual, ou 0 quando não há rodada ativa. */
  elapsedMs: number;
}

/**
 * Snapshot consolidado do control-plane retornado por GET /v1/state: a sessão FIX,
 * a FSM da simulação com contadores, e a SimulationConfig vigente.
 */
export interface StateResponse {
  /** Visão da sessão do acceptor FIX, ou null quando nenhum cliente conectou ainda. */
  session: SessionStateView | null;
  /** Estado da FSM da simulação e contadores da rodada. */
  simulation: SimulationStateView;
  /** O snapshot de configuração atualmente em vigor. */
  config: SimulationConfig;
}

// ---------------------------------------------------------------------------
// Corpos de requisição de fault-injection
// ---------------------------------------------------------------------------

/** Corpo de POST /v1/fault/logout: o Text (tag 58) carregado no Logout forçado (FR-FAULT-02). */
export interface ForceLogoutRequest {
  /** Motivo legível do Logout, ex.: "MsgSeqNum too low". */
  text: string;
}

/** Corpo de POST /v1/fault/logon-mode: como o próximo Logon do cliente é respondido (FR-FAULT-03). */
export interface LogonModeRequest {
  /** Resposta de Logon aplicada ao próximo Logon. */
  mode: LogonMode;
}

/** Corpo de POST /v1/fault/seq-gap: quantos MsgSeqNum de saída pular (FR-FAULT-04). */
export interface SequenceGapRequest {
  /** Número de sequence numbers de saída a pular. Deve ser positivo. */
  skip: number;
}

// ---------------------------------------------------------------------------
// Erro estruturado da API (FR-API-05)
// ---------------------------------------------------------------------------

/**
 * Corpo de erro estruturado retornado pela API para 400/404/409.
 * `code` identifica a categoria (ex.: validation_failed, not_found, invalid_state,
 * invalid_session_state) e `message` é o detalhe legível.
 */
export interface ApiError {
  /** Código estável da categoria de erro. */
  code: string;
  /** Detalhe legível do erro. */
  message: string;
}

// ---------------------------------------------------------------------------
// Eventos de telemetria (feed WebSocket /v1/ws)
// ---------------------------------------------------------------------------

/** Campos comuns a todo evento de telemetria. */
interface TelemetryEventBase {
  /** Timestamp UTC (ISO 8601) de quando o evento foi produzido. */
  timestampUtc: string;
}

/** Emitido quando a FSM da sessão do acceptor FIX transiciona. */
export interface SessionStateChanged extends TelemetryEventBase {
  type: 'SessionStateChanged';
  /** Nome legível do estado da sessão (ex.: Active, Disconnected). */
  state: string;
  /** SenderCompID do acceptor. */
  senderCompId: string;
  /** TargetCompID do cliente conectado / CompID esperado. */
  targetCompId: string;
  /** Próximo MsgSeqNum de saída no momento da transição. */
  msgSeqNum: number;
  /** Horário UTC (ISO 8601) da última atividade de heartbeat, quando houver. */
  lastHeartbeatUtc?: string | null;
}

/** Emitido quando a FSM de controle da simulação transiciona. */
export interface SimulationStateChanged extends TelemetryEventBase {
  type: 'SimulationStateChanged';
  /** Novo estado da simulação. */
  state: SimulationState;
  /** Motivo opcional (ex.: stop, limite atingido, fault). */
  reason?: string | null;
}

/** Snapshot periódico dos contadores de envio e taxa observada. */
export interface CountersSnapshot extends TelemetryEventBase {
  type: 'CountersSnapshot';
  /** Total de mensagens de aplicação enviadas com sucesso na rodada atual. */
  totalSent: number;
  /** Taxa de envio observada no último intervalo de contadores. */
  ratePerSec: number;
  /** Contagens chaveadas pelo caractere ExecType (FIX tag 150). */
  byExecType: Record<string, number>;
  /** Número de erros de envio/geração na rodada atual. */
  errors: number;
  /** Milissegundos decorridos desde o início da rodada atual. */
  elapsedMs: number;
}

/** Resumo amostrado de mensagem de aplicação para o feed de mensagens da UI. */
export interface MessageSample extends TelemetryEventBase {
  type: 'MessageSample';
  /** FIX MsgType (ex.: 8). */
  msgType: string;
  /** Client order id (ClOrdID), quando presente. */
  clOrdId?: string | null;
  /** Exchange order id (OrderID), quando presente. */
  orderId?: string | null;
  /** ExecType (tag 150) como string de um caractere. */
  execType?: string | null;
  /** OrdStatus (tag 39) como string de um caractere. */
  ordStatus?: string | null;
  /** Side (tag 54) como string de um caractere. */
  side?: string | null;
  /** Símbolo do instrumento para exibição. */
  instrument?: string | null;
  /** BusinessSeqNum B3 (tag 10016), quando presente. */
  businessSeqNum?: number | null;
  /** TransactTime / horário da amostra em UTC (ISO 8601). */
  tsUtc: string;
}

/**
 * União discriminada de todos os eventos de telemetria, distinguidos pela
 * propriedade `type` (discriminador do System.Text.Json polimórfico).
 */
export type TelemetryEvent =
  | SessionStateChanged
  | SimulationStateChanged
  | CountersSnapshot
  | MessageSample;
