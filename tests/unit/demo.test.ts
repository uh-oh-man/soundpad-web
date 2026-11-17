import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeEngine, HOST, id, setup } from './demo-engine';
import { DEMO_COMPANION, DemoModel, MAX_COPY_MS, UNAVAILABLE } from '../../web/src/demo/model';
import { DEMO_SOUNDS, mimicLoopLines, wordLines } from '../../web/src/demo/library';
import {
  applyEdit,
  blockPeaks,
  loopLevels,
  loopSlice,
  resample,
  reverse,
  stretch,
  waveformLevels,
  wav,
  type Pcm,
} from '../../web/src/demo/dsp';
import { command, snapshotSchema, statisticsSchema, waveformSchema } from '../../web/src/protocol';
import { DEFAULT_POLICY } from '../../web/src/network/policy';
import { getSchema, searchSchema, statusSchema } from '../../web/src/library/voices';
import { changedSchema, sentenceSchema } from '../../web/src/library/sentences';

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
    expect(s.companionVersion).toBe(DEMO_COMPANION);
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
      { name: 'Alex (simulated)', count: 1 },
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

describe('solo example', () => {
  it('stays silent and never adds guests or requests, even after changing admission or rotating the invite', async () => {
    const { model, state, session, notices } = setup();
    model.begin();
    model.setPolicy({ ...model.getPolicy(), admission: 'ask' });
    await vi.advanceTimersByTimeAsync(90_000);
    model.rotate();
    await vi.advanceTimersByTimeAsync(90_000);
    expect(session().guests).toEqual([]);
    expect(session().requests).toEqual([]);
    expect(state().playback.status).toBe('STOPPED');
    expect(notices).toEqual([]);
    model.close();
  });

  it('stops every timer when the example is left', async () => {
    const { model, engine, session } = setup();
    model.begin();
    model.close();
    expect(engine.closed).toBe(true);
    vi.advanceTimersByTime(40_000);
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
    const levels = loopLevels(blockPeaks(pcm([0.1, 0.5])), -2, 2, 4);
    expect(levels).toEqual([0, 0, 100, 500]);
  });

  it('matches the preview samples at partial peak blocks and the loop seam', () => {
    const loop = pcm([0.8, 0.2, -0.6, 0.3, -1]);
    const bins = loopLevels(blockPeaks(loop, 2), 1, 9, 4);
    const preview = loopSlice(loop, 1, 9).channels[0];
    expect(bins).toEqual([600, 1000, 800, 600]);
    expect(bins).toEqual(
      Array.from({ length: 4 }, (_, i) =>
        Math.round(Math.max(...Array.from(preview.slice(i * 2, i * 2 + 2), Math.abs)) * 1000),
      ),
    );
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

describe('example voice library and sentences', () => {
  it('has three simulated voices from the example clips, with words to search and changes to undo', async () => {
    const { send } = setup();
    const status = statusSchema.parse(await send('libraryStatus'));
    expect(status.voices.map((v) => [v.name, v.count])).toEqual([
      ['Neil Armstrong (simulated)', 1],
      ['Tranquility Base (simulated)', 1],
      ['Studio Audience (simulated)', 1],
    ]);
    expect(status.unreviewed).toBe(1);
    const found = searchSchema.parse(await send('librarySearch', { query: 'eagle has' }));
    expect(found.results).toHaveLength(1);
    expect(found.results[0].context.filter((w) => w.hit).map((w) => w.t)).toEqual(['Eagle', 'has']);
    const id = found.results[0].id;
    const { group } = (await send('libraryEdit', { recordingId: id, addTags: ['moon'] })) as {
      group: number;
    };
    expect(getSchema.parse(await send('libraryGet', { recordingId: id })).recording.tags).toContain(
      'moon',
    );
    await send('libraryUndo', { auditId: group });
    expect(
      getSchema.parse(await send('libraryGet', { recordingId: id })).recording.tags,
    ).not.toContain('moon');
    await expect(send('libraryIndex', { categories: ['Speech'] })).rejects.toThrow(
      'Not available in the example',
    );
  });

  it('builds "hello [laugh]": a missing word that bleeps and the named laugh, played with a sentence id', async () => {
    const { send, state, engine } = setup();
    const sentence = sentenceSchema.parse(
      await send('sentenceCreate', { sentence: { text: 'hello [laugh]' } }),
    );
    expect(sentence.pieces.map((p) => [p.kind, p.text])).toEqual([
      ['missing', 'hello'],
      ['named', '[laugh]'],
    ]);
    expect(sentence.missing).toBe(1);
    // Layout: the laugh follows the missing word's room.
    expect(sentence.pieces[1].offsetMs).toBe(sentence.pieces[0].durationMs);
    const edited = changedSchema.parse(
      await send('sentenceEdit', {
        sentenceId: sentence.id,
        sentence: { do: 'missing', pieces: [sentence.pieces[0].id], mode: 'bleep' },
      }),
    ).sentence;
    expect(edited.pieces[0].missing).toBe('bleep');
    const kept = (await send('sentenceKeep', { sentenceId: sentence.id })) as { version: number };
    expect(kept.version).toBe(1);
    const result = (await send('sentencePlay', {
      sentenceId: sentence.id,
      sentence: { version: 1 },
    })) as { played: boolean };
    expect(result.played).toBe(true);
    expect(state().playback.soundId).toBe(`sentence:${sentence.id}`);
    expect(state().playback.identitySource).toBe('web');
    const parts = engine.mixes.at(-1)!.parts;
    expect(parts[0]).toMatchObject({ tone: 1000, atMs: 0 });
    expect(parts[1]).toMatchObject({ clip: id('audience-laugh') });
    // Reopened later, it is still kept, with its version.
    const again = sentenceSchema.parse(await send('sentenceGet', { sentenceId: sentence.id }));
    expect(again.kept).toBe(true);
    expect(again.versions.map((v) => v.n)).toEqual([1]);
  });

  it('matches the longest recorded phrase, and keeps unchanged words when the text changes', async () => {
    const { send } = setup();
    const s = sentenceSchema.parse(
      await send('sentenceCreate', { sentence: { text: 'one small step zebra' } }),
    );
    expect(s.pieces.map((p) => [p.kind, p.text])).toEqual([
      ['word', 'one small step'],
      ['missing', 'zebra'],
    ]);
    const changed = sentenceSchema.parse(
      await send('sentenceText', {
        sentenceId: s.id,
        sentence: { text: 'one small step the Eagle' },
      }),
    );
    expect(changed.pieces[0].id).toBe(s.pieces[0].id);
    expect(changed.pieces.map((p) => p.text)).toEqual(['one small step', 'the Eagle']);
  });
});
