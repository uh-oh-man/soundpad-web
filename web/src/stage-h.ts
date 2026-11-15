import { createContext, useContext } from 'react';
import { controller } from './controller';
import { notify } from './notifications';
import type { Snapshot } from './protocol';

/**
 * Stage H (2.44) wiring for the website: backups, the updater, Organize mode, setup and shortcuts. The
 * app body provides this once (main.tsx), so each package's pieces read the same state without new props
 * through the shell. Integrator-owned and frozen; each package keeps its logic in its own module.
 */
export type StageH = {
  state: Snapshot;
  controller: typeof controller;
  mode: ReturnType<typeof controller.getSnapshot>['mode'];
  demo: boolean;
  notify: typeof notify;
};
export const StageHContext = createContext<StageH>({
  state: controller.getSnapshot().state,
  controller,
  mode: 'landing',
  demo: false,
  notify,
});
export const useStageH = () => useContext(StageHContext);

/** True while Organize mode is on where `el` is (its board): clicks and keys there select, never play. */
export function organizeActive(el: Element | null): boolean {
  return (
    !!el?.closest?.('[data-organize="on"]') ||
    document.getElementById('board')?.dataset.organize === 'on'
  );
}
