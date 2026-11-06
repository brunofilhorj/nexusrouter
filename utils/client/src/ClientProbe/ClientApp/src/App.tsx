/**
 * ClientPanel shell: topbar, dispatcher menu, and one static screen.
 * The hash (`#/kafka`, `#/rabbit`, `#/fix`, `#/ws`) selects the screen and
 * survives a refresh. An empty hash opens Kafka. No react-router.
 * The gear is the modal trigger; the modal itself arrives in T-05.
 */

import { useEffect, useState, type JSX } from 'react';
import ConnectivityBadge from './components/ConnectivityBadge';
import DispatcherNav from './components/DispatcherNav';
import ThemeToggle from './components/ThemeToggle';
import BrokerScreen from './screens/BrokerScreen';
import FixScreen from './screens/FixScreen';
import { screenFromHash, type ScreenId } from './screens/screens';
import WebSocketScreen from './screens/WebSocketScreen';
import './theme/tokens.css';
import styles from './App.module.css';

/** Reads the screen from the current location hash. */
function currentScreen(): ScreenId {
  return screenFromHash(window.location.hash);
}

function App(): JSX.Element {
  const [screen, setScreen] = useState<ScreenId>(currentScreen);

  useEffect(() => {
    const onHashChange = (): void => {
      setScreen(currentScreen());
    };
    window.addEventListener('hashchange', onHashChange);
    return () => {
      window.removeEventListener('hashchange', onHashChange);
    };
  }, []);

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
    </div>
  );
}

export default App;
