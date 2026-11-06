/**
 * Controles de injeção de falha (FR-UI-06, FR-FAULT-01 a FR-FAULT-04).
 *
 * Zona perigosa do SimPanel: concentra os comandos que degradam deliberadamente
 * a sessão FIX para exercitar a recuperação do NexusRouter. Expõe:
 *
 * - **Drop Connection** (FR-FAULT-01): derruba o socket TCP sem Logout, para
 *   testar a reconexão com backoff exponencial do cliente.
 * - **Force Logout** (FR-FAULT-02): envia `Logout` (35=5) com `Text` (58)
 *   escolhido entre os cenários da `b3-guidelines.md` §13 — "MsgSeqNum too low"
 *   e "Password will expire" — ou um texto livre (custom).
 * - **Logon response mode** (FR-FAULT-03): define como o acceptor responde ao
 *   próximo `Logon` do cliente — `Success`, `InvalidCredentials` (6930=1) ou
 *   `NotAuthorized` (6930=2). É uma configuração do próximo handshake, aplicada
 *   diretamente (não é uma injeção pontual).
 * - **Sequence Gap** (FR-FAULT-04): pula N `MsgSeqNum` de saída para forçar um
 *   `ResendRequest` do cliente.
 *
 * Conforme a Seção 4.5 do design, toda injeção de falha de risco assimétrico
 * (Drop Connection, Force Logout, Sequence Gap) passa pelo `ConfirmDialog`, que
 * nomeia a ação e seu efeito antes de disparar. O resultado se reflete na
 * telemetria (transições de sessão, `SimulationStateChanged`), não exigindo
 * feedback de sucesso próprio; erros da API aparecem como aviso discreto.
 */

import { useCallback, useState } from 'react';
import { simApi, SimApiError } from '../api/client';
import ConfirmDialog from './ConfirmDialog';
import type { LogonMode } from '../api/types';

/** Cenários predefinidos de `Text` (58) do Logout forçado (b3-guidelines §13). */
const LOGOUT_SCENARIOS = [
  { id: 'seq-too-low', label: 'MsgSeqNum too low', text: 'MsgSeqNum too low' },
  {
    id: 'password-expire',
    label: 'Password will expire',
    text: 'Password will expire',
  },
  { id: 'custom', label: 'Custom (texto livre)', text: '' },
] as const;

/** Identificador do cenário de Logout selecionado. */
type LogoutScenarioId = (typeof LOGOUT_SCENARIOS)[number]['id'];

/** Opções do modo de resposta ao próximo Logon do cliente (FR-FAULT-03). */
const LOGON_MODES: ReadonlyArray<{ value: LogonMode; label: string }> = [
  { value: 'Success', label: 'Success (aceita o Logon)' },
  { value: 'InvalidCredentials', label: 'InvalidCredentials (6930 = 1)' },
  { value: 'NotAuthorized', label: 'NotAuthorized (6930 = 2)' },
];

/** Qual injeção de falha está sendo confirmada no modal, ou `null`. */
type PendingFault = 'drop' | 'logout' | 'seq-gap' | null;

