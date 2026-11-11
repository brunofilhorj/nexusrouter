/**
 * Configuration modal with Kafka, Rabbit, FIX and WebSocket tabs (FR-UI-06).
 * It is a side drawer anchored to the right with no backdrop, so the topbar
 * and the menu stay usable while it is open. Plain fields persist to
 * localStorage; password and token stay in memory only.
 */
import { useId, useState, type ChangeEvent, type JSX } from 'react';
import {
  getSecrets,
  loadConfig,
  saveConfig,
  setSecrets,
  type ConfigTab,
  type PersistedConfig,
} from '../state/config';
import styles from './ConfigModal.module.css';

/** Props for the config modal. */
export interface ConfigModalProps {
  onClose: () => void;
}

interface FieldDef {
  key: string;
  label: string;
  secret?: boolean;
  /** Checkbox stored as 'true' / 'false'. */
  checkbox?: boolean;
}

const TABS: readonly { id: ConfigTab; label: string }[] = [
  { id: 'kafka', label: 'Kafka' },
  { id: 'rabbit', label: 'Rabbit' },
  { id: 'fix', label: 'FIX' },
  { id: 'ws', label: 'WebSocket' },
];

const FIELDS: Record<ConfigTab, readonly FieldDef[]> = {
  kafka: [
    { key: 'host', label: 'Host' },
    { key: 'port', label: 'Porta' },
    { key: 'topic', label: 'Tópico' },
    { key: 'groupId', label: 'Grupo' },
  ],
  rabbit: [
    { key: 'host', label: 'Host' },
    { key: 'port', label: 'Porta' },
    { key: 'exchange', label: 'Exchange' },
    { key: 'routingKey', label: 'Routing key' },
    { key: 'username', label: 'Usuário (vazio usa RABBITMQ_DEFAULT_USER)' },
    { key: 'password', label: 'Senha (só nesta sessão)', secret: true },
  ],
  fix: [
    { key: 'host', label: 'Host' },
    { key: 'port', label: 'Porta' },
    { key: 'senderCompId', label: 'SenderCompID' },
    { key: 'targetCompId', label: 'TargetCompID' },
    { key: 'resetSeqNum', label: 'Zerar sequência no próximo Logon (141=Y)', checkbox: true },
    { key: 'sendNextExpected', label: 'Enviar NextExpectedMsgSeqNum (789)', checkbox: true },
  ],
  ws: [
    { key: 'host', label: 'Host' },
    { key: 'port', label: 'Porta' },
    { key: 'clientId', label: 'clientId' },
    { key: 'token', label: 'Token (só nesta sessão)', secret: true },
  ],
};

/** Tabbed settings panel. Closes with the button or Escape. */
export function ConfigModal({ onClose }: ConfigModalProps): JSX.Element {
  const baseId = useId();
  const [tab, setTab] = useState<ConfigTab>('kafka');
  const [config, setConfig] = useState<PersistedConfig>(loadConfig);
  const [secrets, setSecretState] = useState(() => ({ ...getSecrets() }));

  const valueOf = (def: FieldDef): string => {
    if (def.secret) {
      return tab === 'rabbit' ? secrets.rabbitPassword : secrets.wsToken;
    }
    return (config[tab] as Record<string, string>)[def.key] ?? '';
  };

  const onChange = (def: FieldDef) => (event: ChangeEvent<HTMLInputElement>) => {
    const value = def.checkbox ? String(event.target.checked) : event.target.value;
    if (def.secret) {
      const next = tab === 'rabbit' ? { rabbitPassword: value } : { wsToken: value };
      setSecrets(next);
      setSecretState((current) => ({ ...current, ...next }));
      return;
    }
    const next: PersistedConfig = { ...config, [tab]: { ...config[tab], [def.key]: value } };
    setConfig(next);
    saveConfig(next);
  };

  return (
    <aside
      className={styles.modal}
      role="dialog"
      aria-labelledby={`${baseId}-title`}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          onClose();
        }
      }}
    >
      <header className={styles.header}>
        <h2 id={`${baseId}-title`} className={styles.title}>
          Configuração
        </h2>
        <button type="button" className={styles.close} aria-label="Fechar configuração" onClick={onClose}>
          <span aria-hidden="true">×</span>
        </button>
      </header>
      <div role="tablist" aria-label="Transportes" className={styles.tabs}>
        {TABS.map((item) => (
          <button
            key={item.id}
            type="button"
            role="tab"
            id={`${baseId}-tab-${item.id}`}
            aria-selected={tab === item.id}
            aria-controls={`${baseId}-panel`}
            className={styles.tab}
            onClick={() => {
              setTab(item.id);
            }}
          >
            {item.label}
          </button>
        ))}
      </div>
      <div
        role="tabpanel"
        id={`${baseId}-panel`}
        aria-labelledby={`${baseId}-tab-${tab}`}
        className={styles.panel}
      >
        {FIELDS[tab].map((def) =>
          def.checkbox ? (
            <label key={def.key} className={styles.check}>
              <input type="checkbox" checked={valueOf(def) === 'true'} onChange={onChange(def)} />
              <span>{def.label}</span>
            </label>
          ) : (
            <label key={def.key} className={styles.field}>
              <span>{def.label}</span>
              <input
                type={def.secret ? 'password' : 'text'}
                autoComplete={def.secret ? 'off' : undefined}
                value={valueOf(def)}
                onChange={onChange(def)}
              />
            </label>
          ),
        )}
      </div>
    </aside>
  );
}

export default ConfigModal;
