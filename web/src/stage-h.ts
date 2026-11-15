import { createContext, useContext } from 'react';
import type { controller } from './controller';
import type { Snapshot } from './protocol';

/**
 * Stage H (2.44) shared pieces (spec section 3.1.5). Phase 0 owns this file; this is the WP3 subset under
 * the Phase 0 names. The context is not provided yet (WP3's components take props); the integrator's
 * Phase 0 provides it once in main.tsx.
 */
export type StageH = {
  state: Snapshot;
  controller: typeof controller;
  mode: 'host' | 'guest' | 'landing';
  demo: boolean;
  notify: (typeof controller)['notify'];
};
export const StageHContext = createContext<StageH | null>(null);
export const useStageH = () => useContext(StageHContext);

/**
 * True while the board is in Organize mode (2.44): clicks and keys select sounds there instead of playing
 * them, so the board's Space shortcut stays out of the way. Panic and search still work.
 */
export function organizeActive(el: Element | null): boolean {
  return (
    !!el?.closest?.('[data-organize="on"]') ||
    (typeof document !== 'undefined' && document.getElementById('board')?.dataset.organize === 'on')
  );
}
