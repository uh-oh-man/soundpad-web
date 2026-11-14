import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DemoHistory,
  HEARD_MS,
  HISTORY_RATE,
  LOOP_FRAMES,
  LOOP_WORDS,
  loopMs,
  msToFrames,
} from '../../web/src/demo/history';
import { ALEX_BAND_MS } from '../../web/src/demo/drafts';
import { ALEX, GUEST_JOIN_MS, UNAVAILABLE } from '../../web/src/demo/model';
import { MIMIC_LOOP_MS } from '../../web/src/demo/library';
import { blockPeaks, loopLevels, type Pcm } from '../../web/src/demo/dsp';
import { levelsSchema, timelineSchema, transcriptSchema } from '../../web/src/mimic/history';
import { draftListSchema, draftSchema } from '../../web/src/mimic/drafts';
import { historySearchSchema } from '../../web/src/library/voices';
import { setup } from './demo-engine';

const T0 = Date.UTC(2026, 10, 14, 12, 0, 0);
const s = (seconds: number) => seconds * 1000;

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(T0);
});
afterEach(() => {
  vi.useRealTimers();
});

/** A 20 s loop at 8 kHz, loud only between 1 and 2 seconds. */
function loop(): Pcm {
  const rate = 8000;
  const channel = new Float32Array((MIMIC_LOOP_MS * rate) / 1000);
  for (let i = rate; i < 2 * rate; i++) channel[i] = 0.5;
  return { rate, channels: [channel] };
}

describe('example recording history', () => {
  it('runs on across loop repeats, with a gap where listening stopped', () => {
    const h = new DemoHistory();
    h.start(T0);
    // 45 s: two whole loops and a bit, all of it one continuous run.
    let t = timelineSchema.parse(h.timeline(T0 + s(45), true));
    expect(t.liveFrame).toBe(45 * HISTORY_RATE);
    expect(t.liveFrame).toBeGreaterThan(2 * LOOP_FRAMES);
    expect(t.available).toEqual([{ startFrame: 0, endFrame: 45 * HISTORY_RATE }]);
    expect(t.spans).toEqual([{ startFrame: 0, endFrame: 45 * HISTORY_RATE, wallStartUtcMs: T0 }]);
    expect(t.gaps).toEqual([]);
    // Stopped for 10 s: the frames stop too; a new run follows a "Stopped" gap on the same history.
    h.stop(T0 + s(45));
    const session = h.sessionId;
    h.start(T0 + s(55));
    t = timelineSchema.parse(h.timeline(T0 + s(60), true));
    expect(h.sessionId).toBe(session);
    expect(t.liveFrame).toBe(50 * HISTORY_RATE);
    expect(t.gaps).toEqual([
      {
        atFrame: 45 * HISTORY_RATE,
        wallFromUtcMs: T0 + s(45),
        wallToUtcMs: T0 + s(55),
        reason: 'stopped',
      },
    ]);
    expect(t.spans.map((x) => x.wallStartUtcMs)).toEqual([T0, T0 + s(55)]);
    // Only the window can be seen: 10 minutes by default, rolling on.
    t = timelineSchema.parse(h.timeline(T0 + s(55 + 700), true));
    expect(t.available).toEqual([
      { startFrame: t.liveFrame - 600 * HISTORY_RATE, endFrame: t.liveFrame },
    ]);
    expect(t.gaps).toEqual([]);
    expect(t.spans).toHaveLength(1);
    expect(t.spans[0].wallStartUtcMs).toBe(
      T0 + s(55) + loopMs(t.available[0].startFrame - s(45) * 48),
    );
  });

  it('draws levels that wrap around the loop', () => {
    const h = new DemoHistory();
    h.start(T0);
    const peaks = blockPeaks(loop());
    const read = (from: number, to: number, n: number) => loopLevels(peaks, from, to, n);
    const now = T0 + s(70);
    // The same second of the loop, one and two repeats later, looks the same; outside it is silent.
    const at = (rep: number) =>
      levelsSchema.parse(
        h.levels(
          { startFrame: rep * LOOP_FRAMES, endFrame: rep * LOOP_FRAMES + 3 * HISTORY_RATE },
          30,
          now,
          read,
        ),
      ).levels;
    expect(at(1)).toEqual(at(0));
    expect(at(2)).toEqual(at(0));
    const first = at(0);
    expect(first).toHaveLength(60);
    expect(Math.max(...first.slice(0, 20))).toBe(0); // 0 to 1 s
    expect(Math.max(...first.slice(22, 38))).toBeGreaterThan(16000); // 1 to 2 s
    // A range across the loop's end reads the end of one repeat and the start of the next.
    const across = h.levels(
      { startFrame: LOOP_FRAMES - HISTORY_RATE, endFrame: LOOP_FRAMES + 2 * HISTORY_RATE },
      30,
      now,
      read,
    ).levels;
    expect(across.slice(0, 20).every((v) => v === 0)).toBe(true);
    expect(Math.max(...across.slice(42, 58))).toBeGreaterThan(16000);
    // What has not been recorded yet is silent.
    const future = h.levels(
      { startFrame: 69 * HISTORY_RATE, endFrame: 72 * HISTORY_RATE },
      30,
      now,
      read,
    ).levels;
    expect(future.slice(22).every((v) => v === 0)).toBe(true);
  });

  it('shows each word only once it has been heard, and finds words in what can be seen', () => {
    const h = new DemoHistory();
    h.start(T0);
    const first = LOOP_WORDS[0];
    const range = { startFrame: 0, endFrame: msToFrames(MIMIC_LOOP_MS) };
    const words = (ms: number) =>
      transcriptSchema.parse(h.transcript(range, T0 + ms, true)).words.map((w) => w.text);
    expect(words(first.endMs + HEARD_MS - 1)).toEqual([]);
    expect(words(first.endMs + HEARD_MS)).toEqual([first.text]);
    // Ids are stable across polls, one series per repeat of the loop.
    const once = h.transcript(range, T0 + s(30), true).words;
    const again = h.transcript(range, T0 + s(31), true).words;
    expect(again.map((w) => w.id)).toEqual(once.map((w) => w.id));
    expect(
      h.transcript({ startFrame: LOOP_FRAMES, endFrame: 2 * LOOP_FRAMES }, T0 + s(30), true)
        .words[0].id,
    ).toBe(1001);
    // With transcripts off there are no words; corrections change only the text.
    expect(h.transcript(range, T0 + s(30), false).words).toEqual([]);
    h.correct(once[0].id, 'Thats', T0 + s(30));
    expect(h.transcript(range, T0 + s(30), true).words[0]).toMatchObject({
      text: 'Thats',
      corrected: true,
    });
    const found = historySearchSchema.parse(h.search('eagle has', T0 + s(30)));
    expect(found.matches.map((m) => m.text)).toEqual(['Eagle has']);
    expect(found.matches[0].context).toBe('The Eagle has landed.');
  });

  it('discards nothing of the current run', () => {
    const h = new DemoHistory();
    h.start(T0);
    const before = h.timeline(T0 + s(20), true);
    expect(h.discard()).toEqual({ discarded: 0 });
    expect(h.timeline(T0 + s(20), true)).toEqual(before);
  });
});

