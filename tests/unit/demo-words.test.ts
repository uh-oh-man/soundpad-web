import { describe, expect, it } from 'vitest';
import { DEMO_SOUNDS } from '../../web/src/demo/library';

// The example's speech clips carry hand-placed word times. The audio-quality evaluation (2.45,
// companion/harness/AudioQualityEval.cs --audio-quality-eval) reads them from library.ts and scores them against
// edges checked on spectrograms; these checks keep the list itself well formed.
describe('example word times', () => {
  const speech = DEMO_SOUNDS.filter((s) => s.words?.length);

  it('are in order, do not overlap and stay inside the clip', () => {
    expect(speech.map((s) => s.key)).toEqual(['apollo11-small-step', 'apollo11-eagle-landed']);
    for (const sound of speech) {
      const words = sound.words!;
      words.forEach((w, i) => {
        expect(w.endMs - w.startMs, `${sound.key} ${w.w}`).toBeGreaterThanOrEqual(100);
        expect(w.startMs).toBeGreaterThanOrEqual(0);
        expect(w.endMs).toBeLessThanOrEqual(sound.durationMs);
        if (i > 0)
          expect(w.startMs, `${sound.key} ${w.w}`).toBeGreaterThanOrEqual(words[i - 1].endMs);
      });
    }
  });

  it('put each word of the eagle clip where it is spoken (2.45 correction)', () => {
    const eagle = speech.find((s) => s.key === 'apollo11-eagle-landed')!.words!;
    const at = (word: string) => eagle.find((w) => w.w.replace(/[.,]$/, '') === word)!;
    // Up to 2.44 these were 230-270 ms late: "Base" played the hiss after it, "Tranquility" began mid-word.
    expect(at('Tranquility').startMs).toBeLessThan(600);
    expect(at('Base').startMs).toBeLessThan(1250);
    expect(at('Base').endMs).toBeLessThan(1450);
    expect(at('here').startMs).toBeGreaterThan(1400);
  });
});
