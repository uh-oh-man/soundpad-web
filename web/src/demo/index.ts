import { DEMO_SOUNDS, MIMIC_LOOP_MS, MIMIC_PLACEMENTS } from './library';
import { DemoModel, type DemoHooks } from './model';
import { WebAudioEngine } from './engine';
import { mixLoop } from './dsp';

/**
 * Starts the example session. Loaded on demand when the visitor presses "Try the example", so
 * the normal host and guest page never downloads it. It fetches the example sounds and their artwork
 * from `demo/` next to the page and decodes them in this browser; nothing else goes over the network.
 *
 * Call it from the click itself: browsers let a page start audio only after a press.
 */
export async function startDemo(hooks: DemoHooks): Promise<DemoModel> {
  const Context =
    window.AudioContext ??
    (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
  if (!Context) throw Error('This browser cannot play audio from a web page.');
  const context = new Context();
  // Created inside the click, so the browser lets it play; resumed again in case it starts suspended.
  void context.resume().catch(() => {});
  const engine = new WebAudioEngine(context);
  try {
    const base = `${import.meta.env.BASE_URL}demo/`;
    const art: Record<string, string> = {};
    // A sound this browser cannot load or decode is left out; the example needs at least one.
    const loaded = await Promise.allSettled(
      DEMO_SOUNDS.map(async (sound) => {
        const response = await fetch(base + sound.file);
        if (!response.ok) throw Error(`Could not load ${sound.title} (${response.status}).`);
        const buffer = await context.decodeAudioData(await response.arrayBuffer());
        engine.add(DemoModel.soundId(sound.key), buffer);
        if (sound.art) {
          // Artwork is optional: a missing picture leaves the sound with its waveform tile.
          const image = await fetch(base + sound.art).catch(() => null);
          const url = image?.ok ? await dataUrl(await image.blob()).catch(() => '') : '';
          if (url) art[sound.key] = url;
        }
      }),
    );
    const failed = loaded.find((r): r is PromiseRejectedResult => r.status === 'rejected');
    if (failed && loaded.every((r) => r.status === 'rejected'))
      throw failed.reason instanceof Error
        ? failed.reason
        : Error('The example sounds did not load.');
    engine.setLoop(
      mixLoop(
        engine.rate,
        MIMIC_LOOP_MS,
        MIMIC_PLACEMENTS.flatMap(({ key, atMs }) => {
          const pcm = engine.pcm(DemoModel.soundId(key));
          return pcm ? [{ pcm, atMs }] : [];
        }),
      ),
    );
    return new DemoModel(engine, hooks, { art });
  } catch (e) {
    engine.close();
    throw e;
  }
}

function dataUrl(blob: Blob) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error ?? Error('Could not read the artwork.'));
    reader.readAsDataURL(blob);
  });
}

export { DemoModel };
