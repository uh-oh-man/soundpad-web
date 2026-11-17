import { describe, expect, it } from 'vitest';
import {
  CLICK_PX,
  clipKey,
  effectiveMode,
  END_SLACK_MS,
  gameBlocked,
  gamePlayEnded,
  isClick,
  PLAY_FROM_MAX_MS,
  PLAY_MODE_KEY,
  playFrom,
  playheadFrame,
  playingWordIds,
  readPlayMode,
  reusableClip,
  writePlayMode,
} from '../../web/src/components/mimic/playMode';

const RATE = 48000;
const s = (seconds: number) => Math.round(seconds * RATE);

/** A store as localStorage is; `broken` throws on every access, as a blocked one does. */
function store(initial: Record<string, string> = {}, broken = false) {
  const data = new Map(Object.entries(initial));
  return {
    data,
    getItem(key: string) {
      if (broken) throw new Error('blocked');
      return data.get(key) ?? null;
    },
    setItem(key: string, value: string) {
      if (broken) throw new Error('blocked');
      data.set(key, value);
    },
  };
}

describe('Mimic play mode: remembered per person, Preview by default', () => {
  it('starts in Preview, and remembers Play into game', () => {
    const mine = store();
    expect(readPlayMode(mine)).toBe('preview');
    writePlayMode('game', mine);
    expect(mine.data.get(PLAY_MODE_KEY)).toBe('game');
    expect(readPlayMode(mine)).toBe('game');
    writePlayMode('preview', mine);
    expect(readPlayMode(mine)).toBe('preview');
  });
  it('reads anything unknown as Preview', () => {
    expect(readPlayMode(store({ [PLAY_MODE_KEY]: 'loud' }))).toBe('preview');
  });
  it('works without storage: blocked or missing', () => {
    const blocked = store({}, true);
    expect(readPlayMode(blocked)).toBe('preview');
    expect(() => writePlayMode('game', blocked)).not.toThrow();
    expect(readPlayMode(null)).toBe('preview');
    expect(() => writePlayMode('game', null)).not.toThrow();
  });
});

describe('Mimic play mode: Play into game needs playback, and for a guest guest Mimic', () => {
  const ok = { host: false, canPlay: true, guestMimic: true, held: false };
  it('is allowed for a host who can play, and for a guest with both permissions', () => {
    expect(gameBlocked({ ...ok, host: true, guestMimic: false })).toBeNull();
    expect(gameBlocked(ok)).toBeNull();
  });
  it('is refused without remote playback, without guest Mimic, and while Panic holds guests', () => {
    expect(gameBlocked({ ...ok, canPlay: false })).toMatch(/not allowed guests to play/);
    expect(gameBlocked({ ...ok, host: true, canPlay: false })).toMatch(/not available/);
    expect(gameBlocked({ ...ok, guestMimic: false })).toMatch(/guest Mimic/);
    expect(gameBlocked({ ...ok, held: true })).toMatch(/paused guest playback/);
  });
  it('makes a refused Play into game act as Preview, without changing what is remembered', () => {
    expect(effectiveMode('game', null)).toBe('game');
    expect(effectiveMode('game', 'The host has not allowed guests to play')).toBe('preview');
    expect(effectiveMode('preview', null)).toBe('preview');
  });
});

describe('Mimic waveform click: plays from that point', () => {
  const timeline = {
    available: [{ startFrame: s(10), endFrame: s(60) }],
    gaps: [{ atFrame: s(40), wallFromUtcMs: 0, wallToUtcMs: 1000, reason: 'stopped' }],
    rate: RATE,
    liveFrame: s(60),
    fromFrame: null,
  };
  it('plays to the end of the continuous audio there, at most 10 s', () => {
    expect(playFrom(s(12), timeline)).toEqual({
      range: { startFrame: s(12), endFrame: s(12) + s(PLAY_FROM_MAX_MS / 1000) },
    });
    // Never across a gap: 2 s before it, the play stops there.
    expect(playFrom(s(38), timeline)).toEqual({ range: { startFrame: s(38), endFrame: s(40) } });
    // Up to live.
    expect(playFrom(s(55), timeline)).toEqual({ range: { startFrame: s(55), endFrame: s(60) } });
  });
  it('refuses a point with too little audio after it', () => {
    expect(playFrom(s(39.95), timeline)).toEqual({ reason: expect.stringMatching(/Too little/) });
  });
  it('refuses a point a guest may not hear: before they joined, not recorded yet, or expired', () => {
    const guest = {
      ...timeline,
      available: [{ startFrame: s(30), endFrame: s(60) }],
      fromFrame: s(30),
    };
    expect(playFrom(s(20), guest)).toEqual({ reason: expect.stringMatching(/before you joined/) });
    expect(playFrom(s(31), guest)).toEqual({ range: { startFrame: s(31), endFrame: s(40) } });
    expect(playFrom(s(61), timeline)).toEqual({
      reason: expect.stringMatching(/Nothing has been/),
    });
    expect(playFrom(s(5), timeline)).toEqual({
      reason: expect.stringMatching(/no recording there/),
    });
  });
  it('tells a click from a drag: a press that moved, or took an edge handle, never plays', () => {
    expect(isClick(0, 0, false)).toBe(true);
    expect(isClick(CLICK_PX - 1, 0, false)).toBe(true);
    expect(isClick(CLICK_PX, 0, false)).toBe(false);
    expect(isClick(3, 3, false)).toBe(false);
    expect(isClick(0, 0, true)).toBe(false);
  });
});

