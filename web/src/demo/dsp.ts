/**
 * Pure audio helpers for the example session. They work on plain sample arrays, so the unit tests use
 * them without a browser. The example makes every edited copy here, in the visitor's browser.
 */

export type Pcm = { rate: number; channels: Float32Array[] };
export type Effect = 'none' | 'chipmunk' | 'slowmo' | 'echo' | 'speed';
export type ClipEdit = {
  fromMs: number;
  toMs: number;
  effect: Effect;
  speed?: number;
  keepPitch?: boolean;
};

export const frameCount = (pcm: Pcm) => pcm.channels[0]?.length ?? 0;
export const lengthMs = (pcm: Pcm) => (frameCount(pcm) * 1000) / pcm.rate;
const map = (pcm: Pcm, f: (c: Float32Array) => Float32Array): Pcm => ({
  rate: pcm.rate,
  channels: pcm.channels.map(f),
});

export function slice(pcm: Pcm, fromMs: number, toMs: number): Pcm {
  const n = frameCount(pcm);
  const a = Math.max(0, Math.min(n, Math.round((fromMs * pcm.rate) / 1000)));
  const b = Math.max(a, Math.min(n, Math.round((toMs * pcm.rate) / 1000)));
  return map(pcm, (c) => c.slice(a, b));
}

export function reverse(pcm: Pcm): Pcm {
  return map(pcm, (c) => c.slice().reverse());
}

/** Tape-style speed change: faster is higher, slower is deeper (linear interpolation). */
export function resample(pcm: Pcm, speed: number): Pcm {
  const n = frameCount(pcm);
  const out = Math.max(1, Math.floor(n / speed));
  return map(pcm, (c) => {
    const result = new Float32Array(out);
    for (let i = 0; i < out; i++) {
      const at = i * speed,
        j = Math.floor(at),
        t = at - j;
      const a = c[Math.min(j, n - 1)] ?? 0,
        b = c[Math.min(j + 1, n - 1)] ?? 0;
      result[i] = a + (b - a) * t;
    }
    return result;
  });
}

/**
 * Speed change that keeps the pitch: overlap-add of 46 ms Hann-windowed grains, read at the new speed
 * and written at the old one. Simple and fast; voices sound a little rougher than the companion's
 * time-stretch, which is fine for an example.
 */
export function stretch(pcm: Pcm, speed: number): Pcm {
  const n = frameCount(pcm);
  const grain = Math.max(64, Math.round(pcm.rate * 0.046));
  const hop = Math.floor(grain / 2);
  const out = Math.max(1, Math.floor(n / speed));
  const window = new Float32Array(grain);
  for (let i = 0; i < grain; i++) window[i] = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / grain);
  const weight = new Float32Array(out + grain);
  for (let at = 0; at < out; at += hop)
    for (let i = 0; i < grain && at + i < weight.length; i++) weight[at + i] += window[i];
  return map(pcm, (c) => {
    const result = new Float32Array(out + grain);
    for (let at = 0; at < out; at += hop) {
      const from = Math.min(n - 1, Math.round(at * speed));
      for (let i = 0; i < grain; i++) {
        const j = from + i;
        if (j >= n) break;
        result[at + i] += c[j] * window[i];
      }
    }
    for (let i = 0; i < out; i++) if (weight[i] > 1e-3) result[i] /= weight[i];
    return result.subarray(0, out).slice();
  });
}

/** Four repeats 0.28 s apart, each half as loud, then scaled down if the sum would clip. */
export function echo(pcm: Pcm): Pcm {
  const n = frameCount(pcm);
  const delay = Math.round(pcm.rate * 0.28);
  const taps = 4;
  let peak = 0;
  const channels = pcm.channels.map((c) => {
    const result = new Float32Array(n + delay * taps);
    for (let t = 0, gain = 1; t <= taps; t++, gain *= 0.5)
      for (let i = 0; i < n; i++) result[i + t * delay] += c[i] * gain;
    for (const v of result) peak = Math.max(peak, Math.abs(v));
    return result;
  });
  if (peak > 0.99) for (const c of channels) for (let i = 0; i < c.length; i++) c[i] *= 0.99 / peak;
  return { rate: pcm.rate, channels };
}

export const MIN_SPEED = 0.05,
  MAX_SPEED = 20;
export const clampSpeed = (s: number | undefined) =>
  Math.min(MAX_SPEED, Math.max(MIN_SPEED, Number.isFinite(s) ? (s as number) : 1));

/** How many times faster an effect plays the audio (echo adds its tail instead). */
export function editFactor(edit: Pick<ClipEdit, 'effect' | 'speed'>) {
  return edit.effect === 'chipmunk'
    ? 1.5
    : edit.effect === 'slowmo'
      ? 0.7
      : edit.effect === 'speed'
        ? clampSpeed(edit.speed)
        : 1;
}

