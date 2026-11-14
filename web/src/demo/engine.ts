import type { DemoEngine } from './model';
import {
  applyEdit,
  base64,
  blockPeaks,
  frameCount,
  lengthMs,
  loopLevels,
  loopSlice,
  reverse,
  slice,
  waveformLevels,
  wav,
  type ClipEdit,
  type Level,
  type Pcm,
} from './dsp';

/** Lets the click that asked for heavy work finish painting before the work starts. */
const breath = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/**
 * The example's audio, played by Web Audio in this browser tab (never through Soundpad). One clip
 * plays at a time, like Soundpad: a new start replaces the old one at once.
 */
export class WebAudioEngine implements DemoEngine {
  private clips = new Map<string, { pcm: Pcm; buffer: AudioBuffer }>();
  private levels = new Map<string, Level[]>();
  private source?: AudioBufferSourceNode;
  private generation = 0;
  private current?: {
    clip: string;
    /** Context time at which `offsetMs` was playing. */
    at: number;
    offsetMs: number;
    endMs: number | null;
    repeat: boolean;
    fromMs: number;
    paused: boolean;
    ended: () => void;
  };
  private loop?: { pcm: Pcm; peaks: ReturnType<typeof blockPeaks> };
  private output: GainNode;

  constructor(private context: AudioContext) {
    this.output = context.createGain();
    this.output.gain.value = 0.9;
    this.output.connect(context.destination);
  }

  get rate() {
    return this.context.sampleRate;
  }

  /** Adds decoded audio as a clip. */
  add(clip: string, buffer: AudioBuffer) {
    const pcm: Pcm = {
      rate: buffer.sampleRate,
      channels: Array.from({ length: buffer.numberOfChannels }, (_, i) => buffer.getChannelData(i)),
    };
    this.clips.set(clip, { pcm, buffer });
    this.levels.delete(clip);
    return lengthMs(pcm);
  }

  pcm(clip: string) {
    return this.clips.get(clip)?.pcm;
  }

  private store(clip: string, pcm: Pcm) {
    const buffer = this.context.createBuffer(
      Math.max(1, pcm.channels.length),
      Math.max(1, frameCount(pcm)),
      pcm.rate,
    );
    pcm.channels.forEach((c, i) => buffer.copyToChannel(c as Float32Array<ArrayBuffer>, i));
    return this.add(clip, buffer);
  }

  /** Sets Mimic's loop, whose block peaks draw the live buffer. */
  setLoop(pcm: Pcm) {
    this.loop = { pcm, peaks: blockPeaks(pcm) };
  }

  duration(clip: string) {
    const entry = this.clips.get(clip);
    return entry ? lengthMs(entry.pcm) : 0;
  }

  play(clip: string, fromMs: number, endMs: number | null, repeat: boolean, ended: () => void) {
    if (!this.clips.has(clip)) throw Error('That sound has no audio in the example.');
    this.current = { clip, at: 0, offsetMs: fromMs, endMs, repeat, fromMs, paused: false, ended };
    this.startSource(fromMs);
  }

  /** (Re)starts the current clip at `offsetMs`. */
  private startSource(offsetMs: number) {
    this.silence();
    const current = this.current;
    const entry = current && this.clips.get(current.clip);
    if (!current || !entry) return;
    if (this.context.state === 'suspended') void this.context.resume().catch(() => {});
    const source = this.context.createBufferSource();
    source.buffer = entry.buffer;
    source.connect(this.output);
    const mine = ++this.generation;
    const length = lengthMs(entry.pcm);
    if (current.repeat && current.endMs !== null) {
      source.loop = true;
      source.loopStart = current.fromMs / 1000;
      source.loopEnd = current.endMs / 1000;
      source.start(0, offsetMs / 1000);
    } else {
      const end = current.endMs ?? length;
      source.start(0, offsetMs / 1000, Math.max(0.001, (end - offsetMs) / 1000));
      source.onended = () => {
        if (mine !== this.generation || this.current !== current) return;
        this.source = undefined;
        this.current = undefined;
        current.ended();
      };
    }
    current.at = this.context.currentTime;
    current.offsetMs = offsetMs;
    this.source = source;
  }

