import { useEffect, useSyncExternalStore } from 'react';
import { z } from 'zod';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { supports } from '../../version';

/**
 * What the guided setup and the host-wide hotkey pickers read from the companion (2.44, gates setup and
 * hostHotkeys): setupGet and hotkeysGet answers, fetched again when the snapshot's `setupDone` or
 * `hotkeys` revision moves. Host only; guests never send these.
 */
export const problemSchema = z.enum(['notRunning', 'noAnswer', 'accessDenied', 'oldVersion']);
export type SoundpadProblem = z.infer<typeof problemSchema>;
export const setupInfoSchema = z.object({
  done: z.boolean(),
  dismissed: z.boolean(),
  existing: z.boolean(),
  soundpad: z.object({
    available: z.boolean(),
    problem: problemSchema.nullable(),
    error: z.string().max(2000).nullable(),
    elevated: z.boolean(),
  }),
  outputs: z.array(z.object({ id: z.string().max(512), name: z.string().max(600) })).max(500),
  remembered: z.string().max(512).nullable(),
});
export type SetupInfo = z.infer<typeof setupInfoSchema>;

const statusSchema = z.object({
  state: z.enum(['off', 'on', 'taken', 'invalid', 'conflict', 'disabled']),
  message: z.string().max(600).nullable(),
  last: z.string().max(600).nullable(),
});
export const HOST_ACTIONS = ['capture', 'panic', 'stop'] as const;
export type HostAction = (typeof HOST_ACTIONS)[number];
export const hotkeysSchema = z.object({
  bindings: z.object({
    capture: z.string().max(40).nullable(),
    panic: z.string().max(40).nullable(),
    stop: z.string().max(40).nullable(),
  }),
  status: z.object({ capture: statusSchema, panic: statusSchema, stop: statusSchema }),
  mimicKey: z.string().max(8),
  disabled: z.boolean(),
  problem: z.string().max(600).nullable(),
});
export type HostHotkeys = z.infer<typeof hotkeysSchema>;

type Store = {
  info: SetupInfo | null;
  hotkeys: HostHotkeys | null;
  /** The guide was opened by hand (Settings → Run setup again, or the upgrade card). */
  open: boolean;
  /** Closed without finishing on this page: not shown again until the page reloads. */
  closed: boolean;
};
let store: Store = { info: null, hotkeys: null, open: false, closed: false };
const listeners = new Set<() => void>();
function set(change: Partial<Store>) {
  store = { ...store, ...change };
  for (const l of listeners) l();
}
const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
};
export const useSetupStore = () => useSyncExternalStore(subscribe, () => store);

export async function loadSetup() {
  try {
    set({ info: setupInfoSchema.parse(await controller.run('setupGet', {}, { quiet: true })) });
  } catch {
    // An older or unreachable companion: the guide stays closed.
  }
}
export async function loadHotkeys() {
  try {
    set({ hotkeys: hotkeysSchema.parse(await controller.run('hotkeysGet', {}, { quiet: true })) });
  } catch {
    // An older or unreachable companion: the pickers say so.
  }
}
/** Applies a hotkeysSet; the companion's refusal (a clash, an invalid key) is thrown for the caller to show. */
export async function setHostHotkey(action: HostAction, chord: string | null) {
  const reply = hotkeysSchema.parse(
    await controller.run('hotkeysSet', { body: { [action]: chord } }, { quiet: true }),
  );
  set({ hotkeys: reply });
  return reply;
}
export function openGuide() {
  set({ open: true, closed: false });
  void loadSetup();
}
export function closeGuide() {
  set({ open: false, closed: true });
}
/** Test seam. */
export function resetSetupStore() {
  store = { info: null, hotkeys: null, open: false, closed: false };
}

/** The host's page on a companion with setup (not the example, not a guest). */
export function setupEligible(state: Snapshot, mode: string, demo: boolean) {
  return mode === 'host' && !demo && supports(state.companionVersion, 'setup');
}

/** Keeps the setup answer fresh while it is needed: when the host page connects and setupDone changes. */
export function useSetupSync(state: Snapshot, on: boolean) {
  const done = state.setupDone;
  useEffect(() => {
    if (on && (done === false || store.open)) void loadSetup();
  }, [on, done]);
}
/** Keeps the host-wide hotkeys fresh while they show (their revision is in the snapshot). */
export function useHotkeysSync(state: Snapshot, on: boolean) {
  const revision = state.hotkeys;
  useEffect(() => {
    if (on) void loadHotkeys();
  }, [on, revision]);
}
