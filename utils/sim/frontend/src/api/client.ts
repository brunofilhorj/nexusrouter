/**
 * Cliente HTTP tipado do SimCore.
 *
 * Fino wrapper de `fetch` que resolve a base URL via `VITE_SIM_API`
 * (default http://localhost:8099), injeta `Authorization: Bearer <token>` quando
 * `VITE_SIM_TOKEN` estiver definido (somente dev) e traduz o corpo de erro
 * estruturado `{ code, message }` da API (FR-API-05) em exceções tipadas.
 */

import type {
  ApiError,
  ForceLogoutRequest,
  LogonMode,
  LogonModeRequest,
  SequenceGapRequest,
  SimulationConfig,
  SimulationConfigRequest,
  StateResponse,
} from './types';

/** Base URL do SimCore (API + WebSocket). Default usado quando a env var está ausente. */
export const SIM_API_BASE_URL: string =
  import.meta.env.VITE_SIM_API ?? 'http://localhost:8099';

/** Token Bearer opcional (somente dev); vazio/ausente desativa o header Authorization. */
const SIM_TOKEN: string | undefined = import.meta.env.VITE_SIM_TOKEN || undefined;

/**
 * Erro lançado quando a API responde com status não-2xx.
 *
 * Carrega o status HTTP e, quando o corpo for o erro estruturado da API, o
 * `code` estável e a `message` legível.
 */
export class SimApiError extends Error {
  /** Status HTTP da resposta. */
  readonly status: number;
  /** Código estável da categoria de erro, quando o corpo foi parseável. */
  readonly code: string | undefined;

  constructor(status: number, code: string | undefined, message: string) {
    super(message);
    this.name = 'SimApiError';
    this.status = status;
    this.code = code;
  }
}

/** Monta uma URL absoluta a partir de um caminho relativo da API. */
function resolveUrl(path: string): string {
  const base = SIM_API_BASE_URL.replace(/\/+$/, '');
  return `${base}${path}`;
}

/** Monta os headers da requisição, incluindo Authorization quando houver token. */
function buildHeaders(hasBody: boolean): Headers {
  const headers = new Headers();
  headers.set('Accept', 'application/json');
  if (hasBody) {
    headers.set('Content-Type', 'application/json');
  }
  if (SIM_TOKEN) {
    headers.set('Authorization', `Bearer ${SIM_TOKEN}`);
  }
  return headers;
}

/** Extrai um `ApiError` do corpo da resposta, tolerando corpos vazios ou não-JSON. */
async function parseError(response: Response): Promise<SimApiError> {
  let code: string | undefined;
  let message = `HTTP ${response.status} ${response.statusText}`.trim();

  try {
    const text = await response.text();
    if (text) {
      const body = JSON.parse(text) as Partial<ApiError>;
      if (typeof body.code === 'string') {
        code = body.code;
      }
      if (typeof body.message === 'string' && body.message.length > 0) {
        message = body.message;
      }
    }
  } catch {
    // Corpo ausente ou não-JSON: mantém a mensagem derivada do status.
  }

  return new SimApiError(response.status, code, message);
}

/**
 * Executa uma requisição e devolve o corpo JSON parseado como `T`.
 *
 * @throws {SimApiError} quando a resposta tem status não-2xx.
 */
async function requestJson<T>(
  method: string,
  path: string,
  body?: unknown,
): Promise<T> {
  const hasBody = body !== undefined;
  const response = await fetch(resolveUrl(path), {
    method,
    headers: buildHeaders(hasBody),
    body: hasBody ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    throw await parseError(response);
  }

  return (await response.json()) as T;
}

/**
 * Executa um comando que não retorna corpo (204 No Content).
 *
 * @throws {SimApiError} quando a resposta tem status não-2xx.
 */
async function requestVoid(
  method: string,
  path: string,
  body?: unknown,
): Promise<void> {
  const hasBody = body !== undefined;
  const response = await fetch(resolveUrl(path), {
    method,
    headers: buildHeaders(hasBody),
    body: hasBody ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    throw await parseError(response);
  }
}

/**
 * Chamadas tipadas aos endpoints do SimCore: estado, controle da simulação,
 * configuração e fault-injection.
 */
export const simApi = {
  /** GET /v1/state — snapshot consolidado da sessão FIX, simulação e config. */
  getState(): Promise<StateResponse> {
    return requestJson<StateResponse>('GET', '/v1/state');
  },

  /** POST /v1/sim/start — inicia a rodada, opcionalmente aplicando um delta de config. */
  start(config?: SimulationConfigRequest): Promise<void> {
    return requestVoid('POST', '/v1/sim/start', config);
  },

  /** POST /v1/sim/pause — pausa a rodada. */
  pause(): Promise<void> {
    return requestVoid('POST', '/v1/sim/pause');
  },

  /** POST /v1/sim/resume — retoma a rodada pausada. */
  resume(): Promise<void> {
    return requestVoid('POST', '/v1/sim/resume');
  },

  /** POST /v1/sim/stop — para a rodada e zera o estado. */
  stop(): Promise<void> {
    return requestVoid('POST', '/v1/sim/stop');
  },

  /** PUT /v1/config — atualiza a SimulationConfig e retorna a config vigente. */
  updateConfig(config: SimulationConfigRequest): Promise<SimulationConfig> {
    return requestJson<SimulationConfig>('PUT', '/v1/config', config);
  },

  /** POST /v1/fault/drop — derruba a conexão FIX ativa sem Logout. */
  faultDrop(): Promise<void> {
    return requestVoid('POST', '/v1/fault/drop');
  },

  /** POST /v1/fault/logout — força um Logout com o Text informado. */
  faultLogout(text: string): Promise<void> {
    const body: ForceLogoutRequest = { text };
    return requestVoid('POST', '/v1/fault/logout', body);
  },

  /** POST /v1/fault/logon-mode — define como o próximo Logon do cliente é respondido. */
  faultLogonMode(mode: LogonMode): Promise<void> {
    const body: LogonModeRequest = { mode };
    return requestVoid('POST', '/v1/fault/logon-mode', body);
  },

  /** POST /v1/fault/seq-gap — injeta um gap pulando `skip` sequence numbers de saída. */
  faultSeqGap(skip: number): Promise<void> {
    const body: SequenceGapRequest = { skip };
    return requestVoid('POST', '/v1/fault/seq-gap', body);
  },
};
