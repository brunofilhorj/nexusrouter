/**
 * Painel de status da sessão FIX (FR-UI-02).
 *
 * Exibe, em tempo real, o estado da sessão do acceptor (Disconnected/Logon/
 * Active/Logout), os `SenderCompID`/`TargetCompID` negociados, o `MsgSeqNum`
 * corrente e o horário do último heartbeat. Lê a `SessionView` empurrada pela
 * telemetria via store (Seção 4.3 do design) e não faz nenhuma chamada própria.
 */

import { useAppSelector } from '../state/useAppStore';
import type { SessionView } from '../state/store';

/** Formata um instante ISO 8601 (UTC) para exibição local; tolera valores ausentes/inválidos. */
function formatTimestamp(iso: string | null): string {
  if (!iso) {
    return '—';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  return date.toLocaleString();
}

/**
 * Normaliza o nome do estado da sessão para um slug estável usado no
 * `data-state`, permitindo estilização por estado sem acoplar a textos livres.
 */
function stateSlug(state: string): string {
  return state.trim().toLowerCase().replace(/\s+/g, '-');
}

/** Painel de telemetria da sessão FIX. */
export function SessionStatus(): React.JSX.Element {
  const session = useAppSelector<SessionView | null>((s) => s.session);

  return (
    <section className="session-status" aria-labelledby="session-status-heading">
      <h2 id="session-status-heading">Sessão FIX</h2>
      {session === null ? (
        <p className="session-status__empty">Aguardando telemetria da sessão…</p>
      ) : (
        <dl className="session-status__grid">
          <div className="session-status__row">
            <dt>Estado</dt>
            <dd data-state={stateSlug(session.state)}>{session.state}</dd>
          </div>
          <div className="session-status__row">
            <dt>SenderCompID</dt>
            <dd>{session.senderCompId}</dd>
          </div>
          <div className="session-status__row">
            <dt>TargetCompID</dt>
            <dd>{session.targetCompId}</dd>
          </div>
          <div className="session-status__row">
            <dt>MsgSeqNum</dt>
            <dd>{session.msgSeqNum}</dd>
          </div>
          <div className="session-status__row">
            <dt>Último heartbeat</dt>
            <dd>{formatTimestamp(session.lastHeartbeatUtc)}</dd>
          </div>
        </dl>
      )}
    </section>
  );
}

export default SessionStatus;
