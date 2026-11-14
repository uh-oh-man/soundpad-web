import { useEffect, useSyncExternalStore } from 'react';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { listSchema, type SentenceList } from './sentences';

/**
 * The sentence list (2.42): kept sentences (everyone's), this person's own projects, and the named
 * palette pieces, fetched once per change the companion announces (the snapshot's `sentences`
 * revision) and shared by the Sentences window and the board's pinned tiles.
 */
type Store = { list: SentenceList | null };
let store: Store = { list: null };
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
export function refreshSentences(): Promise<void> {
  if (fetching) {
    again = true;
    return fetching;
  }
  fetching = (async () => {
    try {
      do {
        again = false;
        try {
          set({
            list: listSchema.parse(await controller.run('sentenceList', {}, { quiet: true })),
          });
        } catch {
          set({ list: null });
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
      set({ list: null });
      return;
    }
    // Like the library's first look, a moment after joining (the join sends a burst already).
    const timer = setTimeout(() => void refreshSentences(), store.list ? 150 : 2200);
    return () => clearTimeout(timer);
  }, [enabled, revision]);
}

/** Test seam. */
export function resetSentenceStore() {
  store = { list: null };
  fetching = null;
  again = false;
}
