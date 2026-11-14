import { useEffect, useRef, useSyncExternalStore } from 'react';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { statusSchema, type LibraryNotice, type LibraryStatus } from './voices';

/**
 * The voice library's status (2.41): voices, tags, wanted words, the category index's progress and the
 * "possible match" notices, fetched once per change the companion announces (the snapshot's
 * `voiceLibrary` revision) and shared by everything that shows them. Also the page's requests to open
 * Voices on a recording or Mimic at a moment of the history (a notice's jump marker).
 */
type Store = { status: LibraryStatus | null };
let store: Store = { status: null };
const listeners = new Set<() => void>();
const set = (next: Partial<Store>) => {
  store = { ...store, ...next };
  for (const listener of listeners) listener();
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
export const useVoiceStore = () => useSyncExternalStore(subscribe, () => store);

let fetching: Promise<void> | null = null;
let again = false;
export function refreshLibrary(): Promise<void> {
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
            status: statusSchema.parse(await controller.run('libraryStatus', {}, { quiet: true })),
          });
        } catch {
          set({ status: null });
        }
      } while (again);
    } finally {
      fetching = null;
    }
  })();
  return fetching;
}

/** Someone asked to see a moment of the history (a notice's Jump) or a library recording. */
export type OpenRequest =
  | { kind: 'history'; sessionId: string; startFrame: number; endFrame: number }
  | { kind: 'recording'; recordingId: string };
let pending: OpenRequest | null = null;
const openListeners = new Set<(request: OpenRequest) => void>();
export function requestOpen(request: OpenRequest) {
  pending = request;
  for (const listener of openListeners) listener(request);
}
/** Takes the request still waiting for a window that opens after it was made (once). */
export function takeOpen(kind: OpenRequest['kind']) {
  if (pending?.kind !== kind) return null;
  const request = pending;
  pending = null;
  return request;
}
export function useOpenRequests(listener: (request: OpenRequest) => void) {
  const current = useRef(listener);
  current.current = listener;
  useEffect(() => {
    const call = (request: OpenRequest) => current.current(request);
    openListeners.add(call);
    return () => {
      openListeners.delete(call);
    };
  }, []);
}

/** A notice's words: quiet (no sound), deduplicated by the companion, with where it was heard. */
export function noticeText(n: LibraryNotice) {
  return n.kind === 'history'
    ? `Possible match for ${n.voiceName}: “${n.text}” was just said.`
    : `${n.voiceName} now has “${n.text}” in the voice library.`;
}

/**
 * Keeps the store in step while `enabled` (a 2.41 companion, and a host page that relays it). New
 * notices since this page looked are shown once, without sound; older ones are not announced. A
 * history match is shown only to someone who may open Mimic (`canMimic`), since it says what was said.
 */
export function useLibrarySync(state: Snapshot, enabled: boolean, canMimic: boolean) {
  const mimic = useRef(canMimic);
  mimic.current = canMimic;
  const seen = useRef<number | null>(null);
  const revision = state.voiceLibrary ?? 0;
  useEffect(() => {
    if (!enabled) {
      set({ status: null });
      seen.current = null;
      return;
    }
    // The first look waits a little: joining a session sends a burst of requests already, and a guest's
    // host page allows only so many per second.
    const timer = setTimeout(
      () => {
        void refreshLibrary().then(() => {
          const notices = store.status?.notices ?? [];
          const newest = notices.reduce((max, n) => Math.max(max, n.id), 0);
          if (seen.current === null) {
            seen.current = newest;
            return;
          }
          for (const n of notices) {
            if (n.id <= seen.current) continue;
            if (n.kind === 'history' && !mimic.current) continue;
            controller.notify(
              noticeText(n),
              'info',
              n.kind === 'history' && n.sessionId && n.startFrame != null && n.endFrame != null
                ? {
                    label: 'Jump',
                    run: () =>
                      requestOpen({
                        kind: 'history',
                        sessionId: n.sessionId!,
                        startFrame: n.startFrame!,
                        endFrame: n.endFrame!,
                      }),
                  }
                : n.recordingId
                  ? {
                      label: 'Open',
                      run: () => requestOpen({ kind: 'recording', recordingId: n.recordingId! }),
                    }
                  : undefined,
            );
          }
          seen.current = Math.max(seen.current, newest);
        });
      },
      seen.current === null ? 2000 : 250,
    );
    return () => clearTimeout(timer);
  }, [enabled, revision]);
}

/** Test seam. */
export function resetVoiceStore() {
  store = { status: null };
  fetching = null;
  again = false;
  pending = null;
}
