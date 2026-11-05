/**
 * Casca do SimPanel: topbar fixa + dashboard de duas colunas (Seção 4.5).
 *
 * A topbar sempre visível traz o estado corrente da simulação como fonte da
 * verdade, o `ConnectivityBadge` (API/WS) e o `ThemeToggle`. Abaixo, duas
 * colunas: controle à esquerda (`SimulationControls`, `ConfigForm`,
 * `FaultControls`) e observação à direita (`SessionStatus`, `SimulationStatus`
 * e o `MessageFeed`). O tema (claro/escuro) é aplicado ao documento pelo
 * `ThemeToggle`/`useTheme`; aqui a casca apenas importa os design tokens.
 */

import { useEffect } from 'react';
import ConfigForm from './components/ConfigForm';
import ConnectivityBadge from './components/ConnectivityBadge';
import FaultControls from './components/FaultControls';
import MessageFeed from './components/MessageFeed';
import SessionStatus from './components/SessionStatus';
import SimulationControls from './components/SimulationControls';
import SimulationStatus from './components/SimulationStatus';
import ThemeToggle from './components/ThemeToggle';
import { useAppSelector } from './state/useAppStore';
import { appStore } from './state/store';
import { simApi } from './api/client';
import type { SimulationState } from './api/types';
import { telemetryClient } from './ws/telemetry';
import './theme/tokens.css';

const SIM_API = import.meta.env.VITE_SIM_API ?? 'http://localhost:8099';

/** Slug estável do estado da simulação para estilização via `data-state`. */
function stateSlug(state: SimulationState): string {
  return state.toLowerCase();
}

function App(): React.JSX.Element {
  const simulationState = useAppSelector<SimulationState>(
    (s) => s.simulationState,
  );

  useEffect(() => {
    let cancelled = false;
    // Semeia o estado com a verdade do servidor antes/independente de transições
    // (um reload com o backend já em Running cairia em Idle, travando a UI).
    void simApi
      .getState()
      .then((snapshot) => {
        if (!cancelled) {
          appStore.hydrate(snapshot);
        }
      })
      .catch(() => {
        // Backend indisponível na carga: a telemetria hidrata ao reconectar.
      });

    telemetryClient.start();
    return () => {
      cancelled = true;
      telemetryClient.stop();
    };
  }, []);

  return (
    <div className="app">
      <header className="app__topbar">
        <div className="app__brand">
          <h1 className="app__title">SimPanel</h1>
          <p className="app__subtitle">
            DropCopy Simulator — <code>{SIM_API}</code>
          </p>
        </div>
        <span
          className="app__sim-state"
          data-state={stateSlug(simulationState)}
          role="status"
          aria-label={`Estado da simulação: ${simulationState}`}
        >
          {simulationState}
        </span>
        <ConnectivityBadge />
        <ThemeToggle />
      </header>

      <div className="app__body">
        <div className="app__column app__column--control">
          <SimulationControls />
          <ConfigForm />
          <FaultControls />
        </div>
        <div className="app__column app__column--observe">
          <SessionStatus />
          <SimulationStatus />
          <MessageFeed />
        </div>
      </div>
    </div>
  );
}

export default App;
