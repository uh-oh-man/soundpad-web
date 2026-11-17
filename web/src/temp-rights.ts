import { useTempAssets, removedFromTemp, type TempAsset, type TempData } from './temp-assets';
import { useDeleted } from './mimic/pending-delete';
import { formatMb } from './mimic/drafts';
import { supports } from './version';
import type { Snapshot } from './protocol';

/**
 * Who may take what out of Temp, and what Temp lists, shared by the Temp list and Mimic's recent clips so the
 * two never disagree.
 */

/**
 * Whether the host's permissions let a guest remove their own Temp clips: while guests may upload, or (since
 * 2.46, `tempUndo`) while they may use Mimic. A 2.45 companion refuses a guest who may only use Mimic, so no
 * Remove is offered there.
 */
export function guestMayRemoveTemp(state: Pick<Snapshot, 'permissions' | 'companionVersion'>) {
  return (
    state.permissions.guestUploads !== false ||
    (state.permissions.guestMimic === true && supports(state.companionVersion, 'tempUndo'))
  );
}

/**
 * Whether a source is off the Temp list: a Remove pressed in Temp (`removing`), a Delete in Mimic still in its
 * Undo wait or done (`deleted`), or removed at the companion with its files on their way out.
 */
export function goneFromTemp(
  a: TempAsset,
  removing: ReadonlySet<string>,
  deleted: ReadonlySet<string>,
) {
  return removing.has(a.id) || deleted.has(a.id) || removedFromTemp(a);
}

/** The Temp list and what is hidden from it, from both stores of hidden sources (Temp's and Mimic's Delete). */
export function useTempList() {
  const temp = useTempAssets();
  const deleted = useDeleted();
  return { ...temp, gone: (a: TempAsset) => goneFromTemp(a, temp.hidden, deleted) };
}

/**
 * The line about removed files still on the host's PC (2.47, `tempWaiting`), or undefined when there are none.
 * With the real Soundpad a removed clip's file stays until Clear Temp or quitting, and still takes Temp's space.
 */
export function tempWaitingText(
  data: TempData | undefined,
  host: boolean,
  version: string | null | undefined,
) {
  const waiting = data?.manager.waiting;
  if (!waiting || waiting.files <= 0 || !supports(version, 'tempWaiting')) return undefined;
  const files = `${waiting.files} removed file${waiting.files === 1 ? '' : 's'}`;
  return `Waiting to be freed: ${files} · ${formatMb(waiting.bytes)}. They still use Temp's space; ${
    host ? 'Clear all temporary audio' : "the host's Clear all temporary audio"
  } frees them (quitting the companion does too).`;
}