  /** Stops the sound without forgetting what was playing. */
  private silence() {
    this.generation++;
    const source = this.source;
    this.source = undefined;
    if (!source) return;
    source.onended = null;
    try {
      source.stop();
    } catch {
      /* Never started or already stopped. */
    }
    source.disconnect();
  }

  stop() {
    this.silence();
    this.current = undefined;
  }

  pause() {
    const current = this.current;
    if (!current || current.paused) return;
    const at = this.position();
    this.silence();
    current.offsetMs = at;
    current.paused = true;
  }

  resume() {
    const current = this.current;
    if (!current || !current.paused) return;
    current.paused = false;
    this.startSource(current.offsetMs);
  }

  seek(ms: number) {
    const current = this.current;
    if (!current) return;
    if (current.paused) current.offsetMs = ms;
    else this.startSource(ms);
  }

  position() {
    const current = this.current;
    if (!current) return 0;
    if (current.paused) return current.offsetMs;
    const elapsed = Math.max(0, (this.context.currentTime - current.at) * 1000);
    const at = current.offsetMs + elapsed;
    if (current.repeat && current.endMs !== null && at > current.endMs) {
      const span = current.endMs - current.fromMs;
      return span > 0 ? current.fromMs + ((at - current.fromMs) % span) : current.fromMs;
    }
    return Math.min(at, current.endMs ?? this.duration(current.clip));
  }

  private need(clip: string) {
    const entry = this.clips.get(clip);
    if (!entry) throw Error('That sound has no audio in the example.');
    return entry.pcm;
  }

  async derive(target: string, source: string, edit: ClipEdit) {
    await breath();
    return this.store(target, applyEdit(this.need(source), edit));
  }

  async reverse(target: string, source: string) {
    await breath();
    return this.store(target, reverse(this.need(source)));
  }

  async fromLoop(target: string, fromMs: number, toMs: number) {
    if (!this.loop) throw Error('Mimic has nothing to listen to in the example.');
    await breath();
    return this.store(target, loopSlice(this.loop.pcm, fromMs, toMs));
  }

  loopLevels(fromMs: number, toMs: number, count: number) {
    return this.loop
      ? loopLevels(this.loop.peaks, fromMs, toMs, count)
      : new Array<number>(count).fill(0);
  }

  async decode(target: string, bytes: Uint8Array) {
    const copy = bytes.slice().buffer;
    const buffer = await this.context.decodeAudioData(copy);
    return this.add(target, buffer);
  }

  async previewClip(source: string, edit: ClipEdit, maxMs: number) {
    await breath();
    const made = applyEdit(this.need(source), edit);
    const part = slice(made, 0, maxMs);
    return { wav: base64(wav(part)), seconds: lengthMs(part) / 1000 };
  }

  async previewLoop(fromMs: number, toMs: number) {
    if (!this.loop) throw Error('Mimic has nothing to listen to in the example.');
    await breath();
    const part = loopSlice(this.loop.pcm, fromMs, toMs);
    return { wav: base64(wav(part)), seconds: lengthMs(part) / 1000 };
  }

  waveform(clip: string) {
    const entry = this.clips.get(clip);
    if (!entry) return null;
    let levels = this.levels.get(clip);
    if (!levels) {
      levels = waveformLevels(entry.pcm);
      this.levels.set(clip, levels);
    }
    return levels;
  }

  forget(clip: string) {
    if (this.current?.clip === clip) this.stop();
    this.clips.delete(clip);
    this.levels.delete(clip);
  }

  close() {
    this.stop();
    this.clips.clear();
    this.levels.clear();
    this.loop = undefined;
    void this.context.close().catch(() => {});
  }
}
