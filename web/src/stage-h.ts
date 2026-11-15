import { createContext, useContext } from 'react';
import { controller } from './controller';
import { EMPTY, type Snapshot } from './protocol';
import type { NoticeAction, NoticeKind } from './notifications';

/**
 * Stage H (2.44) shared context: what every Stage H piece needs from the app shell, provided once in
 * main.tsx around the app body. This copy was made by WP1 (backups) on a base without Phase 0; the
 * integrator's Phase 0 version (with organizeActive for WP3) replaces it.
 */
export type StageH = {
  state: Snapshot;
  controller: typeof controller;
  mode: 'landing' | 'host' | 'guest';
  demo: boolean;
  notify: (message: string, kind?: NoticeKind, action?: NoticeAction) => void;
};

export const StageHContext = createContext<StageH>({
  state: EMPTY,
  controller,
  mode: 'landing',
  demo: false,
  notify: (message, kind, action) => controller.notify(message, kind, action),
});

export const useStageH = () => useContext(StageHContext);

/** True when `el` is inside the board while Organize mode is on (WP3). */
export function organizeActive(el: Element | null): boolean {
  return (
    !!el?.closest?.('[data-organize="on"]') ||
    document.getElementById('board')?.dataset.organize === 'on'
  );
}
