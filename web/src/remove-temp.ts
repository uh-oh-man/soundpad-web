import { controller } from './controller';

/**
 * Removes one Temp source right after it was made, for Undo (2.28 Edit copy, 2.29 Mimic clips). The
 * companion never removes temporary audio that is playing, so a source still playing (Save & play,
 * Mimic & play) is stopped first. Host only: guests cannot remove Temp audio.
 */
export async function removeNewTempAudio(assetId: string) {
  const { state } = controller.getSnapshot();
  const sound = state.library.sounds.find((s) => s.assetId === assetId);
  if (sound && state.playback.soundId === sound.id && state.playback.status !== 'STOPPED')
    await controller.run('stop');
  for (let attempt = 0; ; attempt++) {
    try {
      await controller.run('removeAudio', { assetId });
      return;
    } catch (e) {
      // Soundpad may report the stop a moment late; try again briefly before giving up.
      if (attempt >= 4 || !(e instanceof Error) || !/Stop playback/i.test(e.message)) throw e;
      await new Promise((r) => setTimeout(r, 400));
    }
  }
}
