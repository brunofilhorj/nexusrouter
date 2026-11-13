/**
 * Per-tab connection settings.
 * Host, port and identifiers persist in localStorage. Secrets (Rabbit
 * password, WebSocket token) live only in module memory and vanish on unload
 * (FR-UI-06, NFR-SEC-01). `saveConfig` writes a whitelist of plain fields, so
 * a secret cannot reach storage even by mistake.
 */

/** Tabs of the config modal; they match the transport ids. */
export type ConfigTab = 'kafka' | 'rabbit' | 'fix' | 'ws';

/** Persistable (non-secret) fields per tab, all kept as strings. */
export interface PersistedConfig {
  kafka: { host: string; port: string; topic: string; groupId: string };
  rabbit: { host: string; port: string; exchange: string; routingKey: string; username: string };
  /** `resetSeqNum` and `sendNextExpected` are checkboxes stored as 'true' / 'false'. `replayMode` is empty, `FromLogon` or `StartOfDay`. */
  fix: {
    host: string;
    port: string;
    senderCompId: string;
    targetCompId: string;
    resetSeqNum: string;
    replayMode: string;
    sendNextExpected: string;
  };
  ws: { host: string; port: string; clientId: string };
}

/** Secrets held in memory only. */
export interface SessionSecrets {
  rabbitPassword: string;
  wsToken: string;
}

const STORAGE_KEY = 'clientpanel.config';

/** Defaults follow design §7. WebSocket host and port start empty. */
export const DEFAULT_CONFIG: PersistedConfig = {
  kafka: { host: '127.0.0.1', port: '19092', topic: 'fix.ordexec.raw', groupId: 'probe-local' },
  rabbit: { host: '127.0.0.1', port: '5672', exchange: 'fix.orders', routingKey: 'fix.raw', username: '' },
  fix: {
    host: '127.0.0.1',
    port: '6001',
    senderCompId: 'PROBE',
    targetCompId: 'NEXUSROUTER',
    resetSeqNum: 'false',
    replayMode: '',
    sendNextExpected: 'true',
  },
  ws: { host: '', port: '', clientId: '' },
};

const secrets: SessionSecrets = { rabbitPassword: '', wsToken: '' };

/** Copies the known string keys of `source` over `base`; anything else is ignored. */
function mergeSection<T extends Record<string, string>>(base: T, source: unknown): T {
  const result: Record<string, string> = { ...base };
  if (typeof source === 'object' && source !== null) {
    for (const key of Object.keys(base)) {
      const value = (source as Record<string, unknown>)[key];
      if (typeof value === 'string') {
        result[key] = value;
      }
    }
  }
  return result as T;
}

/** Loads persisted settings over the defaults. */
export function loadConfig(): PersistedConfig {
  let stored: unknown = null;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    stored = raw ? JSON.parse(raw) : null;
  } catch {
    stored = null;
  }
  const source = (typeof stored === 'object' && stored !== null ? stored : {}) as Record<string, unknown>;
  return {
    kafka: mergeSection(DEFAULT_CONFIG.kafka, source.kafka),
    rabbit: mergeSection(DEFAULT_CONFIG.rabbit, source.rabbit),
    fix: mergeSection(DEFAULT_CONFIG.fix, source.fix),
    ws: mergeSection(DEFAULT_CONFIG.ws, source.ws),
  };
}

/** Persists only the whitelisted plain fields. */
export function saveConfig(config: PersistedConfig): void {
  const clean = {
    kafka: mergeSection(DEFAULT_CONFIG.kafka, config.kafka),
    rabbit: mergeSection(DEFAULT_CONFIG.rabbit, config.rabbit),
    fix: mergeSection(DEFAULT_CONFIG.fix, config.fix),
    ws: mergeSection(DEFAULT_CONFIG.ws, config.ws),
  };
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(clean));
  } catch {
    // Storage unavailable: settings last for this page only.
  }
}

/** Current in-memory secrets. */
export function getSecrets(): Readonly<SessionSecrets> {
  return secrets;
}

/** Updates in-memory secrets. Never persisted. */
export function setSecrets(next: Partial<SessionSecrets>): void {
  Object.assign(secrets, next);
}
