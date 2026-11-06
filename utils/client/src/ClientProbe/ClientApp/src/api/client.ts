/**
 * HTTP client for the probe API. Every URL is relative to the page origin
 * (`/v1/...`), so there is no base URL and no CORS.
 */

/** Transports handled by the process through `/v1/connect`. */
export type ProbeTransport = 'kafka' | 'rabbit' | 'fix';

/** Counters reported for one adapter. */
export interface CounterState {
  received: number;
  ratePerSecond: number;
  lastReceivedAt: string | null;
  parseFailures: number;
  sampled: number;
  dropped: number;
}

/** One adapter row in `GET /v1/state`. */
export interface AdapterState {
  transport: ProbeTransport;
  state: string;
  endpoint: string;
  lastError: string | null;
  counters: CounterState;
}

/** Snapshot of the three adapters. */
export interface ProbeState {
  adapters: AdapterState[];
}

/** Profile sent to `/v1/connect`. Secrets travel only here, never to storage. */
export type ProbeProfile = Record<string, string | number>;

/** Error raised for a non-2xx answer. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

/** Converts a failed response into an `ApiError`, reading `{ code, message }` when present. */
async function toApiError(response: Response): Promise<ApiError> {
  let code = 'http_error';
  let message = `HTTP ${response.status}`;
  try {
    const body = (await response.json()) as { code?: unknown; message?: unknown };
    if (typeof body.code === 'string') {
      code = body.code;
    }
    if (typeof body.message === 'string') {
      message = body.message;
    }
  } catch {
    // Body is not JSON; keep the generic message.
  }
  return new ApiError(response.status, code, message);
}

/** Sends a request and returns the response, throwing `ApiError` on failure. */
async function send(path: string, init?: RequestInit): Promise<Response> {
  const response = await fetch(path, init);
  if (!response.ok) {
    throw await toApiError(response);
  }
  return response;
}

/** Posts a JSON body. */
function postJson(path: string, body: unknown): Promise<Response> {
  return send(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** `GET /v1/state`. */
export async function getState(): Promise<ProbeState> {
  const response = await send('/v1/state');
  return (await response.json()) as ProbeState;
}

/** `POST /v1/connect`. HTTP 400 for an invalid profile, 409 while connecting. */
export async function connect(transport: ProbeTransport, profile: ProbeProfile): Promise<void> {
  await postJson('/v1/connect', { transport, profile });
}

/** `POST /v1/disconnect`. */
export async function disconnect(transport: ProbeTransport): Promise<void> {
  await postJson('/v1/disconnect', { transport });
}
