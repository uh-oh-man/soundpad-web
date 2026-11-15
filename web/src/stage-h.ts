import { createContext, useContext } from 'react';
import { controller } from './controller';
import { EMPTY, type Snapshot } from './protocol';
import type { NoticeAction, NoticeKind } from './notifications';

/**
 * Stage H (2.44) shared context: what every Stage H piece needs from the app shell, provided once in
 * main.tsx around the app body, so backups, the updater, Organize mode, setup and shortcuts read the same
 * state without new props through the shell. Each package keeps its logic in its own module.
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