/** Length of an edited copy before it is made, to refuse one that would be too long. */
export function editedLengthMs(edit: ClipEdit) {
  const part = Math.max(0, edit.toMs - edit.fromMs);
  return edit.effect === 'echo' ? part + 4 * 280 : part / editFactor(edit);
}

export function applyEdit(pcm: Pcm, edit: ClipEdit): Pcm {
  const part = slice(pcm, edit.fromMs, edit.toMs);
  if (edit.effect === 'echo') return echo(part);
  const factor = editFactor(edit);
  if (factor === 1) return part;
  return edit.effect === 'speed' && edit.keepPitch ? stretch(part, factor) : resample(part, factor);
}

/** Interleaved (min, max) pairs per bin, like the companion's waveform levels. */
export type Level = { bins: number; peaks: number[] };

/** The companion's three waveform levels (512, 4096 and up to 32768 bins), scaled to the loudest sample. */
export function waveformLevels(pcm: Pcm, sizes = [512, 4096, 32768]): Level[] {
  const n = frameCount(pcm);
  let loudest = 0;
  for (const c of pcm.channels)
    for (let i = 0; i < n; i++) loudest = Math.max(loudest, Math.abs(c[i]));
  const scale = loudest > 0 ? 32767 / loudest : 0;
  return sizes.map((size) => {
    const bins = Math.max(1, Math.min(size, n));
    const peaks = new Array<number>(bins * 2);
    for (let b = 0; b < bins; b++) {
      const from = Math.floor((b * n) / bins),
        to = Math.max(from + 1, Math.floor(((b + 1) * n) / bins));
      let min = Infinity,
        max = -Infinity;
      for (const c of pcm.channels)
        for (let i = from; i < Math.min(to, n); i++) {
          const v = c[i];
          if (v < min) min = v;
          if (v > max) max = v;
        }
      if (min === Infinity) min = max = 0;
      peaks[b * 2] = Math.max(-32767, Math.min(32767, Math.round(min * scale)));
      peaks[b * 2 + 1] = Math.max(-32767, Math.min(32767, Math.round(max * scale)));
    }
    return { bins, peaks };
  });
}

/** A 16-bit PCM WAV file. */
export function wav(pcm: Pcm): Uint8Array {
  const n = frameCount(pcm),
    channels = Math.max(1, pcm.channels.length);
  const bytes = new Uint8Array(44 + n * channels * 2);
  const view = new DataView(bytes.buffer);
  const text = (at: number, s: string) => {
    for (let i = 0; i < s.length; i++) bytes[at + i] = s.charCodeAt(i);
  };
  text(0, 'RIFF');
  view.setUint32(4, 36 + n * channels * 2, true);
  text(8, 'WAVE');
  text(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, pcm.rate, true);
  view.setUint32(28, pcm.rate * channels * 2, true);
  view.setUint16(32, channels * 2, true);
  view.setUint16(34, 16, true);
  text(36, 'data');
  view.setUint32(40, n * channels * 2, true);
  let at = 44;
  for (let i = 0; i < n; i++)
    for (let ch = 0; ch < channels; ch++, at += 2) {
      const v = Math.max(-1, Math.min(1, pcm.channels[ch]?.[i] ?? 0));
      view.setInt16(at, Math.round(v * 32767), true);
    }
  return bytes;
}

export function base64(bytes: Uint8Array) {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000)
    text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

/** Mixes clips (first channel of each) onto a silent mono loop at the given offsets. */
export function mixLoop(rate: number, loopMs: number, parts: { pcm: Pcm; atMs: number }[]): Pcm {
  const n = Math.round((loopMs * rate) / 1000);
  const loop = new Float32Array(n);
  for (const { pcm, atMs } of parts) {
    const source = pcm.channels[0] ?? new Float32Array(0);
    const start = Math.round((atMs * rate) / 1000);
    // Clips are decoded at the same rate as the loop (the page's audio rate); others are stretched to fit.
    const step = pcm.rate / rate;
    for (let i = 0; start + i < n && i * step < source.length; i++)
      loop[start + i] += source[Math.floor(i * step)];
  }
  return { rate, channels: [loop] };
}

/** Samples from an endless repeat of `loop` between two times (ms); times before 0 are silence. */
export function loopSlice(loop: Pcm, fromMs: number, toMs: number): Pcm {
  const source = loop.channels[0];
  const n = source.length;
  const a = Math.round((fromMs * loop.rate) / 1000),
    b = Math.round((toMs * loop.rate) / 1000);
  const result = new Float32Array(Math.max(0, b - a));
  for (let i = 0; i < result.length; i++) {
    const at = a + i;
    result[i] = at < 0 ? 0 : source[at % n];
  }
  return { rate: loop.rate, channels: [result] };
}

