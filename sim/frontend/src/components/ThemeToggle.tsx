/**
 * Alternador de tema claro/escuro da topbar (Seção 4.5 do design).
 *
 * Botão único que inverte o tema corrente via `useTheme`, que persiste a
 * escolha em `localStorage` e aplica o atributo `data-theme` na raiz. O rótulo
 * descreve a ação (ir para o outro tema), e `aria-pressed` expõe se o modo
 * escuro está ativo para tecnologias assistivas — o estado não é comunicado só
 * pelo ícone.
 */

import { useTheme } from '../theme/useTheme';

/** Botão de alternância de tema para a topbar do SimPanel. */
export function ThemeToggle(): React.JSX.Element {
  const { theme, toggleTheme } = useTheme();
  const isDark = theme === 'dark';
  const nextLabel = isDark ? 'Tema claro' : 'Tema escuro';

  return (
    <button
      type="button"
      className="theme-toggle"
      data-theme={theme}
      aria-pressed={isDark}
      title={`Alternar para o ${isDark ? 'tema claro' : 'tema escuro'}`}
      onClick={toggleTheme}
    >
      <span className="theme-toggle__icon" aria-hidden="true">
        {isDark ? '☀️' : '🌙'}
      </span>
      <span className="theme-toggle__label">{nextLabel}</span>
    </button>
  );
}

export default ThemeToggle;
