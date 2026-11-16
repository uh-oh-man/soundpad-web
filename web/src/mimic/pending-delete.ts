import { useSyncExternalStore } from 'react';
import { noticeShown, notify, retireNoticeAction } from '../notifications';
import type { TempAsset } from '../temp-assets';

/**
 * Delete with Undo (2.46) for Mimic's clips and drafts. The companion can't take either back: removing a
 * Temp clip deletes its temporary file, and discarding a draft lets go of the history it held. So Delete
 * hides the item on this page at once and shows a notice with Undo; the companion is asked only once that
 * notice has gone (it stays while the pointer or the keyboard is on it). Undo just shows the item again.
 * Leaving the page meanwhile deletes it at once, as asked. A refusal shows the item again (the caller's
 * `commit` says why).
 */
type Pending = {
  timer: ReturnType<typeof setTimeout>;
  notice: string;
  commit: () => Promise<unknown>;
};

/** How often a pending delete looks whether its notice has gone. */
export const CHECK_MS = 400;

const pending = new Map<string, Pending>();
/** Deleted at the companion: never shown again on this page, even before its next snapshot arrives. */
const removed = new Set<string>();
let hidden: ReadonlySet<string> = new Set();
const listeners = new Set<() => void>();
const emit = () => {
  hidden = new Set([...pending.keys(), ...removed]);
  for (const listener of listeners) listener();
};

function finish(key: string) {
  const entry = pending.get(key);
  if (!entry) return;
  clearTimeout(entry.timer);
  retireNoticeAction(entry.notice);
  entry.commit().then(
    () => {
      pending.delete(key);
      removed.add(key);
      emit();
    },
    () => {
      // Refused or failed: it is still there, so it shows again.
      pending.delete(key);
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
 * Hides `key` (an asset or draft id) now and runs `commit` once the Undo notice saying `message` has gone.
 * `onUndo` runs if Undo is pressed instead. A second Delete of the same item does nothing.
 */
export function deleteWithUndo(
  key: string,
  message: string,
  commit: () => Promise<unknown>,
  onUndo?: () => void,
) {
  if (pending.has(key) || removed.has(key)) return;
  const notice = notify(message, 'success', {
    label: 'Undo',
    run: () => {
      const entry = pending.get(key);
      if (!entry) return;
      clearTimeout(entry.timer);
      pending.delete(key);
      emit();
      onUndo?.();
    },
  });
  pending.set(key, { notice, commit, timer: setTimeout(() => check(key), CHECK_MS) });
  emit();
}

/** Asks the companion now for every delete still waiting (the page is going away). */
export function flushPendingDeletes() {
  for (const key of [...pending.keys()]) finish(key);
}
if (typeof window !== 'undefined') window.addEventListener('pagehide', flushPendingDeletes);

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