/** Loudest sample per block of `blockMs`, for drawing a live buffer quickly. */
export function blockPeaks(pcm: Pcm, blockMs = 10) {
  const source = pcm.channels[0];
  const block = Math.max(1, Math.round((pcm.rate * blockMs) / 1000));
  const peaks = new Float32Array(Math.ceil(source.length / block));
  for (let i = 0; i < source.length; i++) {
    const v = Math.abs(source[i]);
    const b = Math.floor(i / block);
    if (v > peaks[b]) peaks[b] = v;
  }
  return { samples: source, rate: pcm.rate, block, peaks };
}

/**
 * Mimic's level bars (0 to 1000) for [fromMs, toMs) of an endless loop, from its block peaks. Bars
 * before 0 ms (before listening started) are empty.
 */
export function loopLevels(
  { samples, rate, block, peaks }: ReturnType<typeof blockPeaks>,
  fromMs: number,
  toMs: number,
  count: number,
) {
  const levels: number[] = [];
  const width = (toMs - fromMs) / count;
  for (let i = 0; i < count; i++) {
    const start = Math.max(0, Math.round(((fromMs + i * width) * rate) / 1000));
    const end = Math.round(((fromMs + (i + 1) * width) * rate) / 1000);
    let loudest = 0;
    for (let at = start; samples.length && at < end;) {
      const offset = at % samples.length;
      // Reuse whole blocks. On either edge, read only the actual samples inside this bin.
      if (offset % block === 0 && offset + block <= samples.length && at + block <= end) {
        loudest = Math.max(loudest, peaks[offset / block]);
        at += block;
      } else {
        loudest = Math.max(loudest, Math.abs(samples[offset]));
        at++;
      }
    }
    levels.push(Math.max(0, Math.min(1000, Math.round(loudest * 1000))));
  }
  return levels;
}

/**
 * One part of a mix (the example's sentences and palette): a range of a clip, placed at `atMs`, with a gain,
 * fades, a speed (tape-style) and optionally reversed; or a sine tone (a bleep) of `toMs - fromMs`.
 */
export type MixPart = {
  clip?: string;
  tone?: number;
  atMs: number;
  fromMs: number;
  toMs: number;
  gain?: number;
  fadeInMs?: number;
  fadeOutMs?: number;
  speed?: number;
  reverse?: boolean;
};

/** Mixes parts onto one mono track at `rate`; a clip `pcm` does not know is left out. */
export function mixParts(
  rate: number,
  parts: MixPart[],
  pcm: (clip: string) => Pcm | undefined,
): Pcm {
  const pieces = parts.flatMap((part) => {
    let audio: Float32Array;
    if (part.tone) {
      const n = Math.max(0, Math.round(((part.toMs - part.fromMs) * rate) / 1000));
      audio = new Float32Array(n);
      for (let i = 0; i < n; i++) audio[i] = Math.sin((2 * Math.PI * part.tone * i) / rate);
    } else {
      const source = part.clip ? pcm(part.clip) : undefined;
      if (!source) return [];
      let cut = slice(source, part.fromMs, part.toMs);
      if (part.reverse) cut = reverse(cut);
      // To the mix's rate, and to the part's speed, in one resample.
      const speed = (part.speed && part.speed > 0 ? part.speed : 1) * (source.rate / rate);
      if (Math.abs(speed - 1) > 1e-6) cut = resample(cut, speed);
      audio = cut.channels[0] ?? new Float32Array(0);
    }
    return [{ part, audio }];
  });
  const end = pieces.reduce(
    (m, { part, audio }) => Math.max(m, Math.round((part.atMs * rate) / 1000) + audio.length),
    0,
  );
  const out = new Float32Array(Math.max(1, end));
  for (const { part, audio } of pieces) {
    const start = Math.round((part.atMs * rate) / 1000);
    const fadeIn = Math.round(((part.fadeInMs ?? 0) * rate) / 1000),
      fadeOut = Math.round(((part.fadeOutMs ?? 0) * rate) / 1000);
    const gain = part.gain ?? 1;
    for (let i = 0; i < audio.length && start + i < out.length; i++) {
      let g = gain;
      if (fadeIn && i < fadeIn) g *= i / fadeIn;
      if (fadeOut && i > audio.length - fadeOut) g *= (audio.length - i) / fadeOut;
      out[start + i] += audio[i] * g;
    }
  }
  let peak = 0;
  for (const v of out) peak = Math.max(peak, Math.abs(v));
  if (peak > 0.99) for (let i = 0; i < out.length; i++) out[i] *= 0.99 / peak;
  return { rate, channels: [out] };
}