describe('example drafts', () => {
  it('makes a draft of the latest phrase, which stays playable after it has rolled out of view', async () => {
    const { send, state, engine } = setup();
    await send('mimicSources');
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    expect(state().mimic?.history).toMatchObject({
      seconds: 600,
      subtitles: 'gaming',
      recoveredMs: 0,
    });
    await send('historySettings', { seconds: 60 });
    vi.setSystemTime(T0 + s(16));
    const draft = draftSchema.parse(await send('draftCreate', { action: 'phrase' }));
    expect(draft.name).toBe('The Eagle has landed.');
    // Two minutes later its part of the history (one minute) is gone from view; the draft is not.
    vi.setSystemTime(T0 + s(140));
    const list = draftListSchema.parse(await send('draftList'));
    expect(list.drafts.map((d) => d.id)).toEqual([draft.id]);
    const timeline = timelineSchema.parse(await send('historyTimeline'));
    expect(timeline.available[0].startFrame).toBeGreaterThan(draft.endFrame);
    const played = (await send('draftPlay', { draftId: draft.id, autoplay: true })) as {
      played: boolean;
    };
    expect(played.played).toBe(true);
    expect(engine.cuts.at(-1)).toEqual({
      from: loopMs(draft.startFrame),
      to: loopMs(draft.endFrame),
    });
    // Its edges, though, can only move within what can still be seen.
    await expect(
      send('draftUpdate', { draftId: draft.id, startFrame: draft.startFrame - 100 }),
    ).rejects.toThrow('no longer in the recording history');
  });

  it('plays a draft into the game with a new epoch; its private preview leaves playback alone', async () => {
    const { send, state, engine } = setup();
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    // "mankind." ends 7.55 s into the loop: the phrase is finished once it was heard, 1.5 s later.
    vi.setSystemTime(T0 + s(9));
    expect(draftSchema.parse(await send('draftCreate', { action: 'phrase' })).name).toBe(
      "That's one small step for man,",
    );
    vi.setSystemTime(T0 + s(9.1));
    const draft = draftSchema.parse(await send('draftCreate', { action: 'phrase' }));
    expect(draft.name).toBe('one giant leap for mankind.');
    const before = state().playback;
    await send('mimicPreview', { draftId: draft.id, sourceId: draft.sessionId });
    expect(engine.previews).toHaveLength(1);
    expect(state().playback).toEqual(before);
    expect(engine.now).toBeUndefined();
    const first = (await send('draftPlay', { draftId: draft.id, autoplay: true })) as {
      epoch: string;
    };
    expect(state().playback).toMatchObject({
      status: 'PLAYING',
      epoch: first.epoch,
      identitySource: 'web',
      requester: 'Host',
    });
    const sound = state().library.sounds.find((x) => x.id === state().playback.soundId);
    expect(sound).toMatchObject({ name: 'one giant leap for mankind.', internalVariant: true });
    const second = (await send('draftPlay', { draftId: draft.id, autoplay: true })) as {
      epoch: string;
    };
    expect(second.epoch).not.toBe(first.epoch);
    expect(state().playback.epoch).toBe(second.epoch);
  });

  it('keeps a draft in Temp, refuses the permanent keep, and shows the simulated guest’s selection', async () => {
    const { model, send, state, engine } = setup();
    model.begin();
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    vi.advanceTimersByTime(GUEST_JOIN_MS);
    vi.setSystemTime(T0 + s(16));
    const draft = draftSchema.parse(await send('draftCreate', { action: 'recent' }));
    expect((draft.endFrame - draft.startFrame) / HISTORY_RATE).toBe(10);
    const revision = state().drafts ?? 0;
    await vi.advanceTimersByTimeAsync(ALEX_BAND_MS);
    expect(state().drafts).toBeGreaterThan(revision);
    const list = draftListSchema.parse(await send('draftList'));
    expect(list.others).toMatchObject([{ ownerName: 'Alex (simulated)', tag: 'alex' }]);
    expect(
      list.others[0].endFrame <= draft.startFrame || list.others[0].startFrame >= draft.endFrame,
    ).toBe(true);
    const copy = draftSchema.parse(await send('draftCopy', { draftId: list.others[0].id }));
    expect(copy.startFrame).toBe(list.others[0].startFrame);
    await expect(send('draftKeep', { draftId: draft.id, action: 'library' })).rejects.toThrow(
      UNAVAILABLE.keep,
    );
    const kept = (await send('draftKeep', { draftId: draft.id, action: 'close' })) as {
      assetId: string;
      closed: boolean;
    };
    expect(kept.closed).toBe(true);
    expect(state().mimic?.recent?.[0].assetId).toBe(kept.assetId);
    expect(state().library.sounds.find((x) => x.assetId === kept.assetId)?.temporary).toBe(true);
    expect(engine.cuts).toHaveLength(1);
    expect(draftListSchema.parse(await send('draftList')).drafts.map((d) => d.id)).toEqual([
      copy.id,
    ]);
    await send('draftDiscard', { draftId: list.others[0].id });
    expect(draftListSchema.parse(await send('draftList')).others).toEqual([]);
    model.close();
  });

  it('gives the simulated guest no selection when they are not in the session', async () => {
    const { model, send } = setup();
    model.begin();
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    vi.setSystemTime(T0 + s(3));
    await send('draftCreate', { action: 'recent' });
    await vi.advanceTimersByTimeAsync(ALEX_BAND_MS - GUEST_JOIN_MS);
    // Alex joined meanwhile; the selection appears only if they were there when it was due.
    model.disconnect(ALEX.peer);
    await vi.advanceTimersByTimeAsync(GUEST_JOIN_MS);
    expect(draftListSchema.parse(await send('draftList')).others).toEqual([]);
    model.close();
  });

  it('cuts Mimic clips and previews from frames of the history', async () => {
    const { send, engine, state } = setup();
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    vi.setSystemTime(T0 + s(30));
    const timeline = timelineSchema.parse(await send('historyTimeline'));
    const range = {
      startFrame: LOOP_FRAMES + msToFrames(1000),
      endFrame: LOOP_FRAMES + msToFrames(3300),
      sourceId: timeline.sessionId!,
    };
    const clip = (await send('mimicClip', range)) as { assetId: string };
    expect(engine.cuts.at(-1)).toEqual({ from: s(21), to: s(23.3) });
    expect(state().mimic?.recent?.[0]).toMatchObject({
      assetId: clip.assetId,
      name: "That's one small step for man,",
    });
    await send('mimicPreview', range);
    expect(engine.previews.at(-1)).toEqual({ from: s(21), to: s(23.3) });
    await expect(send('mimicClip', { ...range, sourceId: 'f'.repeat(32) })).rejects.toThrow(
      'another recording',
    );
    await expect(
      send('mimicClip', { ...range, endFrame: range.startFrame + msToFrames(31000) }),
    ).rejects.toThrow('at most 30 seconds');
  });
});
