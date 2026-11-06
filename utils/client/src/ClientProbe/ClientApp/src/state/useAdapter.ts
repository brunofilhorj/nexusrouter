/**
 * Binds one probe transport to its screen: the adapter snapshot from the
 * store plus connect and disconnect actions that call the API and refresh.
 */
import { useCallback, useState } from 'react';
import { ApiError, connect, disconnect, type AdapterState, type ProbeTransport } from '../api/client';
import type { ConnectionState } from '../components/ConnectionCard';
import { refreshState } from './poll';
import { buildProfile } from './profile';
import { useStore } from './store';

/** Operator-facing label for each connection state. */
const STATE_LABELS: Record<ConnectionState, string> = {
  disconnected: 'Desconectado',
  connecting: 'Conectando',
  live: 'Ao vivo',
  faulted: 'Com falha',
};

/** Normalises the server state name (`Live`, `Faulted`, ...) to a card state. */
function toConnectionState(value: string | undefined): ConnectionState {
  switch (value?.toLowerCase()) {
    case 'connecting':
      return 'connecting';
    case 'live':
      return 'live';
    case 'faulted':
      return 'faulted';
    default:
      return 'disconnected';
  }
}

/** What a screen needs to render and drive one transport. */
export interface AdapterView {
  /** Last server snapshot, or undefined before the first poll. */
  adapter: AdapterState | undefined;
  state: ConnectionState;
  stateLabel: string;
  /** Server error, or the error of the last connect or disconnect request. */
  lastError: string | null;
  /** True while a connect or disconnect request is in flight. */
  busy: boolean;
  onConnect: () => void;
  onDisconnect: () => void;
}

/** Message shown for a failed request. Never includes the request body. */
function describe(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message;
  }
  return 'API indisponível.';
}

/** View model and actions for one transport. */
export function useAdapter(transport: ProbeTransport): AdapterView {
  const { adapters } = useStore();
  const adapter = adapters[transport];
  const [busy, setBusy] = useState(false);
  const [requestError, setRequestError] = useState<string | null>(null);

  const run = useCallback(
    async (action: () => Promise<void>): Promise<void> => {
      setBusy(true);
      setRequestError(null);
      try {
        await action();
      } catch (error) {
        setRequestError(describe(error));
      } finally {
        await refreshState();
        setBusy(false);
      }
    },
    [],
  );

  const onConnect = useCallback(() => {
    void run(() => connect(transport, buildProfile(transport)));
  }, [run, transport]);

  const onDisconnect = useCallback(() => {
    void run(() => disconnect(transport));
  }, [run, transport]);

  const state = toConnectionState(adapter?.state);
  return {
    adapter,
    state,
    stateLabel: STATE_LABELS[state],
    lastError: requestError ?? adapter?.lastError ?? null,
    busy,
    onConnect,
    onDisconnect,
  };
}
