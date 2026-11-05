/**
 * Cliente de telemetria WebSocket do SimPanel (Seção 4.3 do design).
 *
 * Abre o feed em tempo real (`/v1/ws`) a partir da mesma base do SimCore
 * (`VITE_SIM_API`), traduzindo o esquema HTTP(S) para WS(S). Processa os eventos
 * tipados (`TelemetryEvent`) e os aplica ao store, expõe o estado de
 * conectividade (FR-UI-08) e reconecta automaticamente com backoff exponencial
 * simples e jitter até um teto.
 *
 * O WebSocket do browser não aceita headers customizados no handshake, então o
 * token de dev (`VITE_SIM_TOKEN`), quando presente, vai na query string — uso
 * somente de laboratório, alinhado a FR-API-06.
 */

import type { TelemetryEvent } from '../api/types';
import { SIM_API_BASE_URL } from '../api/client';
import { appStore, type AppStore } from '../state/store';

/** Caminho do endpoint de upgrade WebSocket do SimCore. */
const WS_PATH = '/v1/ws';

/** Token Bearer opcional (somente dev); ausente desativa a autenticação. */
const SIM_TOKEN: string | undefined = import.meta.env.VITE_SIM_TOKEN || undefined;

/** Parâmetros do backoff de reconexão. */
const RECONNECT = {
  /** Atraso inicial antes da primeira retentativa, em ms. */
  baseDelayMs: 500,
  /** Teto do atraso entre retentativas, em ms. */
  maxDelayMs: 15_000,
  /** Fator de crescimento exponencial por tentativa. */
  factor: 2,
} as const;

/** Resolve a URL absoluta do WebSocket a partir da base HTTP(S) do SimCore. */
export function resolveWebSocketUrl(
  baseUrl: string = SIM_API_BASE_URL,
  token: string | undefined = SIM_TOKEN,
): string {
  const url = new URL(WS_PATH, baseUrl);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  if (token) {
    url.searchParams.set('access_token', token);
  }
  return url.toString();
}

/** Faz o parse tolerante de uma mensagem do feed em um `TelemetryEvent`. */
function parseTelemetryEvent(data: unknown): TelemetryEvent | null {
  if (typeof data !== 'string') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    return null;
  }
  if (
    typeof parsed !== 'object' ||
    parsed === null ||
    typeof (parsed as { type?: unknown }).type !== 'string'
  ) {
    return null;
  }
  return parsed as TelemetryEvent;
}

/**
 * Conexão de telemetria WebSocket com reconexão automática.
 *
 * Ciclo de vida: `start()` abre a conexão e mantém-na viva reabrindo com backoff
 * a cada queda; `stop()` encerra e impede novas retentativas. Reaberturas
 * repetidas são seguras (idempotentes): só há um socket vivo por instância.
 */
export class TelemetryClient {
  private socket: WebSocket | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;
  private stopped = true;
  private readonly store: AppStore;
  private readonly url: string;

  /**
   * @param store Store a atualizar com conectividade e eventos.
   * @param url URL do WebSocket; default derivada de `VITE_SIM_API`.
   */
  constructor(store: AppStore = appStore, url: string = resolveWebSocketUrl()) {
    this.store = store;
    this.url = url;
  }

  /** Abre a conexão e habilita a reconexão automática. Idempotente. */
  start(): void {
    if (!this.stopped) {
      return;
    }
    this.stopped = false;
    this.open();
  }

  /** Encerra a conexão e cancela qualquer retentativa pendente. */
  stop(): void {
    this.stopped = true;
    this.clearReconnectTimer();
    if (this.socket) {
      // Remove handlers antes de fechar para não disparar reconexão.
      this.detach(this.socket);
      this.socket.close();
      this.socket = null;
    }
    this.store.setConnectivity('closed');
  }

  private open(): void {
    if (this.stopped) {
      return;
    }
    this.store.setConnectivity('connecting');

    let socket: WebSocket;
    try {
      socket = new WebSocket(this.url);
    } catch {
      // Falha síncrona ao construir (ex.: URL inválida): agenda retentativa.
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;

    socket.onopen = () => {
      this.attempt = 0;
      this.store.setConnectivity('open');
    };

    socket.onmessage = (ev: MessageEvent) => {
      const event = parseTelemetryEvent(ev.data);
      if (event) {
        this.store.applyEvent(event);
      }
    };

    socket.onerror = () => {
      // O fechamento subsequente dispara onclose, que agenda a reconexão.
    };

    socket.onclose = () => {
      this.detach(socket);
      if (this.socket === socket) {
        this.socket = null;
      }
      if (this.stopped) {
        return;
      }
      this.store.setConnectivity('closed');
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.reconnectTimer !== null) {
      return;
    }
    const delay = this.nextDelay();
    this.attempt += 1;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.open();
    }, delay);
  }

  /** Calcula o atraso da próxima retentativa com backoff exponencial e jitter. */
  private nextDelay(): number {
    const exponential =
      RECONNECT.baseDelayMs * RECONNECT.factor ** this.attempt;
    const capped = Math.min(exponential, RECONNECT.maxDelayMs);
    // Jitter total (full jitter): uniforme em [0, capped] para evitar thundering herd.
    return Math.random() * capped;
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private detach(socket: WebSocket): void {
    socket.onopen = null;
    socket.onmessage = null;
    socket.onerror = null;
    socket.onclose = null;
  }
}

/** Instância singleton do cliente de telemetria ligada ao store da app. */
export const telemetryClient = new TelemetryClient();
