import { z } from 'zod';
import { controller } from './controller';
import { supports } from './version';
import { hideTemp, refreshTempAssets, showTemp } from './temp-assets';

/**
 * Removing Temp audio from the Temp list (2.46, from the 2.45 feedback "it should do it and not be in the
 * list, there should be no pending"). The source leaves the list at once; if the companion refuses, it comes
 * back with the reason. A companion that can bring it back (tempUndo) gets an Undo on the notice instead of a
 * question first. The companion stops the source first only when it is what it played itself
 * ("stopIfPlaying"); anything else keeps playing, and the files go once Soundpad is quiet.
 */
const removedMine = z.object({ removed: z.number().int(), assetIds: z.array(z.string()) });
const restored = z.object({ restored: z.number().int(), assetIds: z.array(z.string()) });

const reason = (e: unknown) => (e instanceof Error && e.message ? e.message : 'Try again.');
/** Whether the companion can undo a removal (2.46). */
export const canUndoTempRemoval = () =>
  supports(controller.getSnapshot().state.companionVersion, 'tempUndo');
const clips = (n: number) => `${n} clip${n === 1 ? '' : 's'}`;

async function undo(ids: string[], what: string) {
  try {
    const answer = await controller.run(
      'tempRestore',
      ids.length === 1 ? { assetId: ids[0] } : { assetIds: ids },
      { quiet: true },
    );
    const back = restored.safeParse(answer);
    showTemp(back.success ? back.data.assetIds : ids);
    controller.notify(`${what} back in Temp.`, 'success');
  } catch (e) {
    controller.notify(`Could not bring it back: ${reason(e)}`, 'error');
  }
  refreshTempAssets();
}

/** Removes one source (the host's "Remove temporary audio", a guest's "Remove my clip"). */
export async function removeTempSource(asset: { id: string; name: string }) {
  hideTemp([asset.id]);
  try {
    await controller.run(
      'removeAudio',
      { assetId: asset.id, action: 'stopIfPlaying' },
      { quiet: true },
    );
  } catch (e) {
    showTemp([asset.id]);
    refreshTempAssets();
    controller.notify(`Could not remove “${asset.name}”: ${reason(e)}`, 'error');
    return false;
  }
  refreshTempAssets();
  controller.notify(
    `Removed “${asset.name}” from Temp.`,
    'success',
    canUndoTempRemoval()
      ? { label: 'Undo', run: () => void undo([asset.id], `“${asset.name}” is`) }
      : undefined,
  );
  return true;
}

/**
 * A guest's "Remove all my Temp clips" (2.46, gate guestTempClear): every temporary source of theirs, which
 * the companion decides. `shown` (their clips on the list) leave it at once.
 */
export async function removeAllMyTemp(shown: readonly string[]) {
  hideTemp(shown);
  let ids: string[];
  try {
    const answer = removedMine.parse(
      await controller.run('tempRemoveMine', { action: 'stopIfPlaying' }, { quiet: true }),
    );
    ids = answer.assetIds;
  } catch (e) {
    showTemp(shown);
    refreshTempAssets();
    controller.notify(`Could not remove your clips: ${reason(e)}`, 'error');
    return false;
  }
  hideTemp(ids);
  // Anything shown that the companion did not count as theirs stays on the list.
  showTemp(shown.filter((id) => !ids.includes(id)));
  refreshTempAssets();
  controller.notify(
    ids.length ? `Removed your ${clips(ids.length)} from Temp.` : 'You have no clips in Temp.',
    'success',
    ids.length && canUndoTempRemoval()
      ? {
          label: 'Undo',
          run: () =>
            void undo(ids, ids.length === 1 ? 'Your clip is' : `Your ${clips(ids.length)} are`),
        }
      : undefined,
  );
  return true;
}
