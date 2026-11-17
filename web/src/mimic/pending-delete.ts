import { useSyncExternalStore } from 'react';
import { noticeShown, notify, retireNoticeAction } from '../notifications';
import type { TempAsset } from '../temp-assets';

/**
 * Delete with Undo for Mimic's clips and drafts (2.46; 2.47 review: nothing is promised that closing the
 * tab can't keep). The companion can't reliably take either back (a just-made Mimic clip's file goes with
 * it, and a discarded draft lets go of the history it held), so Delete hides the item on this page at once
 * and asks the companion once the Undo notice has gone. Until then the delete is written down in this
 * browser: a page closed meanwhile does it the next time it connects (`replayStoredDeletes`), instead of a
 * request sent while the page unloads that may never arrive.
 *
 * A refusal shows the item again (the caller's `commit` says why). Every notice is its own, so two deletes
 * with the same words each keep their own Undo.
 */
type Pending = {
  timer: ReturnType<typeof setTimeout>;
  notice: string;
  commit: () => Promise<unknown>;
};

/** How often a pending delete looks whether its notice has gone. */
export const CHECK_MS = 400;
/** Where drafts waiting for their Undo to go are written down, for the next page if this one closes. */
export const STORED_KEY = 'soundpad-web:mimic-pending-deletes';
/** A stored delete older than this is dropped unasked: the recording it was on is long gone. */
const STORED_MAX_AGE_MS = 24 * 60 * 60 * 1000;

/** A delete waiting for its Undo to go: a draft (its id and recording) or a Temp clip (its asset id). */
export type StoredDelete = {
  key: string;
  kind: 'draft' | 'clip';
  sourceId?: string;
  at: number;
};

const pending = new Map<string, Pending>();
/** Deleted at the companion: never shown again on this page, even before its next snapshot arrives. */
const removed = new Set<string>();
/** Stored by a page that closed before its Undo went: hidden until this page has deleted them. */
const replaying = new Set<string>();
let hidden: ReadonlySet<string> = new Set();
const listeners = new Set<() => void>();
const emit = () => {
  hidden = new Set([...pending.keys(), ...removed, ...replaying]);
  for (const listener of listeners) listener();
};

function readStored(): StoredDelete[] {
  try {
    const raw = JSON.parse(localStorage.getItem(STORED_KEY) ?? '[]') as unknown;
    if (!Array.isArray(raw)) return [];
    const now = Date.now();
    return raw.filter(
      (d): d is StoredDelete =>
        !!d &&
        typeof d.key === 'string' &&
        (d.kind === 'draft' || d.kind === 'clip') &&
        (d.sourceId === undefined || typeof d.sourceId === 'string') &&
        typeof d.at === 'number' &&
        now - d.at < STORED_MAX_AGE_MS,
    );
  } catch {
    return []; // unavailable or damaged storage: nothing waits
  }
}
function writeStored(list: StoredDelete[]) {
  try {
    if (list.length) localStorage.setItem(STORED_KEY, JSON.stringify(list));
    else localStorage.removeItem(STORED_KEY);
  } catch {
    // Not written down: a page closed within the Undo time then keeps the draft.
  }
}
const forget = (key: string) => writeStored(readStored().filter((d) => d.key !== key));

function finish(key: string) {
  const entry = pending.get(key);
  if (!entry) return;
  clearTimeout(entry.timer);
  retireNoticeAction(entry.notice);
  entry.commit().then(
    () => {
      pending.delete(key);
      removed.add(key);
      forget(key);
      emit();
    },
    () => {
      // Refused or failed: it is still there, so it shows again.
      pending.delete(key);
      forget(key);
      emit();
    },
  );
}

function check(key: string) {
  const entry = pending.get(key);
  if (!entry) return;
  if (noticeShown(entry.notice)) entry.timer = setTimeout(() => check(key), CHECK_MS);
  else finish(key);
}

/**
 * Hides `key` (a draft or asset id) now and runs `commit` once the Undo notice saying `message` has gone.
 * `onUndo` runs if Undo is pressed instead. `stored` writes it down, so a page closed before then still
 * deletes it the next time (see `replayStoredDeletes`). A second Delete of the same item does nothing.
 */
export function deleteWithUndo(
  key: string,
  message: string,
  commit: () => Promise<unknown>,
  onUndo?: () => void,
  stored?: { kind: 'draft' | 'clip'; sourceId?: string },
) {
  if (pending.has(key) || removed.has(key)) return;
  const notice = notify(
    message,
    'success',
    {
      label: 'Undo',
      run: () => {
        const entry = pending.get(key);
        if (!entry) return;
        clearTimeout(entry.timer);
        pending.delete(key);
        forget(key);
        emit();
        onUndo?.();
      },
    },
    undefined,
    { distinct: true },
  );
  if (stored)
    writeStored([...readStored().filter((d) => d.key !== key), { key, ...stored, at: Date.now() }]);
  pending.set(key, { notice, commit, timer: setTimeout(() => check(key), CHECK_MS) });
  emit();
}

/** Asks the companion now for every delete still waiting. */
export function flushPendingDeletes() {
  for (const key of [...pending.keys()]) finish(key);
}

/**
 * Deletes a closed page left waiting (2.47 review): done now through `run`, which says whether that worked
 * (one that is gone already counts as done). Those are hidden until then.
 */
export async function replayStoredDeletes(run: (d: StoredDelete) => Promise<boolean>) {
  const waiting = readStored().filter((d) => !pending.has(d.key) && !replaying.has(d.key));
  if (!waiting.length) return;
  for (const d of waiting) replaying.add(d.key);
  emit();
  for (const d of waiting) {
    let done: boolean;
    try {
      done = await run(d);
    } catch {
      done = false;
    }
    replaying.delete(d.key);
    if (done) {
      removed.add(d.key);
      forget(d.key);
    }
  }
  emit();
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
/** The ids hidden by Delete on this page: waiting for their Undo to go, or deleted. */
export const useDeleted = () => useSyncExternalStore(subscribe, () => hidden);
export const deletedNow = () => hidden;

/** Test seam: forgets everything without deleting. */
export function resetPendingDeletes() {
  for (const entry of pending.values()) clearTimeout(entry.timer);
  pending.clear();
  removed.clear();
  replaying.clear();
  writeStored([]);
  emit();
}

/**
 * Whether Delete can take a clip out of Temp (2.46): it still has its temporary audio and nothing of it
 * was kept. A kept clip stays in Soundpad for good, so Delete would not remove it. While the Temp list
 * is on its way, the companion decides.
 */
export function clipDeletable(asset: TempAsset | undefined) {
  return (
    !asset || (asset.hasTemporary && !asset.variants.some((v) => v.permanent || v.legacyApproval))
  );
}
