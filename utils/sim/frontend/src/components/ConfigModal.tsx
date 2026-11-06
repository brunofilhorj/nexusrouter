/**
 * Modal de configuração da simulação (Seção 4.5).
 *
 * Move o `ConfigForm` da coluna de controle para um diálogo modal, aberto por um
 * ícone de engrenagem na topbar. Mantém o formulário fora do fluxo principal —
 * que é majoritariamente reativo (telemetria) — reservando a configuração a um
 * ato deliberado, sem ocupar espaço permanente no dashboard.
 *
 * Segue o mesmo padrão de acessibilidade do `ConfirmDialog`: `role="dialog"`,
 * `aria-modal`, título ligado por `aria-labelledby`, foco inicial no botão de
 * fechar, fechamento por `Escape` e clique no backdrop. É controlado pelo pai:
 * só renderiza quando `open` é `true` e delega o fechamento a `onClose`.
 *
 * O `ConfigForm` interno não muda: continua lendo/gravando a config via API e
 * aplicando a política híbrida por campo (FR-SIM-06). O modal é apenas a moldura.
 */

import { useCallback, useEffect, useId, useRef } from 'react';
import ConfigForm from './ConfigForm';

/** Propriedades do `ConfigModal`. */
export interface ConfigModalProps {
  /** Quando `false`, nada é renderizado; quando `true`, o modal aparece. */
  open: boolean;
  /** Chamado ao fechar (botão, `Escape` ou clique no backdrop). */
  onClose: () => void;
}

/** Diálogo modal que hospeda o formulário de configuração. */
export function ConfigModal({ open, onClose }: ConfigModalProps): React.JSX.Element | null {
  const titleId = useId();
  const closeRef = useRef<HTMLButtonElement>(null);

  // Foco inicial no botão de fechar (opção segura) ao abrir.
  useEffect(() => {
    if (open) {
      closeRef.current?.focus();
    }
  }, [open]);

  // Fecha com Escape enquanto o modal está aberto.
  useEffect(() => {
    if (!open) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, onClose]);

  // Clique no backdrop fecha; clique no painel não propaga para o backdrop.
  const onPanelClick = useCallback((event: React.MouseEvent): void => {
    event.stopPropagation();
  }, []);

  if (!open) {
    return null;
  }

  return (
    <div className="config-modal" onClick={onClose}>
      <div
        className="config-modal__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onClick={onPanelClick}
      >
        <header className="config-modal__header">
          <h2 id={titleId} className="config-modal__title">
            Configuração
          </h2>
          <button
            type="button"
            className="config-modal__close"
            aria-label="Fechar configuração"
            onClick={onClose}
            ref={closeRef}
          >
            ✕
          </button>
        </header>
        <div className="config-modal__body">
          <ConfigForm />
        </div>
      </div>
    </div>
  );
}

export default ConfigModal;
