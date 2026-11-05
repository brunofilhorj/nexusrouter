/**
 * Hook de assinatura do store externo do SimPanel.
 *
 * Fino wrapper de `useSyncExternalStore` (React 19) que liga qualquer componente
 * ao `appStore`, re-renderizando quando o snapshot imutável troca de identidade
 * (Seção 4.3 do design). Opcionalmente recebe um seletor para derivar uma fatia
 * do estado; o seletor deve devolver valores estáveis por identidade (o próprio
 * snapshot garante isso, pois cada mutação troca o objeto inteiro).
 */

import { useSyncExternalStore } from 'react';
import { appStore, type AppState, type AppStore } from './store';

/** Assina o store inteiro e devolve o snapshot corrente. */
export function useAppState(store: AppStore = appStore): AppState {
  return useSyncExternalStore(store.subscribe, store.getSnapshot);
}

/**
 * Assina o store e devolve uma fatia derivada pelo `selector`.
 *
 * O `selector` é aplicado ao snapshot; como cada mutação troca o snapshot
 * inteiro, fatias que são campos diretos do estado (ex.: `s.session`) mantêm
 * identidade estável entre renders sem mudança.
 */
export function useAppSelector<T>(
  selector: (state: AppState) => T,
  store: AppStore = appStore,
): T {
  return useSyncExternalStore(
    store.subscribe,
    () => selector(store.getSnapshot()),
  );
}
