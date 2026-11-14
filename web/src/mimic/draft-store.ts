import { useEffect, useRef, useSyncExternalStore } from 'react';
import type { z } from 'zod';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import {
  bandLabel,
  draftListSchema,
  guestBudgetSchema,
  hostBudgetSchema,
  newBands,
  type DraftList,
} from './drafts';

/**
 * The person's drafts and the other people's selections (2.40), fetched once per change the companion
 * announces (the snapshot's `drafts` revision) and shared by everything that shows them, plus the
 * storage figures (`budgets` revision): a guest's own, or for the host every guest's and their requests.
 */
type Budget =
  | { host: z.infer<typeof hostBudgetSchema>; guest?: undefined }
  | { guest: z.infer<typeof guestBudgetSchema>; host?: undefined };
type Store = { drafts: DraftList | null; budget: Budget | null };
let store: Store = { drafts: null, budget: null };
const listeners = new Set<() => void>();
const set = (next: Partial<Store>) => {
  store = { ...store, ...next };
  for (const listener of listeners) listener();
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
const read = () => store;
export const useDraftStore = () => useSyncExternalStore(subscribe, read);

let fetching: Promise<void> | null = null;
let again = false;
/** Fetches the draft list now (after one's own change, say); a fetch in progress is followed by one more. */
export function refreshDrafts(): Promise<void> {
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
            drafts: draftListSchema.parse(await controller.run('draftList', {}, { quiet: true })),
          });
        } catch {
          // Nothing recorded yet, or not allowed: no drafts to show.
          set({ drafts: null });
        }
      } while (again);
    } finally {
      fetching = null;
    }
  })();
  return fetching;
}

export async function refreshBudget(host: boolean) {
  try {
    const raw = await controller.run('budgetStatus', {}, { quiet: true });
    set({
      budget: host
        ? { host: hostBudgetSchema.parse(raw) }
        : { guest: guestBudgetSchema.parse(raw) },
    });
  } catch {
    set({ budget: null });
  }
}

/**
 * Keeps the store in step with the companion while `enabled` (a 2.40 companion, and this person may use
 * Mimic). Someone else's new capture gets a quiet notice; what existed before this page looked does not.
 */
export function useDraftSync(state: Snapshot, enabled: boolean) {
  const seen = useRef<Set<string> | null>(null);
  const revision = state.drafts ?? 0;
  const session = state.mimic?.history?.sessionId ?? null;
  useEffect(() => {
    if (!enabled || !session) {
      seen.current = null;
      set({ drafts: null });
      return;
    }
    // Dragging an edge can change a draft several times a second: fetch once it settles.
    const timer = setTimeout(() => {
      void refreshDrafts().then(() => {
        const bands = store.drafts?.others ?? [];
        if (seen.current === null) seen.current = new Set(bands.map((b) => b.id));
        else {
          for (const band of newBands(seen.current, bands)) {
            seen.current.add(band.id);
            controller.notify(
              `${band.ownerName} captured a clip${band.name ? `: “${band.name}”` : ''}. ${bandLabel(band)} shows in Mimic.`,
            );
          }
        }
      });
    }, 250);
    return () => clearTimeout(timer);
  }, [enabled, revision, session]);
}

/** As `useDraftSync`, for the storage figures (the `budgets` revision). */
export function useBudgetSync(state: Snapshot, host: boolean, enabled: boolean) {
  const revision = state.budgets ?? 0;
  useEffect(() => {
    if (!enabled) {
      set({ budget: null });
      return;
    }
    const timer = setTimeout(() => void refreshBudget(host), 200);
    return () => clearTimeout(timer);
  }, [enabled, revision, host]);
}

/** Test seam. */
export function resetDraftStore() {
  store = { drafts: null, budget: null };
  fetching = null;
  again = false;
}