describe('Mimic playing words', () => {
  const words = [
    { id: 1, start: s(1), end: s(1.4) },
    { id: 2, start: s(1.5), end: s(2) },
    { id: 3, start: s(2.6), end: s(3) },
    { id: 4, start: s(5), end: s(5.5) },
  ];
  const range = { startFrame: s(0.98), endFrame: s(3.01) };
  it('lights nothing until the audio has started', () => {
    expect([...playingWordIds(words, range, null, false)]).toEqual([]);
    expect([...playingWordIds(words, null, s(1), false)]).toEqual([]);
  });
  it('lights the word being said, the first through its padding and the last one said through a pause', () => {
    const at = (seconds: number) => [...playingWordIds(words, range, s(seconds), false)];
    expect(at(0.99)).toEqual([1]);
    expect(at(1.2)).toEqual([1]);
    expect(at(1.6)).toEqual([2]);
    expect(at(2.3)).toEqual([2]); // the pause after word 2
    expect(at(2.8)).toEqual([3]);
    // Word 4 is outside the range: never lit.
    expect(at(5.2)).toEqual([3]);
  });
  it('with reduced motion lights every word of the play at once, wherever the audio is', () => {
    expect([...playingWordIds(words, range, s(1), true)]).toEqual([1, 2, 3]);
    expect([...playingWordIds(words, range, s(2.9), true)]).toEqual([1, 2, 3]);
  });
  it('a single word stays lit for the whole of its play', () => {
    const one = { startFrame: s(1.48), endFrame: s(2.01) };
    for (const t of [1.48, 1.7, 2.0])
      expect([...playingWordIds(words, one, s(t), false)]).toEqual([2]);
  });
  it('the playhead runs from the start of the range and stops at its end', () => {
    expect(playheadFrame(range, 0, RATE)).toBe(range.startFrame);
    expect(playheadFrame(range, 500, RATE)).toBe(range.startFrame + s(0.5));
    expect(playheadFrame(range, 60000, RATE)).toBe(range.endFrame);
    expect(playheadFrame(range, -20, RATE)).toBe(range.startFrame);
  });
});

describe('Mimic play into the game: when its highlight ends', () => {
  const play = { epoch: 'mine', startedAt: 1000, lengthMs: 500 };
  it('keeps it while the report has not caught up yet, then while it plays', () => {
    expect(gamePlayEnded(play, { epoch: 'older', status: 'PLAYING' }, false, false, 1100)).toEqual({
      ended: false,
      seen: false,
    });
    expect(gamePlayEnded(play, { epoch: 'mine', status: 'PLAYING' }, false, false, 1200)).toEqual({
      ended: false,
      seen: true,
    });
  });
  it('ends on Stop, when something else plays, on Panic, and at the end', () => {
    expect(
      gamePlayEnded(play, { epoch: 'mine', status: 'STOPPED' }, false, false, 1200).ended,
    ).toBe(true);
    expect(
      gamePlayEnded(play, { epoch: 'newer', status: 'PLAYING' }, false, true, 1200).ended,
    ).toBe(true);
    expect(gamePlayEnded(play, { epoch: 'mine', status: 'PLAYING' }, true, true, 1200).ended).toBe(
      true,
    );
    const end = play.startedAt + play.lengthMs + END_SLACK_MS;
    expect(gamePlayEnded(play, { epoch: 'mine', status: 'PLAYING' }, false, true, end).ended).toBe(
      false,
    );
    expect(
      gamePlayEnded(play, { epoch: 'older', status: 'PLAYING' }, false, false, end + 1).ended,
    ).toBe(true);
  });
});

describe('Mimic play into the game: the same range replays its clip', () => {
  it('keys a clip by recording and frames', () => {
    expect(clipKey('rec-1', { startFrame: 10.4, endFrame: 20 })).toBe('rec-1:10-20');
    expect(clipKey('rec-2', { startFrame: 10, endFrame: 20 })).not.toBe(
      clipKey('rec-1', { startFrame: 10, endFrame: 20 }),
    );
  });
  it('reuses a clip only while it is in Temp and ready', () => {
    const assets = [
      { id: 'a', hasTemporary: true, normal: { ready: true } },
      { id: 'b', hasTemporary: false, normal: { ready: true } },
      { id: 'c', hasTemporary: true, normal: { ready: false } },
      { id: 'd', hasTemporary: true, normal: null },
    ];
    expect(reusableClip(assets, 'a')).toBe(true);
    for (const id of ['b', 'c', 'd', 'gone']) expect(reusableClip(assets, id)).toBe(false);
    // 2.47 review: never one this page is deleting or took out of Temp.
    expect(reusableClip(assets, 'a', (id) => id === 'a')).toBe(false);
    expect(reusableClip(undefined, 'a')).toBe(false);
    expect(reusableClip(assets, undefined)).toBe(false);
  });
});
