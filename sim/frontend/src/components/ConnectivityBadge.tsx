/**
 * Indicador de conectividade com o SimCore (FR-UI-08).
 *
 * Mostra dois estados independentes: o da API HTTP e o do feed WebSocket.
 *
 * - WS: lido diretamente do store (`connectivity`), empurrado pelo
 *   `TelemetryClient`, que já reconecta automaticamente com backoff.
 * - API: sondada periodicamente por um `GET /v1/state` leve; um erro de rede
 *   marca a API como down, enquanto um erro estruturado da API (SimApiError)
 *   ainda conta como "up" — o servidor respondeu.
 *
 * O componente não altera o store; apenas lê o WS e mantém seu próprio estado
 * local da sonda de API.
 */

import { useEffect, useState } from 'react';
import { useAppSelector } from '../state/useAppStore';
import type { ConnectivityStatus } from '../state/store';
import { SimApiError, simApi } from '../api/client';

/** Intervalo entre sondagens de saúde da API, em ms. */
const API_PROBE_INTERVAL_MS = 5_000;

/** Estado da sonda de saúde da API HTTP. */
type ApiHealth = 'unknown' | 'up' | 'down';

/** Rótulo e slug (para `data-status`) do indicador de API. */
function apiIndicator(health: ApiHealth): { label: string; status: string } {
  switch (health) {
    case 'up':
      return { label: 'API online', status: 'up' };
    case 'down':
      return { label: 'API offline', status: 'down' };
    default:
      return { label: 'API…', status: 'unknown' };
  }
}

/** Rótulo e slug (para `data-status`) do indicador de WebSocket. */
function wsIndicator(connectivity: ConnectivityStatus): {
  label: string;
  status: string;
} {
  switch (connectivity) {
    case 'open':
      return { label: 'WS online', status: 'up' };
    case 'connecting':
      return { label: 'WS conectando…', status: 'connecting' };
    default:
      return { label: 'WS offline', status: 'down' };
  }
}

/** Badge de conectividade API + WebSocket. */
export function ConnectivityBadge(): React.JSX.Element {
  const connectivity = useAppSelector<ConnectivityStatus>(
    (s) => s.connectivity,
  );
  const [apiHealth, setApiHealth] = useState<ApiHealth>('unknown');

  useEffect(() => {
    let cancelled = false;

    async function probe(): Promise<void> {
      try {
        await simApi.getState();
        if (!cancelled) {
          setApiHealth('up');
        }
      } catch (error) {
        if (cancelled) {
          return;
        }
        // O servidor respondeu (mesmo com erro estruturado): API está de pé.
        // Falha de rede/fetch: API indisponível.
        setApiHealth(error instanceof SimApiError ? 'up' : 'down');
      }
    }

    void probe();
    const timer = setInterval(() => void probe(), API_PROBE_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);

  const api = apiIndicator(apiHealth);
  const ws = wsIndicator(connectivity);

  return (
    <section
      className="connectivity-badge"
      aria-labelledby="connectivity-badge-heading"
    >
      <h2 id="connectivity-badge-heading">Conectividade</h2>
      <ul className="connectivity-badge__list">
        <li
          className="connectivity-badge__item"
          data-channel="api"
          data-status={api.status}
        >
          <span className="connectivity-badge__dot" aria-hidden="true" />
          <span className="connectivity-badge__label">{api.label}</span>
        </li>
        <li
          className="connectivity-badge__item"
          data-channel="ws"
          data-status={ws.status}
        >
          <span className="connectivity-badge__dot" aria-hidden="true" />
          <span className="connectivity-badge__label">{ws.label}</span>
        </li>
      </ul>
    </section>
  );
}

export default ConnectivityBadge;
