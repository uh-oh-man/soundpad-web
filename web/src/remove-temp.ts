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
      // Quiet: an expected "Stop playback" refusal is retried below; only a final failure is shown.
      await controller.run('removeAudio', { assetId }, { quiet: true });
      return;
    } catch (e) {
      if (attempt >= 4 || !(e instanceof Error) || !/Stop playback/i.test(e.message)) {
        controller.notify(e instanceof Error ? e.message : 'Could not remove it.', 'error');
        throw e;
      }
      // The companion says it is playing even if this page hadn't heard yet (Undo pressed right after
      // Mimic & play): stop it, then try again once Soundpad reports the stop.
      if (attempt === 0) await controller.run('stop');
      await new Promise((r) => setTimeout(r, 400));
    }
  }
}
