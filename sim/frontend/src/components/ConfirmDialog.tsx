/**
 * Modal de confirmação de ações importantes (Seção 4.5 do design).
 *
 * Componente de apresentação reutilizável que nomeia uma ação e seu efeito antes
 * de dispará-la, com um botão primário de confirmação e um de cancelamento. É
 * usado pelas ações de risco assimétrico do SimPanel — `Stop` da simulação e
 * todas as injeções de falha (`Drop Connection`, `Force Logout`, `Sequence
 * Gap`). Não usa duplo clique: a confirmação é sempre um ato deliberado em dois
 * passos (abrir → confirmar).
 *
 * É controlado pelo pai: renderiza apenas quando `open` é `true`, delega a
 * decisão a `onConfirm`/`onCancel` e não carrega estado próprio. A acessibilidade
 * segue o padrão de diálogo modal: `role="dialog"`, `aria-modal`, título e
 * descrição ligados por `aria-labelledby`/`aria-describedby`, foco inicial no
 * cancelamento (opção segura) e fechamento por `Escape`.
 */

import { useCallback, useEffect, useId, useRef } from 'react';

/** Propriedades do `ConfirmDialog`. */
export interface ConfirmDialogProps {
  /** Quando `false`, nada é renderizado; quando `true`, o modal aparece. */
  open: boolean;
  /** Título curto que nomeia a ação (ex.: "Derrubar a conexão?"). */
  title: string;
  /** Descrição do efeito da ação, exibida no corpo do modal. */
  description: React.ReactNode;
  /** Rótulo do botão primário de confirmação. Default: "Confirmar". */
  confirmLabel?: string;
  /** Rótulo do botão de cancelamento. Default: "Cancelar". */
  cancelLabel?: string;
  /**
   * Variante visual do botão de confirmação. `danger` sinaliza ação destrutiva
   * (default, pois o modal existe para ações de risco assimétrico); `primary`
   * para confirmações neutras.
   */
  confirmVariant?: 'danger' | 'primary';
  /** Desabilita o botão de confirmação (ex.: enquanto um comando está em curso). */
  confirmDisabled?: boolean;
  /** Chamado quando o usuário confirma a ação. */
  onConfirm: () => void;
  /** Chamado quando o usuário cancela (botão, `Escape` ou clique no backdrop). */
  onCancel: () => void;
}

/**
 * Modal de confirmação controlado pelo pai. Nomeia a ação e seu efeito e exige
 * confirmação explícita antes de prosseguir (Seção 4.5).
 */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = 'Confirmar',
  cancelLabel = 'Cancelar',
  confirmVariant = 'danger',
  confirmDisabled = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps): React.JSX.Element | null {
  const titleId = useId();
  const descId = useId();
  const cancelRef = useRef<HTMLButtonElement>(null);

  // Foco inicial no cancelamento (opção segura) ao abrir.
  useEffect(() => {
    if (open) {
      cancelRef.current?.focus();
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
        onCancel();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, onCancel]);

  // Clique no backdrop cancela; clique no painel não propaga para o backdrop.
  const onPanelClick = useCallback((event: React.MouseEvent): void => {
    event.stopPropagation();
  }, []);

  if (!open) {
    return null;
  }

  return (
    <div className="confirm-dialog" onClick={onCancel}>
      <div
        className="confirm-dialog__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descId}
        onClick={onPanelClick}
      >
        <h3 id={titleId} className="confirm-dialog__title">
          {title}
        </h3>
        <div id={descId} className="confirm-dialog__desc">
          {description}
        </div>
        <div className="confirm-dialog__actions">
          <button
            type="button"
            className="confirm-dialog__cancel"
            onClick={onCancel}
            ref={cancelRef}
          >
            {cancelLabel}
          </button>
          <button
            type="button"
            className="confirm-dialog__confirm"
            data-variant={confirmVariant}
            onClick={onConfirm}
            disabled={confirmDisabled}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

export default ConfirmDialog;
