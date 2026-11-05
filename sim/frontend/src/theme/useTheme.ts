/**
 * Hook de tema do SimPanel (Seção 4.5 do design).
 *
 * Suporta tema claro e escuro. Na primeira carga, respeita a preferência do
 * sistema operacional (`prefers-color-scheme`), com **escuro como padrão**
 * quando não há preferência explícita. A escolha do operador é persistida em
 * `localStorage` e tem precedência sobre a preferência do sistema nas cargas
 * seguintes.
 *
 * O tema é aplicado ao documento por um atributo `data-theme` na raiz
 * (`<html>`), que é o que as variáveis de cor de `tokens.css` consomem. Nenhum
 * componente referencia cor fixa diretamente: ler/alterar o tema por este hook
 * é a única forma de trocar a paleta.
 */

import { useCallback, useEffect, useState } from 'react';

/** Temas suportados pelo SimPanel. */
export type Theme = 'light' | 'dark';

/** Chave de persistência da preferência de tema no `localStorage`. */
const STORAGE_KEY = 'simpanel.theme';

/** Tema aplicado quando não há preferência salva nem do sistema. */
const DEFAULT_THEME: Theme = 'dark';

/** Verdadeiro para um valor que é um tema válido. */
function isTheme(value: unknown): value is Theme {
  return value === 'light' || value === 'dark';
}

/** Lê a preferência persistida, ou `null` quando ausente/indisponível. */
function readStoredTheme(): Theme | null {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return isTheme(stored) ? stored : null;
  } catch {
    // localStorage pode estar indisponível (modo privado, bloqueio): ignora.
    return null;
  }
}

/** Persiste a preferência de tema, tolerando `localStorage` indisponível. */
function writeStoredTheme(theme: Theme): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    // Sem persistência: o tema ainda vale para a sessão corrente.
  }
}

/** Tema do sistema via `prefers-color-scheme`, com escuro como padrão. */
function systemTheme(): Theme {
  if (window.matchMedia('(prefers-color-scheme: light)').matches) {
    return 'light';
  }
  return DEFAULT_THEME;
}

/** Tema inicial: preferência salva tem precedência sobre a do sistema. */
function initialTheme(): Theme {
  return readStoredTheme() ?? systemTheme();
}

/** Aplica o tema ao `<html>` via atributo `data-theme`. */
function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-theme', theme);
}

/** Valor devolvido por `useTheme`. */
export interface UseThemeResult {
  /** Tema corrente aplicado ao documento. */
  theme: Theme;
  /** Define explicitamente o tema e o persiste. */
  setTheme: (theme: Theme) => void;
  /** Alterna entre claro e escuro, persistindo a escolha. */
  toggleTheme: () => void;
}

/**
 * Gerencia o tema do SimPanel: resolve o tema inicial, aplica-o ao documento e
 * expõe os comandos de troca. A troca persiste a escolha do operador e passa a
 * ter precedência sobre a preferência do sistema.
 */
export function useTheme(): UseThemeResult {
  const [theme, setThemeState] = useState<Theme>(initialTheme);

  // Reflete o tema corrente no documento sempre que ele muda.
  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  const setTheme = useCallback((next: Theme): void => {
    writeStoredTheme(next);
    setThemeState(next);
  }, []);

  const toggleTheme = useCallback((): void => {
    setThemeState((current) => {
      const next: Theme = current === 'dark' ? 'light' : 'dark';
      writeStoredTheme(next);
      return next;
    });
  }, []);

  return { theme, setTheme, toggleTheme };
}

export default useTheme;
