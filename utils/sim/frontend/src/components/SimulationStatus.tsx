/**
 * Painel de status da simulação (FR-UI-03).
 *
 * Exibe, em tempo real, o estado da FSM de controle (Idle/Running/Paused), o
 * total de mensagens enviadas, a taxa efetiva observada (msg/s), a contagem por
 * `ExecType`, o número de erros e o tempo decorrido da rodada. O estado da FSM
 * vem do último `SimulationStateChanged` e os contadores do último
 * `CountersSnapshot`, ambos empurrados pela telemetria via store (Seção 4.3).
 */

import { useAppSelector } from '../state/useAppStore';
import type {
  ConnectivityStatus,
  CountersView,
} from '../state/store';
import type { SimulationState } from '../api/types';

/** Formata uma duração em milissegundos como `HH:MM:SS`. */
function formatElapsed(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const pad = (n: number): string => n.toString().padStart(2, '0');
  return `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
}

/** Formata a taxa observada com uma casa decimal. */
function formatRate(ratePerSec: number): string {
  return `${ratePerSec.toFixed(1)} msg/s`;
}

/** Ordena as entradas de contagem por ExecType pelo caractere da tag 150. */
function sortedExecTypes(
  byExecType: Record<string, number>,
): Array<[string, number]> {
  return Object.entries(byExecType).sort(([a], [b]) => a.localeCompare(b));
}

/** Slug estável do estado da simulação para estilização via `data-state`. */
function stateSlug(state: SimulationState): string {
  return state.toLowerCase();
}

/** Painel de telemetria da simulação. */
export function SimulationStatus(): React.JSX.Element {
  const simulationState = useAppSelector<SimulationState>(
    (s) => s.simulationState,
  );
  const reason = useAppSelector<string | null>((s) => s.simulationReason);
  const counters = useAppSelector<CountersView | null>((s) => s.counters);
  const connectivity = useAppSelector<ConnectivityStatus>(
    (s) => s.connectivity,
  );

  const execTypes = counters ? sortedExecTypes(counters.byExecType) : [];

  return (
    <section
      className="simulation-status"
      aria-labelledby="simulation-status-heading"
    >
      <h2 id="simulation-status-heading">Simulação</h2>

      <dl className="simulation-status__grid">
        <div className="simulation-status__row">
          <dt>Estado</dt>
          <dd data-state={stateSlug(simulationState)}>
            {simulationState}
            {reason ? <span className="simulation-status__reason"> ({reason})</span> : null}
          </dd>
        </div>
        <div className="simulation-status__row">
          <dt>Total enviado</dt>
          <dd>{counters ? counters.totalSent : '—'}</dd>
        </div>
        <div className="simulation-status__row">
          <dt>Taxa efetiva</dt>
          <dd>{counters ? formatRate(counters.ratePerSec) : '—'}</dd>
        </div>
        <div className="simulation-status__row">
          <dt>Erros</dt>
          <dd data-errors={counters && counters.errors > 0 ? 'yes' : 'no'}>
            {counters ? counters.errors : '—'}
          </dd>
        </div>
        <div className="simulation-status__row">
          <dt>Tempo decorrido</dt>
          <dd>{counters ? formatElapsed(counters.elapsedMs) : '—'}</dd>
        </div>
      </dl>

      <div className="simulation-status__exectypes">
        <h3>Por ExecType</h3>
        {counters === null ? (
          <p className="simulation-status__empty">
            {connectivity === 'open'
              ? 'Aguardando contadores…'
              : 'Telemetria offline.'}
          </p>
        ) : execTypes.length === 0 ? (
          <p className="simulation-status__empty">Nenhuma mensagem ainda.</p>
        ) : (
          <ul className="simulation-status__exectype-list">
            {execTypes.map(([execType, count]) => (
              <li key={execType} className="simulation-status__exectype">
                <span className="simulation-status__exectype-key">{execType}</span>
                <span className="simulation-status__exectype-count">{count}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

export default SimulationStatus;
