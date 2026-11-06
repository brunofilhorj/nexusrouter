/**
 * ClientPanel shell: topbar, dispatcher menu, and one static screen.
 * The hash (`#/kafka`, `#/rabbit`, `#/fix`, `#/ws`) selects the screen and
 * survives a refresh. An empty hash opens Kafka. No react-router.
 * The gear is the modal trigger; the modal is a side drawer, not an overlay.
 */

import { useEffect, useState, type JSX } from 'react';
import ConfigModal from './components/ConfigModal';
import ConnectivityBadge from './components/ConnectivityBadge';
import DispatcherNav from './components/DispatcherNav';
import ThemeToggle from './components/ThemeToggle';
import BrokerScreen from './screens/BrokerScreen';
import FixScreen from './screens/FixScreen';
import { screenFromHash, type ScreenId } from './screens/screens';
import WebSocketScreen from './screens/WebSocketScreen';
import { startStatePolling } from './state/poll';
import { setScreen, useStore } from './state/store';
import { startTelemetry } from './ws/telemetry';
import './theme/tokens.css';
import styles from './App.module.css';

/** Reads the screen from the current location hash. */
function currentScreen(): ScreenId {
  return screenFromHash(window.location.hash);
}

function App(): JSX.Element {
  const { screen } = useStore();
  const [configOpen, setConfigOpen] = useState(false);

  useEffect(() => {
    setScreen(currentScreen());
    const onHashChange = (): void => {
      setScreen(currentScreen());
    };
    window.addEventListener('hashchange', onHashChange);
    return () => {
      window.removeEventListener('hashchange', onHashChange);
    };
  }, []);

  useEffect(() => startTelemetry(), []);
  useEffect(() => startStatePolling(), []);

  return (
    <div className={styles.app}>
      <header className={styles.topbar}>
        <div className={styles.brand}>
          <h1 className={styles.title}>ClientPanel</h1>
          <p className={styles.subtitle}>
            Prova de saída do NexusRouter — localhost:8100
          </p>
        </div>
        <ConnectivityBadge />
        <button
          type="button"
          className={styles.gear}
          aria-label="Abrir configuração"
          title="Configuração"
          aria-expanded={configOpen}
          onClick={() => {
            setConfigOpen((open) => !open);
          }}
        >
          <span aria-hidden="true">⚙️</span>
        </button>
        <ThemeToggle />
      </header>
      <div className={styles.body}>
        <DispatcherNav current={screen} />
        <main className={styles.main}>
          {screen === 'kafka' ? <BrokerScreen transport="kafka" /> : null}
          {screen === 'rabbit' ? <BrokerScreen transport="rabbit" /> : null}
          {screen === 'fix' ? <FixScreen /> : null}
          {screen === 'ws' ? <WebSocketScreen /> : null}
        </main>
      </div>
      {configOpen ? (
        <ConfigModal
          onClose={() => {
            setConfigOpen(false);
          }}
        />
      ) : null}
    </div>
  );
}

export default App;
