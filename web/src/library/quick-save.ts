import { z } from 'zod';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { refreshTempAssets, type TempAsset } from '../temp-assets';
import { siteRelaysQuickSave, supports } from '../version';
import { refreshLibrary, requestOpen } from './voice-store';

/**
 * One-press Save (2.46, from the owner's feedback "just save"): a clip in Temp, a Mimic selection or a draft
 * goes into the voice library at once, kept where Keep puts files (the host's Keep settings), under its own
 * name (its words, else where and when it was made). No confirmation, naming or folder prompt; the notice
 * says where it went and offers Undo and Rename.
 */

/** What libraryKeep and a draft's Keep in voice library answer (2.46 adds the name and where it went). */
export const savedSchema = z.object({
  recordingId: z.string().min(1).max(64),
  title: z.string().max(400).nullish(),
  added: z.boolean().optional(),
  /** The Soundpad category it is in. */
  category: z.string().max(400).nullish(),
  /** The host's folder for kept files (never sent to guests). */
  folder: z.string().max(2000).nullish(),
});
export type Saved = z.infer<typeof savedSchema>;

/**
 * Whether this page may Save in one press: a 2.46 companion; a guest also needs the host's guest Keep and a
 * host page new enough to relay the command (an older one's strict check would drop the guest).
 */
export function canQuickSave(
  state: Pick<Snapshot, 'companionVersion' | 'permissions'>,
  host: boolean,
  hostSite: string,
) {
  if (!supports(state.companionVersion, 'quickSave')) return false;
  return host || (state.permissions.guestKeep === true && siteRelaysQuickSave(hostSite));
}

/** The notice's words: the name it was saved under and where it went. */
export function savedMessage(saved: Omit<Saved, 'recordingId'>, fallback: string) {
  const name = saved.title?.trim() || fallback;
  if (saved.added === false) return `“${name}” is already in the voice library.`;
  const where = saved.category ? ` and Soundpad’s “${saved.category}” category` : '';
  const file = saved.folder ? ` Its file is in ${saved.folder}.` : '';
  return `Saved “${name}” to the voice library${where}.${file}`;
}

/** Takes a save back: out of the voice library again. The kept sound stays in Soundpad (nothing is deleted). */
async function undoSave(recordingId: string) {
  try {
    await controller.run('libraryRemove', { recordingIds: [recordingId] });
    controller.notify(
      'Taken out of the voice library. The kept sound stays in Soundpad.',
      'success',
    );
  } catch {
    /* The controller shows the refusal. */
  } finally {
    void refreshLibrary();
  }
}

/**
 * Says where a save went. Undo and Rename when this person may change the library (Rename opens the
 * recording in Voices with its title ready to type); otherwise Open.
 */
export function announceSaved(saved: Saved, fallback: string, canEdit: boolean) {
  const message = savedMessage(saved, fallback);
  const id = saved.recordingId;
  if (!canEdit) {
    controller.notify(message, 'success', {
      label: 'Open',
      run: () => requestOpen({ kind: 'recording', recordingId: id }),
    });
    return;
  }
  const rename = {
    label: 'Rename',
    run: () => requestOpen({ kind: 'recording', recordingId: id, rename: true }),
  };
  if (saved.added === false) controller.notify(message, 'success', rename);
  else
    controller.notify(message, 'success', { label: 'Undo', run: () => void undoSave(id) }, rename);
}

/** Saves a clip that is in Temp (Just mimicked, Temp, Voices) in one press. Throws on a refusal (already shown). */
export async function saveClip(assetId: string, name: string, canEdit: boolean) {
  try {
    const saved = savedSchema.parse(await controller.run('libraryKeep', { assetId }));
    announceSaved(saved, name, canEdit);
    return saved;
  } finally {
    refreshTempAssets();
    void refreshLibrary();
  }
}

/** Clips that can be saved in one press (2.46): Mimic clips, uploads and copies still only in Temp. */
export function unsavedClips(assets: TempAsset[], max = 8) {
  return assets
    .filter(
      (a) =>
        a.origin === 'upload' &&
        a.state !== 'removed' &&
        a.hasTemporary &&
        a.variants.some(
          (v) =>
            v.orientation === 'normal' &&
            v.current &&
            !v.permanent &&
            v.state === 'ready' &&
            !!v.nativeLabel,
        ),
    )
    .slice(-max)
    .reverse();
}
