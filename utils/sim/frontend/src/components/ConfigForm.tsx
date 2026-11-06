/**
 * Formulário de configuração da simulação (FR-UI-05) com a política híbrida por
 * campo (FR-SIM-06 / FR-SIM-08) aplicada na UI.
 *
 * Expõe todos os campos da `SimulationConfig`: quantidade de ordens (ou
 * ilimitado = 0), frequência (msg/s), lista de contas, lista de instrumentos,
 * estratégia de seleção de conta, proporção de desfechos e `TestMessageIndicator`.
 *
 * Política híbrida (Seção 4.4 do design):
 * - Em `Running`, apenas os campos **hot** (`RatePerSecond` e `Outcome`) ficam
 *   editáveis e submetíveis; o submit manda só o delta hot, evitando o 409 do
 *   servidor. Os campos **travados** ficam desabilitados com a dica
 *   "pause para alterar".
 * - Em `Idle`/`Paused`, todos os campos são editáveis e o submit manda a config
 *   completa.
 *
 * O 409 do servidor permanece como rede de segurança, exibido como aviso
 * discreto, caso o estado mude entre a leitura e o submit.
 *
 * A config vigente não vive no store de telemetria; é lida sob demanda via
 * `GET /v1/state` (`simApi.getState`) e recarregada quando o estado da FSM
 * transiciona, para refletir mudanças aplicadas por outra via.
 */

import { useCallback, useEffect, useState } from 'react';
import { simApi, SimApiError } from '../api/client';
import { useAppSelector } from '../state/useAppStore';
import type {
  AccountSelection,
  OutcomeMix,
  SimulationConfig,
  SimulationConfigRequest,
  SimulationState,
} from '../api/types';

/** Dica exibida nos campos travados durante `Running`. */
const LOCKED_HINT = 'pause para alterar';

/** Soma dos pesos de desfecho; deve ser 100 para submeter. */
function outcomeSum(outcome: OutcomeMix): number {
  return (
    outcome.fillPct +
    outcome.partialFillPct +
    outcome.canceledPct +
    outcome.rejectedPct
  );
}