/** Extrai uma mensagem legível de um erro arbitrário lançado pela API. */
function errorMessage(error: unknown): string {
  if (error instanceof SimApiError) {
    return error.message;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'Erro inesperado ao injetar a falha.';
}

/** Resolve o `Text` (58) efetivo do Logout a partir do cenário e do texto livre. */
function resolveLogoutText(scenario: LogoutScenarioId, customText: string): string {
  if (scenario === 'custom') {
    return customText.trim();
  }
  const found = LOGOUT_SCENARIOS.find((s) => s.id === scenario);
  return found ? found.text : '';
}

/** Painel de injeção de falha da sessão FIX. */
export function FaultControls(): React.JSX.Element {
  // Comando em curso: desabilita os disparos para evitar duplo envio.
  const [pending, setPending] = useState(false);
  // Última falha de comando, exibida como aviso discreto (não-modal).
  const [error, setError] = useState<string | null>(null);
  // Injeção aguardando confirmação no modal (Seção 4.5).
  const [confirming, setConfirming] = useState<PendingFault>(null);

  // Estado do Force Logout.
  const [logoutScenario, setLogoutScenario] =
    useState<LogoutScenarioId>('seq-too-low');
  const [customText, setCustomText] = useState('');

  // Estado do seletor de modo de Logon.
  const [logonMode, setLogonMode] = useState<LogonMode>('Success');
  const [logonModeSaving, setLogonModeSaving] = useState(false);

  // Estado do Sequence Gap.
  const [skip, setSkip] = useState(1);

  /** Executa um comando da API, tratando pending e erro de forma uniforme. */
  const run = useCallback(
    async (command: () => Promise<void>): Promise<void> => {
      setPending(true);
      setError(null);
      try {
        await command();
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        setPending(false);
      }
    },
    [],
  );

  // --- Logon mode: aplicado diretamente, sem modal (não é injeção pontual). ---
  const onLogonModeChange = useCallback(
    (mode: LogonMode): void => {
      setLogonMode(mode);
      setLogonModeSaving(true);
      setError(null);
      void (async () => {
        try {
          await simApi.faultLogonMode(mode);
        } catch (err) {
          setError(errorMessage(err));
        } finally {
          setLogonModeSaving(false);
        }
      })();
    },
    [],
  );

  // --- Abertura do modal de confirmação por tipo de injeção. ---
  const onDropRequested = useCallback(() => setConfirming('drop'), []);
  const onLogoutRequested = useCallback(() => setConfirming('logout'), []);
  const onSeqGapRequested = useCallback(() => setConfirming('seq-gap'), []);
  const onCancel = useCallback(() => setConfirming(null), []);

  // --- Confirmação: dispara a injeção e fecha o modal. ---
  const onConfirm = useCallback(() => {
    const fault = confirming;
    setConfirming(null);
    switch (fault) {
      case 'drop':
        void run(() => simApi.faultDrop());
        break;
      case 'logout': {
        const text = resolveLogoutText(logoutScenario, customText);
        void run(() => simApi.faultLogout(text));
        break;
      }
      case 'seq-gap':
        void run(() => simApi.faultSeqGap(skip));
        break;
      default:
        break;
    }
  }, [confirming, run, logoutScenario, customText, skip]);

  const resolvedLogoutText = resolveLogoutText(logoutScenario, customText);
  const logoutTextEmpty = resolvedLogoutText.length === 0;
  const skipInvalid = !Number.isInteger(skip) || skip < 1;

  return (
    <section
      className="fault-controls"
      aria-labelledby="fault-controls-heading"
      data-danger="true"
    >
      <h2 id="fault-controls-heading">Injeção de falha</h2>
      <p className="fault-controls__warning" role="note">
        Zona perigosa: estes comandos degradam a sessão FIX ativa para testar a
        recuperação do cliente. Cada injeção exige confirmação.
      </p>

      {/* Drop Connection (FR-FAULT-01). */}
      <div className="fault-controls__group">
        <h3 className="fault-controls__group-title">Drop Connection</h3>
        <p className="fault-controls__group-desc">
          Fecha o socket TCP sem enviar Logout. Exercita a reconexão com backoff.
        </p>
        <button
          type="button"
          className="fault-controls__button"
          data-variant="danger"
          data-action="drop"
          onClick={onDropRequested}
          disabled={pending}
        >
          Drop Connection
        </button>
      </div>

      {/* Force Logout (FR-FAULT-02). */}
      <div className="fault-controls__group">
        <h3 className="fault-controls__group-title">Force Logout</h3>
        <label className="fault-controls__field">
          Cenário (Text / tag 58)
          <select
            value={logoutScenario}
            onChange={(e) =>
              setLogoutScenario(e.target.value as LogoutScenarioId)
            }
          >
            {LOGOUT_SCENARIOS.map((scenario) => (
              <option key={scenario.id} value={scenario.id}>
                {scenario.label}
              </option>
            ))}
          </select>
        </label>
        {logoutScenario === 'custom' ? (
          <label className="fault-controls__field">
            Texto do Logout
            <input
              type="text"
              value={customText}
              onChange={(e) => setCustomText(e.target.value)}
              placeholder="Motivo do Logout"
            />
          </label>
        ) : null}
        <button
          type="button"
          className="fault-controls__button"
          data-variant="danger"
          data-action="logout"
          onClick={onLogoutRequested}
          disabled={pending || logoutTextEmpty}
        >
          Force Logout
        </button>
        {logoutScenario === 'custom' && logoutTextEmpty ? (
          <span className="fault-controls__hint">
            Informe o texto do Logout para habilitar.
          </span>
        ) : null}
      </div>

      {/* Logon response mode (FR-FAULT-03). */}
      <div className="fault-controls__group">
        <h3 className="fault-controls__group-title">Resposta ao próximo Logon</h3>
        <p className="fault-controls__group-desc">
          Define como o acceptor responde ao próximo Logon do cliente. Aplicado
          imediatamente.
        </p>
        <label className="fault-controls__field">
          Modo de Logon
          <select
            value={logonMode}
            onChange={(e) => onLogonModeChange(e.target.value as LogonMode)}
            disabled={pending || logonModeSaving}
          >
            {LOGON_MODES.map((mode) => (
              <option key={mode.value} value={mode.value}>
                {mode.label}
              </option>
            ))}
          </select>
        </label>
        {logonModeSaving ? (
          <span className="fault-controls__hint" role="status">
            Aplicando…
          </span>
        ) : null}
      </div>

      {/* Sequence Gap (FR-FAULT-04). */}
      <div className="fault-controls__group">
        <h3 className="fault-controls__group-title">Sequence Gap</h3>
        <p className="fault-controls__group-desc">
          Pula N MsgSeqNum de saída para forçar um ResendRequest do cliente.
        </p>
        <label className="fault-controls__field">
          MsgSeqNum a pular
          <input
            type="number"
            min={1}
            step={1}
            value={skip}
            onChange={(e) => setSkip(Number(e.target.value))}
          />
        </label>
        <button
          type="button"
          className="fault-controls__button"
          data-variant="danger"
          data-action="seq-gap"
          onClick={onSeqGapRequested}
          disabled={pending || skipInvalid}
        >
          Injetar gap
        </button>
        {skipInvalid ? (
          <span className="fault-controls__hint">
            Informe um inteiro positivo.
          </span>
        ) : null}
      </div>

      {error ? (
        <p className="fault-controls__error" role="alert">
          {error}
        </p>
      ) : null}

      {/* Modal único de confirmação, parametrizado pela injeção pendente. */}
      <ConfirmDialog
        open={confirming === 'drop'}
        title="Derrubar a conexão?"
        description="O socket TCP da sessão FIX ativa será fechado sem Logout. O cliente deve detectar a queda e reconectar com backoff exponencial."
        confirmLabel="Drop Connection"
        confirmDisabled={pending}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />
      <ConfirmDialog
        open={confirming === 'logout'}
        title="Forçar Logout?"
        description={
          <>
            Um Logout (35=5) será enviado ao cliente com Text (58):{' '}
            <strong>“{resolvedLogoutText}”</strong>. A sessão FIX ativa será
            encerrada.
          </>
        }
        confirmLabel="Force Logout"
        confirmDisabled={pending}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />
      <ConfirmDialog
        open={confirming === 'seq-gap'}
        title="Injetar sequence gap?"
        description={
          <>
            O acceptor pulará <strong>{skip}</strong> MsgSeqNum de saída, o que
            deve disparar um ResendRequest do cliente. A continuidade da sequência
            da sessão será deliberadamente quebrada.
          </>
        }
        confirmLabel="Injetar gap"
        confirmDisabled={pending}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />
    </section>
  );
}

export default FaultControls;
