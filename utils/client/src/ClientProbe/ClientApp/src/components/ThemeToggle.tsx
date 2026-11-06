/**
 * Light/dark toggle for the ClientPanel topbar.
 * The label names the theme the click will apply. `aria-pressed` reports
 * whether dark is active, so the state is not carried by the icon alone.
 */

import type { JSX } from 'react';
import { useTheme } from '../theme/useTheme';
import styles from './ThemeToggle.module.css';

/** Topbar button that flips the persisted theme. */
export function ThemeToggle(): JSX.Element {
  const { theme, toggleTheme } = useTheme();
  const isDark = theme === 'dark';
  const nextLabel = isDark ? 'Tema claro' : 'Tema escuro';

  return (
    <button
      type="button"
      className={styles.toggle}
      data-theme={theme}
      aria-pressed={isDark}
      title={`Alternar para o ${isDark ? 'tema claro' : 'tema escuro'}`}
      onClick={toggleTheme}
    >
      <span aria-hidden="true">{isDark ? '☀️' : '🌙'}</span>
      <span>{nextLabel}</span>
    </button>
  );
}

export default ThemeToggle;