/** Extrai uma mensagem legível de um erro arbitrário lançado pela API. */
function errorMessage(error: unknown): string {
  if (error instanceof SimApiError) {
    return error.message;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'Erro inesperado.';
}

/** Converte uma lista separada por vírgulas/linhas em array normalizado. */
function parseList(raw: string): string[] {
  return raw
    .split(/[\n,]/)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/** Serializa um array de contas de volta para uma textarea (uma por linha). */
function accountsToText(accounts: string[]): string {
  return accounts.join('\n');
}

/**
 * Estado de edição do formulário. Espelha a `SimulationConfig` em tipos
 * amigáveis à edição (contas como texto livre). Instrumentos e
 * `enabledMessageTypes` são tratados como travados e exibidos somente leitura
 * nesta tela; sua edição estruturada fica fora do escopo deste formulário.
 */
interface FormState {
  totalOrders: number;
  unlimited: boolean;
  ratePerSecond: number;
  accountsText: string;
  accountSelection: AccountSelection;
  outcome: OutcomeMix;
  partialFillsMax: number;
  testMessageIndicator: boolean;
}

/** Deriva o estado de edição a partir de uma `SimulationConfig` carregada. */
function toFormState(config: SimulationConfig): FormState {
  return {
    totalOrders: config.totalOrders,
    unlimited: config.totalOrders === 0,
    ratePerSecond: config.ratePerSecond,
    accountsText: accountsToText(config.accounts),
    accountSelection: config.accountSelection,
    outcome: { ...config.outcome },
    partialFillsMax: config.partialFillsMax,
    testMessageIndicator: config.testMessageIndicator,
  };
}

/** Formulário de configuração da simulação. */
export function ConfigForm(): React.JSX.Element {
  const simulationState = useAppSelector<SimulationState>(
    (s) => s.simulationState,
  );
  const running = simulationState === 'Running';

  // Config carregada do servidor; null enquanto a primeira leitura não voltou.
  const [loaded, setLoaded] = useState<SimulationConfig | null>(null);
  const [form, setForm] = useState<FormState | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  /** Carrega a config vigente do servidor e reinicializa o formulário. */
  const reload = useCallback(async (): Promise<void> => {
    setLoadError(null);
    try {
      const state = await simApi.getState();
      setLoaded(state.config);
      setForm(toFormState(state.config));
    } catch (err) {
      setLoadError(errorMessage(err));
    }
  }, []);

  // Carrega na montagem e recarrega quando o estado da FSM transiciona, para
  // refletir travas/liberações e qualquer mudança aplicada por outra via.
  useEffect(() => {
    void reload();
  }, [reload, simulationState]);

  /** Atualiza um campo do formulário preservando o restante. */
  const patch = useCallback((partial: Partial<FormState>): void => {
    setForm((prev) => (prev ? { ...prev, ...partial } : prev));
  }, []);

  /** Atualiza um peso de desfecho preservando os demais. */
  const patchOutcome = useCallback((partial: Partial<OutcomeMix>): void => {
    setForm((prev) =>
      prev ? { ...prev, outcome: { ...prev.outcome, ...partial } } : prev,
    );
  }, []);

  const onSubmit = useCallback(
    async (event: React.FormEvent): Promise<void> => {
      event.preventDefault();
      if (!form) {
        return;
      }

      if (outcomeSum(form.outcome) !== 100) {
        setSubmitError('Os pesos de desfecho devem somar 100.');
        return;
      }

      setSaving(true);
      setSubmitError(null);
      setNotice(null);

      // Em Running, envia só o delta hot (ratePerSecond + outcome); em
      // Idle/Paused, envia a config completa editável.
      let body: SimulationConfigRequest;
      if (running) {
        body = {
          ratePerSecond: form.ratePerSecond,
          outcome: { ...form.outcome },
        };
      } else {
        body = {
          totalOrders: form.unlimited ? 0 : form.totalOrders,
          ratePerSecond: form.ratePerSecond,
          accounts: parseList(form.accountsText),
          accountSelection: form.accountSelection,
          outcome: { ...form.outcome },
          partialFillsMax: form.partialFillsMax,
          testMessageIndicator: form.testMessageIndicator,
        };
      }

      try {
        const updated = await simApi.updateConfig(body);
        setLoaded(updated);
        setForm(toFormState(updated));
        setNotice('Configuração salva.');
      } catch (err) {
        // 409 = rede de segurança: o estado mudou entre leitura e submit.
        if (err instanceof SimApiError && err.status === 409) {
          setSubmitError(
            'A simulação está em execução; só os campos ao vivo podem ser alterados. ' +
              err.message,
          );
          void reload();
        } else {
          setSubmitError(errorMessage(err));
        }
      } finally {
        setSaving(false);
      }
    },
    [form, running, reload],
  );

  if (loadError) {
    return (
      <section className="config-form" aria-labelledby="config-form-heading">
        <h2 id="config-form-heading">Configuração</h2>
        <p className="config-form__error" role="alert">
          Não foi possível carregar a configuração: {loadError}
        </p>
        <button type="button" onClick={() => void reload()}>
          Tentar novamente
        </button>
      </section>
    );
  }

  if (!form || !loaded) {
    return (
      <section className="config-form" aria-labelledby="config-form-heading">
        <h2 id="config-form-heading">Configuração</h2>
        <p className="config-form__empty">Carregando configuração…</p>
      </section>
    );
  }

  const outcomeTotal = outcomeSum(form.outcome);

  return (
    <section className="config-form" aria-labelledby="config-form-heading">
      <h2 id="config-form-heading">Configuração</h2>

      {running ? (
        <p className="config-form__policy" role="note">
          Simulação em execução: apenas frequência e proporção de desfechos são
          ajustáveis ao vivo. Os demais campos exigem pausar.
        </p>
      ) : null}

      <form className="config-form__form" onSubmit={(e) => void onSubmit(e)}>
        {/* Quantidade de ordens — travado em Running. */}
        <fieldset className="config-form__group" disabled={running}>
          <legend>Quantidade de ordens</legend>
          <label className="config-form__field">
            <input
              type="checkbox"
              checked={form.unlimited}
              onChange={(e) => patch({ unlimited: e.target.checked })}
            />
            Ilimitado
          </label>
          <label className="config-form__field">
            Total de ordens
            <input
              type="number"
              min={0}
              value={form.totalOrders}
              onChange={(e) =>
                patch({ totalOrders: Number(e.target.value) })
              }
              disabled={form.unlimited}
            />
          </label>
          {running ? (
            <span className="config-form__hint">{LOCKED_HINT}</span>
          ) : null}
        </fieldset>

        {/* Frequência — hot, sempre editável. */}
        <fieldset className="config-form__group">
          <legend>Frequência</legend>
          <label className="config-form__field">
            Mensagens por segundo
            <input
              type="number"
              min={0}
              step="0.1"
              value={form.ratePerSecond}
              onChange={(e) =>
                patch({ ratePerSecond: Number(e.target.value) })
              }
            />
          </label>
        </fieldset>

        {/* Contas e seleção — travado em Running. */}
        <fieldset className="config-form__group" disabled={running}>
          <legend>Contas</legend>
          <label className="config-form__field">
            Contas (uma por linha ou separadas por vírgula)
            <textarea
              rows={3}
              value={form.accountsText}
              onChange={(e) => patch({ accountsText: e.target.value })}
            />
          </label>
          <label className="config-form__field">
            Seleção de conta
            <select
              value={form.accountSelection}
              onChange={(e) =>
                patch({
                  accountSelection: e.target.value as AccountSelection,
                })
              }
            >
              <option value="RoundRobin">Round-robin</option>
              <option value="Random">Aleatória</option>
            </select>
          </label>
          {running ? (
            <span className="config-form__hint">{LOCKED_HINT}</span>
          ) : null}
        </fieldset>

        {/* Instrumentos — travado; exibição somente leitura neste formulário. */}
        <fieldset className="config-form__group" disabled>
          <legend>Instrumentos</legend>
          <ul className="config-form__instruments">
            {loaded.instruments.length === 0 ? (
              <li>Nenhum instrumento configurado.</li>
            ) : (
              loaded.instruments.map((inst) => (
                <li key={inst.securityId}>
                  {inst.symbol} ({inst.securityId}) — {inst.securityExchange}
                </li>
              ))
            )}
          </ul>
        </fieldset>

        {/* Proporção de desfechos — hot, sempre editável. */}
        <fieldset className="config-form__group">
          <legend>Proporção de desfechos (%)</legend>
          <label className="config-form__field">
            Fill
            <input
              type="number"
              min={0}
              max={100}
              value={form.outcome.fillPct}
              onChange={(e) =>
                patchOutcome({ fillPct: Number(e.target.value) })
              }
            />
          </label>
          <label className="config-form__field">
            Partial Fill
            <input
              type="number"
              min={0}
              max={100}
              value={form.outcome.partialFillPct}
              onChange={(e) =>
                patchOutcome({ partialFillPct: Number(e.target.value) })
              }
            />
          </label>
          <label className="config-form__field">
            Canceled
            <input
              type="number"
              min={0}
              max={100}
              value={form.outcome.canceledPct}
              onChange={(e) =>
                patchOutcome({ canceledPct: Number(e.target.value) })
              }
            />
          </label>
          <label className="config-form__field">
            Rejected
            <input
              type="number"
              min={0}
              max={100}
              value={form.outcome.rejectedPct}
              onChange={(e) =>
                patchOutcome({ rejectedPct: Number(e.target.value) })
              }
            />
          </label>
          <p
            className="config-form__outcome-sum"
            data-valid={outcomeTotal === 100 ? 'yes' : 'no'}
          >
            Soma: {outcomeTotal}% {outcomeTotal === 100 ? '' : '(deve ser 100)'}
          </p>
        </fieldset>

        {/* Partial fills máximos — travado em Running. */}
        <fieldset className="config-form__group" disabled={running}>
          <legend>Partial fills</legend>
          <label className="config-form__field">
            Máximo de partial fills
            <input
              type="number"
              min={0}
              value={form.partialFillsMax}
              onChange={(e) =>
                patch({ partialFillsMax: Number(e.target.value) })
              }
            />
          </label>
          {running ? (
            <span className="config-form__hint">{LOCKED_HINT}</span>
          ) : null}
        </fieldset>

        {/* TestMessageIndicator — travado em Running. */}
        <fieldset className="config-form__group" disabled={running}>
          <legend>Indicador de teste</legend>
          <label className="config-form__field">
            <input
              type="checkbox"
              checked={form.testMessageIndicator}
              onChange={(e) =>
                patch({ testMessageIndicator: e.target.checked })
              }
            />
            TestMessageIndicator (464 = Y)
          </label>
          {running ? (
            <span className="config-form__hint">{LOCKED_HINT}</span>
          ) : null}
        </fieldset>

        {submitError ? (
          <p className="config-form__error" role="alert">
            {submitError}
          </p>
        ) : null}
        {notice ? (
          <p className="config-form__notice" role="status">
            {notice}
          </p>
        ) : null}

        <div className="config-form__actions">
          <button type="submit" disabled={saving}>
            {saving ? 'Salvando…' : 'Salvar configuração'}
          </button>
        </div>
      </form>
    </section>
  );
}

export default ConfigForm;
