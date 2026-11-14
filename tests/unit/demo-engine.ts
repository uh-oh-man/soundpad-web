/**
 * The example session's test engine and setup, shared by the demo unit tests (Stage G, WP-C). The engine plays
 * nothing: it remembers what it was asked to play, and makes clips when the test says so.
 */
import { expect } from 'vitest';
import { DemoModel, type DemoEngine, type DemoSessionInfo } from '../../web/src/demo/model';
import { DEMO_SOUNDS } from '../../web/src/demo/library';
import { editedLengthMs, waveformLevels, type ClipEdit } from '../../web/src/demo/dsp';
import { command, type Command, type Snapshot } from '../../web/src/protocol';
import { ALEX } from '../../web/src/demo/model';

export const HOST = { id: '33333333-3333-4333-8333-333333333333', name: 'Host' };
export const id = (key: string) => DemoModel.soundId(key);

/** Plays nothing: it remembers what it was asked to play, and makes clips when the test says so. */
export class FakeEngine implements DemoEngine {
  lengths = new Map(DEMO_SOUNDS.map((s) => [DemoModel.soundId(s.key), s.durationMs]));
  now?: { clip: string; from: number; end: number | null; repeat: boolean; ended: () => void };
  at = 0;
  paused = false;
  closed = false;
  /** When set, clips are made only when `finish()` is called. */
  hold = false;
  private waiting: (() => void)[] = [];
  finish() {
    for (const done of this.waiting.splice(0)) done();
  }
  private make(target: string, length: number) {
    return new Promise<number>((resolve) => {
      const done = () => {
        this.lengths.set(target, length);
        resolve(length);
      };
      if (this.hold) this.waiting.push(done);
      else done();
    });
  }
  duration(clip: string) {
    return this.lengths.get(clip) ?? 0;
  }
  play(clip: string, from: number, end: number | null, repeat: boolean, ended: () => void) {
    this.now = { clip, from, end, repeat, ended };
    this.at = from;
    this.paused = false;
  }
  stop() {
    this.now = undefined;
  }
  pause() {
    this.paused = true;
  }
  resume() {
    this.paused = false;
  }
  seek(ms: number) {
    this.at = ms;
  }
  position() {
    return this.at;
  }
  derive(target: string, source: string, edit: ClipEdit) {
    expect(this.lengths.has(source)).toBe(true);
    return this.make(target, editedLengthMs(edit));
  }
  reverse(target: string, source: string) {
    return this.make(target, this.duration(source));
  }
  /** Every part of Mimic's loop cut into a clip, in loop milliseconds. */
  cuts: { from: number; to: number }[] = [];
  fromLoop(target: string, from: number, to: number) {
    this.cuts.push({ from, to });
    return this.make(target, to - from);
  }
  loopLevels(_from: number, _to: number, count: number) {
    return new Array<number>(count).fill(500);
  }
  async decode(target: string, bytes: Uint8Array) {
    if (!bytes.length) throw Error('empty');
    this.lengths.set(target, 1000);
    return 1000;
  }
  async previewClip(_source: string, edit: ClipEdit) {
    return { wav: 'UklGRg==', seconds: editedLengthMs(edit) / 1000 };
  }
  previews: { from: number; to: number }[] = [];
  async previewLoop(from: number, to: number) {
    this.previews.push({ from, to });
    return { wav: 'UklGRg==', seconds: (to - from) / 1000 };
  }
  waveform(clip: string) {
    return this.lengths.has(clip)
      ? waveformLevels({ rate: 8000, channels: [new Float32Array(8000)] })
      : null;
  }
  forget(clip: string) {
    this.lengths.delete(clip);
  }
  close() {
    this.closed = true;
  }
}

export function setup({ visible = true } = {}) {
  const engine = new FakeEngine();
  const states: Snapshot[] = [];
  const sessions: DemoSessionInfo[] = [];
  const notices: string[] = [];
  let shown = visible;
  const model = new DemoModel(
    engine,
    {
      state: (s) => states.push(s),
      session: (s) => sessions.push(s),
      notify: (message) => notices.push(message),
    },
    { random: () => 0, visible: () => shown },
  );
  const send = (op: Command['op'], fields: Partial<Command> = {}) =>
    model.send(command(op, fields), HOST);
  const asAlex = (op: Command['op'], fields: Partial<Command> = {}) =>
    model.send(command(op, fields), ALEX.person, ALEX);
  return {
    model,
    engine,
    send,
    asAlex,
    notices,
    state: () => states[states.length - 1],
    session: () => sessions[sessions.length - 1],
    setVisible: (on: boolean) => (shown = on),
  };
}
