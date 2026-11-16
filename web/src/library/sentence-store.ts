import { useEffect, useSyncExternalStore } from 'react';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { listSchema, type SentenceList } from './sentences';

/**
 * The sentence list (2.42): kept sentences (everyone's), this person's own projects, and the named
 * palette pieces, fetched once per change the companion announces (the snapshot's `sentences`
 * revision) and shared by the Sentences window and the board's pinned tiles.
 */
type Store = { list: SentenceList | null; failed: boolean };
let store: Store = { list: null, failed: false };
const listeners = new Set<() => void>();
const set = (next: Partial<Store>) => {
  store = { ...store, ...next };
  for (const listener of listeners) listener();
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
export const useSentenceStore = () => useSyncExternalStore(subscribe, () => store);

let fetching: Promise<void> | null = null;
let again = false;
// A failed refresh keeps the last list (2.42 review: a refusal or a short reconnect used to empty the
// window and the board's pinned tiles) and tries again a few times, waiting longer each time.
const RETRIES = 5;
let retry = { timer: undefined as ReturnType<typeof setTimeout> | undefined, count: 0 };
export function refreshSentences(): Promise<void> {
  if (fetching) {
    again = true;
    return fetching;
  }
  fetching = (async () => {
    try {
      do {
        again = false;
        clearTimeout(retry.timer);
        try {
          set({
            list: listSchema.parse(await controller.run('sentenceList', {}, { quiet: true })),
            failed: false,
          });
          retry.count = 0;
        } catch {
          set({ failed: true });
          if (retry.count < RETRIES)
            retry.timer = setTimeout(() => void refreshSentences(), 1000 * 2 ** retry.count++);
        }
      } while (again);
    } finally {
      fetching = null;
    }
  })();
  return fetching;
}

/** Keeps the list in step while `enabled` (a 2.42 companion, and a host page that relays it). */
export function useSentenceSync(state: Snapshot, enabled: boolean) {
  const revision = state.sentences ?? 0;
  useEffect(() => {
    if (!enabled) {
      clearTimeout(retry.timer);
      retry.count = RETRIES;
      set({ list: null, failed: false });
      return;
    }
    retry.count = 0;
    // Like the library's first look, a moment after joining (the join sends a burst already).
    const timer = setTimeout(() => void refreshSentences(), store.list ? 150 : 2200);
    return () => clearTimeout(timer);
  }, [enabled, revision]);
}

// ---- Where "Add to sentence" goes (2.45) ----------------------------------------------------------

/**
 * The sentence the palette's and phrase blocks' "Add to sentence" adds to, and where in its text: the
 * last sentence this person had open to edit, at the caret they left in its text (null: the end).
 * Per page, in memory only. Text typed just before leaving the builder is still on its way; adds
 * wait for it (`textSent`), so they land in the text the box showed.
 */
export type ComposeTarget = { sentenceId: string; caret: number | null; title: string };
let target: ComposeTarget | null = null;
let sending: Promise<unknown> = Promise.resolve();
const targetListeners = new Set<() => void>();
export function setComposeTarget(next: ComposeTarget | null) {
  if (
    next === target ||
    (next &&
      target &&
      next.sentenceId === target.sentenceId &&
      next.caret === target.caret &&
      next.title === target.title)
  )
    return;
  target = next;
  for (const listener of targetListeners) listener();
}
export const composeTarget = () => target;
export const useComposeTarget = () =>
  useSyncExternalStore(
    (listener) => {
      targetListeners.add(listener);
      return () => targetListeners.delete(listener);
    },
    () => target,
  );
/** The builder's last text on its way to the companion (sent as it closes). */
export function noteTextSending(promise: Promise<unknown>) {
  sending = promise.catch(() => {});
}
export const textSent = () => sending;

/** Test seam: the store as the hook sees it. */
export const sentenceStoreSnapshot = () => store;

/** Test seam. */
export function resetSentenceStore() {
  store = { list: null, failed: false };
  fetching = null;
  again = false;
  clearTimeout(retry.timer);
  retry = { timer: undefined, count: 0 };
  target = null;
  sending = Promise.resolve();
}
