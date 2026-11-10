import { controller } from './controller';

const PLAYING_REFUSAL = /Stop playback/i;
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** Whether the page last heard this Temp source playing, or some other sound. */
function playing(assetId: string) {
  const { state } = controller.getSnapshot();
  const sound = state.library.sounds.find((s) => s.assetId === assetId);
  const on = state.playback.status !== 'STOPPED' && !!state.playback.soundId;
  const it = on && !!sound && state.playback.soundId === sound.id;
  return { it, other: on && !it };
}

/**
 * Removes one Temp source right after it was made, for Undo (2.28 Edit copy, 2.29 Mimic clips). Host
 * only: guests cannot remove Temp audio.
 *
 * The companion removes nothing while any sound plays. If the source itself is playing (Save & play,
 * Mimic & play) it is stopped first; if something else is playing, that sound is left alone and the
 * refusal is shown (2.29.3: Undo used to stop whatever was playing).
 */
export async function removeNewTempAudio(assetId: string) {
  if (playing(assetId).it) await controller.run('stop');
  for (let attempt = 0; ; attempt++) {
    try {
      // Quiet: a "Stop playback" refusal for this source's own playback is handled below.
      await controller.run('removeAudio', { assetId }, { quiet: true });
      return;
    } catch (e) {
      const forPlayback = e instanceof Error && PLAYING_REFUSAL.test(e.message);
      if (forPlayback && attempt < 4) {
        // Undo pressed right after a "& play" can beat the playback report to this page: wait for it.
        for (let i = 0; i < 6 && !playing(assetId).it && !playing(assetId).other; i++)
          await wait(250);
        if (playing(assetId).it) {
          await controller.run('stop');
          await wait(400);
          continue;
        }
        // Something else is playing (or the stop has not landed yet on a later attempt).
        if (attempt === 0 || playing(assetId).other) {
          controller.notify((e as Error).message, 'error');
          throw e;
        }
        await wait(400);
        continue;
      }
      controller.notify(e instanceof Error ? e.message : 'Could not remove it.', 'error');
      throw e;
    }
  }
}
