/**
 * FIX screen. The static endpoint text (FR-UI-10) shows until the initiator
 * reports its effective endpoint. Session identity and sequence numbers
 * appear while a session exists (FR-UI-09).
 */

import type { JSX } from 'react';
import ConnectionCard from '../components/ConnectionCard';
import EventTable from '../components/EventTable';
import StatsRow from '../components/StatsRow';
import { useStore } from '../state/store';
import { useAdapter } from '../state/useAdapter';
import { lastEventLabel } from './format';
import styles from './Screen.module.css';

/** FIX initiator defaults from the Host appsettings. */
const FIX_ENDPOINT = '127.0.0.1:6001 · Target NEXUSROUTER';

/** FIX screen: connection card, session line, counters and the event table. */
export function FixScreen(): JSX.Element {
  const view = useAdapter('fix');
  const { buffers } = useStore();
  const counters = view.adapter?.counters;
  const session = view.adapter?.session;
  const endpoint = view.adapter?.endpoint ? view.adapter.endpoint : FIX_ENDPOINT;

  return (
    <section className={styles.screen} aria-labelledby="fix-screen-title">
      <h2 id="fix-screen-title" className={styles.title}>
        FIX
      </h2>
      <ConnectionCard
        endpoint={endpoint}
        state={view.state}
        stateLabel={view.stateLabel}
        lastError={view.lastError}
        busy={view.busy}
        onConnect={view.onConnect}
        onDisconnect={view.onDisconnect}
      />
      {session ? (
        <p className={styles.session}>
          Sessão {session.senderCompId} → {session.targetCompId} · MsgSeqNum enviado {session.msgSeqNum} · último
          recebido {session.lastReceivedSeqNum} · próximo esperado {session.nextExpectedSeqNum}
          {session.gapFrom > 0 ? ` · aguardando reenvio desde ${session.gapFrom}` : ''}
        </p>
      ) : null}
      <StatsRow
        received={counters?.received}
        perSecond={counters ? Math.round(counters.ratePerSecond * 10) / 10 : undefined}
        lastEvent={lastEventLabel(counters?.lastReceivedAt)}
        parseFailures={counters?.parseFailures}
        sampled={counters?.sampled}
        dropped={counters?.dropped}
      />
      <EventTable events={buffers.fix} />
    </section>
  );
}

export default FixScreen;
