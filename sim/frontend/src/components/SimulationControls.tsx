/**
 * Controles da rodada de simulação (FR-UI-04).
 *
 * Expõe os quatro comandos de ciclo de vida — Start, Pause, Resume e Stop — com
 * o `disabled` de cada botão derivado do estado corrente da FSM recebido por
 * telemetria (tabela da Seção 4.4 do design):
 *
 * | Estado  | Start | Pause | Resume | Stop |
 * |---------|-------|-------|--------|------|
 * | Idle    |  ✅   |  ❌   |  ❌    | ❌   |
 * | Running |  ❌   |  ✅   |  ❌    | ✅   |
 * | Paused  |  ❌   |  ❌   |  ✅    | ✅   |
 *
 * `Stop` é uma ação de risco assimétrico (encerra a rodada e zera o estado) e,
 * conforme a Seção 4.5, recebe atrito explícito: abre um diálogo de confirmação
 * que nomeia a ação e seu efeito antes de executar. Start/Pause/Resume são de
 * baixo risco e disparam diretamente, sem modal. Erros da API aparecem como
 * aviso discreto; não bloqueiam a UI.
 */

import { useCallback, useState } from 'react';
import { simApi, SimApiError } from '../api/client';
import { useAppSelector } from '../state/useAppStore';
import ConfirmDialog from './ConfirmDialog';
import type { SimulationState } from '../api/types';

/** Mapa de habilitação por estado, espelhando a tabela da Seção 4.4. */
interface ControlEnablement {
  /** Start disponível apenas em Idle. */
  start: boolean;
  /** Pause disponível apenas em Running. */
  pause: boolean;
  /** Resume disponível apenas em Paused. */
  resume: boolean;
  /** Stop disponível em Running ou Paused. */
  stop: boolean;
}

/** Deriva a habilitação de cada botão a partir do estado da FSM (Seção 4.4). */
function enablementFor(state: SimulationState): ControlEnablement {
  switch (state) {
    case 'Running':
      return { start: false, pause: true, resume: false, stop: true };
    case 'Paused':
      return { start: false, pause: false, resume: true, stop: true };
    case 'Idle':
    default:
      return { start: true, pause: false, resume: false, stop: false };
  }
}

/** Extrai uma mensagem legível de um erro arbitrário lançado pela API. */
function errorMessage(error: unknown): string {
  if (error instanceof SimApiError) {
    return error.message;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'Erro inesperado ao executar o comando.';
}

/** Painel de controle da rodada de simulação. */
export function SimulationControls(): React.JSX.Element {
  const simulationState = useAppSelector<SimulationState>(
    (s) => s.simulationState,
  );
  const enablement = enablementFor(simulationState);

  // Comando em curso: desabilita todos os botões para evitar duplo disparo.
  const [pending, setPending] = useState(false);
  // Última falha de comando, exibida como aviso discreto (não-modal).
  const [error, setError] = useState<string | null>(null);
  // Diálogo de confirmação do Stop (ação de risco assimétrico, Seção 4.5).
  const [confirmingStop, setConfirmingStop] = useState(false);

  /** Executa um comando da API, tratando pending e erro de forma uniforme. */
  const run = useCallback(async (command: () => Promise<void>): Promise<void> => {
    setPending(true);
    setError(null);
    try {
      await command();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }, []);

  const onStart = useCallback(() => {
    void run(() => simApi.start());
  }, [run]);

  const onPause = useCallback(() => {
    void run(() => simApi.pause());
  }, [run]);

  const onResume = useCallback(() => {
    void run(() => simApi.resume());
  }, [run]);

  const onStopRequested = useCallback(() => {
    setConfirmingStop(true);
  }, []);

  const onStopCancel = useCallback(() => {
    setConfirmingStop(false);
  }, []);

  const onStopConfirm = useCallback(() => {
    setConfirmingStop(false);
    void run(() => simApi.stop());
  }, [run]);

  return (
    <section
      className="simulation-controls"
      aria-labelledby="simulation-controls-heading"
    >
      <h2 id="simulation-controls-heading">Controle</h2>

      <div className="simulation-controls__buttons" role="group" aria-label="Comandos da simulação">
        <button
          type="button"
          className="simulation-controls__button"
          data-action="start"
          onClick={onStart}
          disabled={pending || !enablement.start}
        >
          Start
        </button>
        <button
          type="button"
          className="simulation-controls__button"
          data-action="pause"
          onClick={onPause}
          disabled={pending || !enablement.pause}
        >
          Pause
        </button>
        <button
          type="button"
          className="simulation-controls__button"
          data-action="resume"
          onClick={onResume}
          disabled={pending || !enablement.resume}
        >
          Resume
        </button>
        <button
          type="button"
          className="simulation-controls__button"
          data-action="stop"
          onClick={onStopRequested}
          disabled={pending || !enablement.stop}
        >
          Stop
        </button>
      </div>

      {error ? (
        <p className="simulation-controls__error" role="alert">
          {error}
        </p>
      ) : null}

      <ConfirmDialog
        open={confirmingStop}
        title="Parar a simulação?"
        description="Parar encerra a rodada em andamento e zera os contadores. O fluxo de mensagens para imediatamente e o estado volta para Idle."
        confirmLabel="Parar"
        confirmDisabled={pending}
        onConfirm={onStopConfirm}
        onCancel={onStopCancel}
      />
    </section>
  );
}

export default SimulationControls;
