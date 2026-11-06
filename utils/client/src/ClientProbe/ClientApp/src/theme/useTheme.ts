/**
 * ClientPanel theme hook, copied leanly from the SimPanel.
 *
 * Dark is the default when the operator has no saved choice and the system
 * does not prefer light. The choice is stored in localStorage and wins over
 * the system preference on later loads. The palette is applied with a
 * `data-theme` attribute on `<html>`.
 */

import { useCallback, useEffect, useState } from 'react';

/** Themes supported by the ClientPanel. */
export type Theme = 'light' | 'dark';

/** Persistence key for the operator's theme choice. */
const STORAGE_KEY = 'clientpanel.theme';

/** Theme used when nothing is stored and the system has no light preference. */
const DEFAULT_THEME: Theme = 'dark';

/** True when the value is a supported theme. */
function isTheme(value: unknown): value is Theme {
  return value === 'light' || value === 'dark';
}

/** Reads the stored theme, or null when missing or unavailable. */
function readStoredTheme(): Theme | null {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return isTheme(stored) ? stored : null;
  } catch {
    return null;
  }
}

/** Persists the theme, tolerating an unavailable localStorage. */
function writeStoredTheme(theme: Theme): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    // The theme still applies for the current session.
  }
}

/** System theme via prefers-color-scheme, with dark as the fallback. */
function systemTheme(): Theme {
  if (window.matchMedia('(prefers-color-scheme: light)').matches) {
    return 'light';
  }
  return DEFAULT_THEME;
}

/** Initial theme: a stored choice wins over the system preference. */
function initialTheme(): Theme {
  return readStoredTheme() ?? systemTheme();
}

/** Applies the theme to `<html>` through the `data-theme` attribute. */
function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-theme', theme);
}

/** Value returned by `useTheme`. */
export interface UseThemeResult {
  /** Theme currently applied to the document. */
  theme: Theme;
  /** Sets the theme and persists it. */
  setTheme: (theme: Theme) => void;
  /** Switches between light and dark and persists the choice. */
  toggleTheme: () => void;
}

/**
 * Resolves the initial theme, applies it to the document, and exposes the
 * commands that change it.
 */
export function useTheme(): UseThemeResult {
  const [theme, setThemeState] = useState<Theme>(initialTheme);

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
