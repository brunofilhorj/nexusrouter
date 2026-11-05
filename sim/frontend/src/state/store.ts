/**
 * Store de aplicação do SimPanel.
 *
 * Mantém o estado reativo empurrado pela telemetria (Seção 4.3 do design): a
 * conectividade do WebSocket, a última visão da sessão FIX, o estado/contadores
 * da simulação e um ring buffer das últimas N `MessageSample` para o
 * `MessageFeed` (FR-UI-07). É um store externo mínimo (sem dependência de lib)
 * compatível com `useSyncExternalStore` do React: componentes assinam via
 * `subscribe` e leem o snapshot imutável via `getSnapshot`.
 */

import type {
  CountersSnapshot,
  MessageSample,
  SessionStateChanged,
  SimulationState,
  SimulationStateChanged,
  TelemetryEvent,
} from '../api/types';

/** Estado de conectividade do feed de telemetria WebSocket (FR-UI-08). */
export type ConnectivityStatus = 'connecting' | 'open' | 'closed';

/** Visão da sessão FIX derivada do último `SessionStateChanged` recebido. */
export interface SessionView {
  /** Nome legível do estado da sessão (ex.: Active, Disconnected). */
  state: string;
  /** SenderCompID do acceptor (lado B3 simulado). */
  senderCompId: string;
  /** TargetCompID do cliente conectado / CompID esperado. */
  targetCompId: string;
  /** Próximo MsgSeqNum de saída no momento da última transição. */
  msgSeqNum: number;
  /** Horário UTC (ISO 8601) da última atividade de heartbeat, quando houver. */
  lastHeartbeatUtc: string | null;
  /** Timestamp UTC (ISO 8601) da transição que produziu esta visão. */
  timestampUtc: string;
}

/** Contadores de telemetria derivados do último `CountersSnapshot`. */
export interface CountersView {
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

/** Snapshot imutável do estado observável do SimPanel. */
export interface AppState {
  /** Estado da conexão WebSocket de telemetria. */
  connectivity: ConnectivityStatus;
  /** Última visão da sessão FIX, ou null enquanto nenhum evento chegou. */
  session: SessionView | null;
  /** Estado corrente da FSM da simulação (default Idle). */
  simulationState: SimulationState;
  /** Último motivo de transição da simulação, quando informado. */
  simulationReason: string | null;
  /** Últimos contadores recebidos, ou null enquanto nenhum chegou. */
  counters: CountersView | null;
  /**
   * Ring buffer das últimas N `MessageSample`, mais recente no fim do array.
   * Limitado por `sampleCapacity` para não crescer indefinidamente.
   */
  samples: readonly MessageSample[];
}

/** Capacidade default do ring buffer de amostras do `MessageFeed`. */
export const DEFAULT_SAMPLE_CAPACITY = 500;

type Listener = () => void;

/** Estado inicial antes de qualquer evento de telemetria. */
function initialState(): AppState {
  return {
    connectivity: 'connecting',
    session: null,
    simulationState: 'Idle',
    simulationReason: null,
    counters: null,
    samples: [],
  };
}

/**
 * Store externo do SimPanel. Imutável por fora: cada mutação troca o snapshot
 * inteiro para que `useSyncExternalStore` detecte a mudança por identidade.
 */
export class AppStore {
  private state: AppState = initialState();
  private readonly listeners = new Set<Listener>();
  private readonly sampleCapacity: number;

  /**
   * @param sampleCapacity Tamanho máximo do ring buffer de `MessageSample`.
   *   Deve ser positivo; default `DEFAULT_SAMPLE_CAPACITY`.
   */
  constructor(sampleCapacity: number = DEFAULT_SAMPLE_CAPACITY) {
    this.sampleCapacity = Math.max(1, Math.floor(sampleCapacity));
  }

  /** Assina mudanças de estado; devolve a função de cancelamento. */
  readonly subscribe = (listener: Listener): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  /** Lê o snapshot imutável corrente (estável entre mutações). */
  readonly getSnapshot = (): AppState => this.state;

  /** Atualiza o estado de conectividade do WebSocket (FR-UI-08). */
  setConnectivity(status: ConnectivityStatus): void {
    if (this.state.connectivity === status) {
      return;
    }
    this.commit({ ...this.state, connectivity: status });
  }

  /**
   * Aplica um evento de telemetria tipado ao estado, derivando a mutação pelo
   * discriminador `type`. Eventos desconhecidos são ignorados.
   */
  applyEvent(event: TelemetryEvent): void {
    switch (event.type) {
      case 'SessionStateChanged':
        this.applySessionStateChanged(event);
        break;
      case 'SimulationStateChanged':
        this.applySimulationStateChanged(event);
        break;
      case 'CountersSnapshot':
        this.applyCountersSnapshot(event);
        break;
      case 'MessageSample':
        this.applyMessageSample(event);
        break;
      default:
        // União fechada: ramo inalcançável, mantido por robustez em runtime.
        break;
    }
  }

  private applySessionStateChanged(event: SessionStateChanged): void {
    const session: SessionView = {
      state: event.state,
      senderCompId: event.senderCompId,
      targetCompId: event.targetCompId,
      msgSeqNum: event.msgSeqNum,
      lastHeartbeatUtc: event.lastHeartbeatUtc ?? null,
      timestampUtc: event.timestampUtc,
    };
    this.commit({ ...this.state, session });
  }

  private applySimulationStateChanged(event: SimulationStateChanged): void {
    this.commit({
      ...this.state,
      simulationState: event.state,
      simulationReason: event.reason ?? null,
    });
  }

  private applyCountersSnapshot(event: CountersSnapshot): void {
    const counters: CountersView = {
      totalSent: event.totalSent,
      ratePerSec: event.ratePerSec,
      byExecType: event.byExecType,
      errors: event.errors,
      elapsedMs: event.elapsedMs,
    };
    this.commit({ ...this.state, counters });
  }

  private applyMessageSample(event: MessageSample): void {
    const next = this.state.samples.concat(event);
    const trimmed =
      next.length > this.sampleCapacity
        ? next.slice(next.length - this.sampleCapacity)
        : next;
    this.commit({ ...this.state, samples: trimmed });
  }

  private commit(next: AppState): void {
    this.state = next;
    for (const listener of this.listeners) {
      listener();
    }
  }
}

/** Instância singleton usada pela app e pelo cliente de telemetria. */
export const appStore = new AppStore();
