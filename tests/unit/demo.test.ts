import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ALEX,
  ALEX_GAP_MS,
  DemoModel,
  GUEST_JOIN_MS,
  MAX_COPY_MS,
  SAM,
  SAM_ASKS_MS,
  UNAVAILABLE,
  type DemoEngine,
  type DemoSessionInfo,
} from '../../web/src/demo/model';
import { DEMO_SOUNDS, mimicLoopLines, wordLines } from '../../web/src/demo/library';
import {
  applyEdit,
  editedLengthMs,
  loopLevels,
  loopSlice,
  resample,
  reverse,
  stretch,
  waveformLevels,
  wav,
  type ClipEdit,
  type Pcm,
} from '../../web/src/demo/dsp';
import {
  command,
  snapshotSchema,
  statisticsSchema,
  waveformSchema,
  type Command,
  type Snapshot,
} from '../../web/src/protocol';
import { DEFAULT_POLICY } from '../../web/src/network/policy';

const HOST = { id: '33333333-3333-4333-8333-333333333333', name: 'Host' };
const id = (key: string) => DemoModel.soundId(key);

/** Plays nothing: it remembers what it was asked to play, and makes clips when the test says so. */
class FakeEngine implements DemoEngine {
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
  fromLoop(target: string, from: number, to: number) {
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
  async previewLoop(from: number, to: number) {
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

function setup({ visible = true } = {}) {
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

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

describe('example session model', () => {
  it('reports a library with categories and artwork in the companion snapshot format', () => {
    const model = new DemoModel(
      new FakeEngine(),
      { state: () => {}, session: () => {}, notify: () => {} },
      { art: { 'air-horn': 'data:image/png;base64,AAAA', boing: 'data:image/png;base64,BBBB' } },
    );
    const s = snapshotSchema.parse(model.snapshot());
    expect(s.library.sounds).toHaveLength(DEMO_SOUNDS.length);
    expect(s.library.categories.map((c) => c.name)).toEqual([
      'Reactions',
      'Impacts',
      'Cartoon',
      'Animals',
      'Music',
      'Speech',
    ]);
    expect(s.library.sounds.find((x) => x.id === id('air-horn'))?.icon).toMatch(/^data:image/);
    expect(s.library.categories[0].icon).toBe('data:image/png;base64,AAAA');
    expect(s.available).toBe(true);
    expect(s.companionVersion).toBe('2.37.0');
  });

  it('plays, pauses, seeks and stops, and the player hears the position', async () => {
    const { send, engine, state } = setup();
    const played = (await send('play', { soundId: id('rimshot') })) as { epoch: string };
    expect(engine.now?.clip).toBe(id('rimshot'));
    expect(state().playback).toMatchObject({
      status: 'PLAYING',
      soundId: id('rimshot'),
      requester: 'Host',
      epoch: played.epoch,
    });
    await send('pause', { soundId: id('rimshot'), epoch: played.epoch });
    expect(state().playback.status).toBe('PAUSED');
    expect(engine.paused).toBe(true);
    await send('seek', { soundId: id('rimshot'), epoch: played.epoch, positionMs: 900 });
    expect(state().playback).toMatchObject({ positionMs: 900, positionRevision: 1 });
    await send('pause', { soundId: id('rimshot'), epoch: played.epoch });
    expect(state().playback.status).toBe('PLAYING');
    // A seek aimed at playback that has been replaced is answered quietly.
    expect(
      await send('seek', { soundId: id('rimshot'), epoch: crypto.randomUUID(), positionMs: 5 }),
    ).toMatchObject({ superseded: true });
    await send('stop');
    expect(engine.now).toBeUndefined();
    expect(state().playback.status).toBe('STOPPED');
  });

  it('plays cue starts and regions, and stops when a sound ends', async () => {
    const { send, engine, state } = setup();
    await send('startAt', { soundId: id('applause'), positionMs: 2000 });
    expect(engine.now).toMatchObject({ from: 2000, end: null, repeat: false });
    await send('region', { soundId: id('applause'), positionMs: 1000, endMs: 3000, repeat: true });
    expect(engine.now).toMatchObject({ from: 1000, end: 3000, repeat: true });
    expect(state().playback).toMatchObject({ regionEndMs: 3000, repeat: true });
    await expect(
      send('region', { soundId: id('applause'), positionMs: 10, endMs: 20 }),
    ).rejects.toThrow(/at least 50 ms/);
    engine.now!.ended();
    expect(state().playback.status).toBe('STOPPED');
  });

  it('lets the newest click win over a copy that is still being made', async () => {
    const { send, engine, state } = setup();
    engine.hold = true;
    const reversed = send('reverse', { soundId: id('boing') });
    await Promise.resolve();
    await send('play', { soundId: id('whoosh') });
    engine.finish();
    expect(await reversed).toMatchObject({ superseded: true, reason: 'newer', played: false });
    expect(state().playback.soundId).toBe(id('whoosh'));
    // The reversed copy was still made, and a later reverse plays it at once.
    engine.hold = false;
    await send('reverse', { soundId: id('boing') });
    expect(state().library.sounds.find((s) => s.id === state().playback.soundId)?.name).toBe(
      'Boing (reversed)',
    );
  });

  it("Panic stops playback and holds guests' playback until the host resumes it", async () => {
    const { send, asAlex, engine, state } = setup();
    await send('play', { soundId: id('applause') });
    await expect(asAlex('panic', { action: 'enable' })).rejects.toThrow('disabled');
    expect(await send('panic', { action: 'enable' })).toEqual({ suspended: true });
    expect(engine.now).toBeUndefined();
    expect(state().playbackSuspended).toBe(true);
    expect(await asAlex('play', { soundId: id('rimshot') })).toMatchObject({
      superseded: true,
      reason: 'suspended',
    });
    expect(engine.now).toBeUndefined();
    await send('play', { soundId: id('rimshot') });
    expect(engine.now?.clip).toBe(id('rimshot'));
    await send('panic', { action: 'disable' });
    expect(state().playbackSuspended).toBe(false);
    expect(await asAlex('play', { soundId: id('boing') })).toMatchObject({ played: true });
  });

  it('keeps a Kick counter for the session that guests change only when allowed', async () => {
    const { send, asAlex, state } = setup();
    await send('counter', { delta: 1 });
    await send('counter', { delta: 1 });
    await send('counter', { delta: -1 });
    expect(state().counter).toBe(1);
    await send('counter', { delta: -1 });
    await send('counter', { delta: -1 });
    expect(state().counter).toBe(0);
    await expect(asAlex('counter', { delta: 1 })).rejects.toThrow('disabled');
    await send('permissions', {
      permissions: { ...state().permissions, guestCounter: true },
    });
    await asAlex('counter', { delta: 1 });
    expect(state().counter).toBe(1);
    await send('reset');
    expect(state().counter).toBe(0);
  });

  it('counts plays per sound and person for statistics', async () => {
    const { send, asAlex, state } = setup();
    await send('play', { soundId: id('rimshot') });
    await send('region', { soundId: id('rimshot'), positionMs: 0, endMs: 500 });
    await asAlex('play', { soundId: id('rimshot') });
    const before = state().statisticsVersion!;
    await send('seek', {
      soundId: id('rimshot'),
      epoch: state().playback.epoch!,
      positionMs: 10,
    });
    expect(state().statisticsVersion).toBe(before); // seeks are not plays
    const one = statisticsSchema.parse(await send('statistics', { soundId: id('rimshot') }));
    expect(one.statistics.total).toBe(3);
    expect(Object.values(one.statistics.participants)).toEqual([
      { name: 'Host', count: 2 },
      { name: 'Alex', count: 1 },
    ]);
    expect(one.recent.map((r) => r.source)).toEqual(['host', 'host', 'guest']);
    const all = (await send('statistics')) as { totals: Record<string, { total: number }> };
    expect(all.totals[id('rimshot')].total).toBe(3);
    expect(all.totals[id('boing')].total).toBe(0);
    await send('clearStatistics');
    const cleared = statisticsSchema.parse(await send('statistics', { soundId: id('rimshot') }));
    expect(cleared.statistics.total).toBe(0);
  });

  it('makes Temp copies with effects, lists them, plays them and removes them', async () => {
    const { send, state, engine } = setup();
    await send('reverse', { soundId: id('dog-bark') });
    const assets = (await send('audioAssets')) as {
      manager: { assets: { id: string; origin: string; normal: { soundId: string } }[] };
    };
    const native = assets.manager.assets[0];
    expect(native.origin).toBe('native');
    expect(native.normal.soundId).toBe(id('dog-bark'));
    const made = (await send('trimAudio', {
      assetId: native.id,
      positionMs: 500,
      endMs: 2500,
      action: 'speed',
      speed: 2,
      keepPitch: true,
    })) as { assetId: string };
    const copy = state().library.sounds.find((s) => s.assetId === made.assetId)!;
    expect(copy).toMatchObject({ name: 'Dog Bark (2x speed, same pitch)', temporary: true });
    expect(copy.durationMs).toBe(1000);
    expect(state().library.categories.at(-1)).toMatchObject({
      name: 'Temp - Soundpad Web [example]',
      soundIds: [copy.id],
    });
    // Save & play: made and played when nothing newer was asked for.
    const played = (await send('trimAudio', {
      assetId: native.id,
      positionMs: 0,
      endMs: 1000,
      action: 'echo',
      autoplay: true,
    })) as { assetId: string; played: boolean };
    expect(played.played).toBe(true);
    expect(engine.now?.clip).toBe(
      state().library.sounds.find((s) => s.assetId === played.assetId)?.id,
    );
    await expect(
      send('trimAudio', {
        assetId: native.id,
        positionMs: 0,
        endMs: 4000,
        action: 'speed',
        speed: 0.05,
      }),
    ).rejects.toThrow(`at most ${MAX_COPY_MS / 1000} s`);
    await expect(send('keepAudio', { assetId: made.assetId, confirmed: true })).rejects.toThrow(
      UNAVAILABLE.keep,
    );
    // Undo of a copy that is playing stops it first; without that, the companion's refusal.
    await expect(send('removeAudio', { assetId: played.assetId })).rejects.toThrow(/Stop playback/);
    await send('removeAudio', { assetId: played.assetId, action: 'stopIfPlaying' });
    expect(engine.now).toBeUndefined();
    expect(state().library.sounds.some((s) => s.assetId === played.assetId)).toBe(false);
    expect(await send('clearTemp')).toEqual({ cleared: 2, pending: 0 });
    expect(state().library.sounds.filter((s) => s.temporary)).toEqual([]);
    expect(state().library.categories).toHaveLength(6);
  });

  it('names a plain trim of speech after its words while transcripts are on', async () => {
    const { send, state } = setup();
    await send('reverse', { soundId: id('apollo11-small-step') });
    const [asset] = ((await send('audioAssets')) as { manager: { assets: { id: string }[] } })
      .manager.assets;
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    const made = (await send('trimAudio', {
      assetId: asset.id,
      positionMs: 3700,
      endMs: 6713,
    })) as {
      assetId: string;
    };
    expect(state().library.sounds.find((s) => s.assetId === made.assetId)?.name).toBe(
      'one giant leap for mankind.',
    );
  });

  it('adds uploads that this browser can read', async () => {
    const { model, state } = setup();
    const result = (await model.upload(new Uint8Array([1, 2, 3]), 'My clip.mp3', HOST)) as {
      assetId: string;
    };
    expect(state().library.sounds.find((s) => s.assetId === result.assetId)).toMatchObject({
      name: 'My clip',
      uploadedBy: 'Host',
    });
    await expect(model.upload(new Uint8Array(), 'broken.wav', HOST)).rejects.toThrow(
      'could not read',
    );
  });

  it('answers waveforms in the companion format', async () => {
    const { send } = setup();
    const data = waveformSchema.parse(await send('waveform', { soundId: id('rimshot') }));
    expect(data.status).toBe('ready');
    expect(data.levels).toHaveLength(3);
  });

  it('shows Spotify, the hotkey and Voice Replay Studio as unavailable', async () => {
    const { send } = setup();
    expect(await send('spotify', { action: 'status' })).toMatchObject({
      available: false,
      error: UNAVAILABLE.spotify,
    });
    await expect(send('spotify', { action: 'enable' })).rejects.toThrow(UNAVAILABLE.spotify);
    await expect(send('mimicHotkey', { action: 'enable' })).rejects.toThrow(UNAVAILABLE.hotkey);
    await expect(send('mimicLibrary')).rejects.toThrow(UNAVAILABLE.library);
  });

  it('feeds Mimic the spoken example: levels, transcript lines and clips into Temp', async () => {
    const { send, state, engine } = setup();
    await expect(send('mimicPeek')).rejects.toThrow('not listening');
    const { sources } = (await send('mimicSources')) as { sources: { id: string }[] };
    await send('mimicStart', { sourceId: sources[0].id });
    expect(state().mimic).toMatchObject({ active: true, transcribing: true });
    vi.advanceTimersByTime(12000);
    const peek = (await send('mimicPeek')) as {
      end: number;
      rate: number;
      windowMs: number;
      levels: number[];
      transcript: { startMs: number; endMs: number; text: string }[];
      ringId: string;
    };
    expect(peek.levels).toHaveLength(300);
    expect(peek.end).toBe(12000 * 48);
    // "One small step" (1.0 to 7.6 s into the loop) has been heard; "The Eagle" (from 11 s) has not.
    expect(peek.transcript.map((l) => l.text)).toEqual([
      "That's one small step for man,",
      'one giant leap for mankind.',
    ]);
    const line = peek.transcript[1];
    const clip = (await send('mimicClip', {
      mimicEnd: peek.end,
      positionMs: line.startMs - 100,
      endMs: line.endMs + 100,
      sourceId: peek.ringId,
      autoplay: true,
    })) as { assetId: string; played: boolean };
    expect(clip.played).toBe(true);
    expect(state().mimic?.recent?.[0]).toMatchObject({
      assetId: clip.assetId,
      name: 'one giant leap for mankind.',
      by: 'Host',
    });
    expect(engine.now?.clip).toBe(
      state().library.sounds.find((s) => s.assetId === clip.assetId)?.id,
    );
    // Before listening started there is nothing to clip.
    await expect(
      send('mimicClip', { mimicEnd: peek.end, positionMs: 0, endMs: 1000, sourceId: peek.ringId }),
    ).rejects.toThrow('had not started');
    await send('mimicTranscribe', { action: 'disable' });
    expect(((await send('mimicPeek')) as { transcript: unknown[] }).transcript).toEqual([]);
    await send('mimicStop');
    expect(state().mimic?.active).toBe(false);
  });
});

describe('example session guests', () => {
  it('Alex joins after a few seconds as a direct guest', () => {
    const { model, session, notices } = setup();
    model.begin();
    vi.advanceTimersByTime(GUEST_JOIN_MS - 1);
    expect(session().guests).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(session().guests).toEqual([ALEX.peer]);
    expect(session().people[ALEX.peer].name).toBe('Alex');
    expect(session().links[ALEX.peer]).toBe('direct');
    expect(notices.at(-1)).toMatch(/Alex joined/);
    model.close();
  });

  it('Alex plays now and then, only while the page is visible and nothing plays', async () => {
    const { model, state, send, setVisible, notices } = setup({ visible: false });
    model.begin();
    // Hidden page: Alex stays quiet however long it is.
    await vi.advanceTimersByTimeAsync(ALEX_GAP_MS * 3);
    expect(state().playback.status).toBe('STOPPED');
    setVisible(true);
    // Something playing: Alex waits.
    await send('region', { soundId: id('applause'), positionMs: 0, endMs: 9000, repeat: true });
    await vi.advanceTimersByTimeAsync(ALEX_GAP_MS);
    expect(state().playback.requester).toBe('Host');
    await send('stop');
    await vi.advanceTimersByTimeAsync(ALEX_GAP_MS);
    expect(state().playback).toMatchObject({ status: 'PLAYING', requester: 'Alex' });
    expect(notices.at(-1)).toMatch(/^Alex played “/);
    const all = (await send('statistics')) as { recent: { name: string }[] };
    expect(all.recent.at(-1)?.name).toBe('Alex');
    model.close();
  });

  it("follows the host's own setting for Alex", async () => {
    const { model, state, send } = setup();
    model.begin();
    vi.advanceTimersByTime(GUEST_JOIN_MS);
    model.setPolicy({
      ...model.getPolicy(),
      overrides: { [ALEX.person.id]: { remotePlayback: false } },
    });
    await vi.advanceTimersByTimeAsync(ALEX_GAP_MS * 3);
    expect(state().playback.status).toBe('STOPPED');
    await expect(
      model.send(command('play', { soundId: id('rimshot') }), ALEX.person, ALEX),
    ).rejects.toThrow('disabled');
    await send('play', { soundId: id('rimshot') });
    model.close();
  });

  it('Sam asks to join once the host chooses Ask me first, and can be let in or turned away', async () => {
    const { model, session } = setup();
    model.begin();
    vi.advanceTimersByTime(GUEST_JOIN_MS);
    model.setPolicy({ ...model.getPolicy(), admission: 'ask' });
    vi.advanceTimersByTime(SAM_ASKS_MS);
    expect(session().requests).toMatchObject([{ id: SAM.person.id, name: 'Sam', kind: 'admit' }]);
    model.turnAway(SAM.person.id);
    expect(session().requests).toEqual([]);
    expect(session().policy.turnedAway).toEqual([SAM.person]);
    // "Allow again" takes Sam off the turned-away list; Sam then has to open the invitation again.
    await model.letIn(SAM.person.id);
    expect(session().policy.turnedAway).toEqual([]);
    // Replace invite: everyone is out, Alex comes back with the new invitation and has to ask.
    model.rotate();
    expect(session().guests).toEqual([]);
    vi.advanceTimersByTime(GUEST_JOIN_MS);
    expect(session().requests.map((r) => r.name)).toEqual(['Alex']);
    await model.letIn(ALEX.person.id);
    expect(session().guests).toEqual([ALEX.peer]);
    model.setPolicy({ ...model.getPolicy(), admission: 'open' });
    model.setPolicy({ ...model.getPolicy(), admission: 'ask' });
    vi.advanceTimersByTime(SAM_ASKS_MS);
    await model.letIn(SAM.person.id);
    expect(session().guests).toEqual([ALEX.peer, SAM.peer]);
    expect(session().links[SAM.peer]).toBe('relay');
    model.disconnect(SAM.peer);
    expect(session().guests).toEqual([ALEX.peer]);
    model.close();
  });

  it('stops every timer when the example is left', async () => {
    const { model, engine, session } = setup();
    model.begin();
    model.close();
    expect(engine.closed).toBe(true);
    vi.advanceTimersByTime(GUEST_JOIN_MS * 10);
    expect(session().guests).toEqual([]);
    await expect(model.send(command('play', { soundId: id('rimshot') }), HOST)).rejects.toThrow(
      'ended',
    );
    expect(session().policy).toEqual(DEFAULT_POLICY);
  });
});

describe('example session audio helpers', () => {
  const pcm = (values: number[], rate = 1000): Pcm => ({
    rate,
    channels: [Float32Array.from(values)],
  });

  it('reverses, changes speed with and without pitch, and adds echo', () => {
    expect(Array.from(reverse(pcm([1, 2, 3])).channels[0])).toEqual([3, 2, 1]);
    const second = pcm(Array.from({ length: 1000 }, (_, i) => Math.sin(i / 5)));
    expect(resample(second, 2).channels[0].length).toBe(500);
    expect(stretch(second, 0.5).channels[0].length).toBe(2000);
    expect(applyEdit(second, { fromMs: 0, toMs: 1000, effect: 'echo' }).channels[0].length).toBe(
      1000 + 4 * 280,
    );
    const half = applyEdit(second, { fromMs: 250, toMs: 750, effect: 'chipmunk' });
    expect(half.channels[0].length).toBe(Math.floor(500 / 1.5));
  });

  it('makes three waveform levels and a WAV', () => {
    const levels = waveformLevels(pcm([0, 0.5, -1, 0.25]));
    expect(levels.map((l) => l.bins)).toEqual([4, 4, 4]);
    expect(levels[0].peaks.slice(4, 6)).toEqual([-32767, -32767]);
    const bytes = wav(pcm([0, 1]));
    expect(String.fromCharCode(...bytes.slice(0, 4))).toBe('RIFF');
    expect(bytes.length).toBe(44 + 4);
  });

  it('reads an endless loop, silent before listening started', () => {
    const loop = pcm([1, 2, 3, 4]);
    expect(Array.from(loopSlice(loop, -2, 6).channels[0])).toEqual([0, 0, 1, 2, 3, 4, 1, 2]);
    const levels = loopLevels({ blockMs: 1, peaks: Float32Array.from([0.1, 0.5]) }, -2, 2, 4);
    expect(levels).toEqual([0, 0, 100, 500]);
  });

  it('groups the example words into transcript lines', () => {
    const step = DEMO_SOUNDS.find((s) => s.key === 'apollo11-small-step')!;
    expect(wordLines(step.words!).map((l) => l.text)).toEqual([
      "That's one small step for man,",
      'one giant leap for mankind.',
    ]);
    expect(mimicLoopLines().map((l) => l.text)).toEqual([
      "That's one small step for man,",
      'one giant leap for mankind.',
      'Houston, Tranquility Base here.',
      'The Eagle has landed.',
    ]);
  });
});
