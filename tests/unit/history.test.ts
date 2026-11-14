import { describe, expect, it } from 'vitest';
import {
  behindLiveMs,
  clampViewEnd,
  formatSpan,
  gapMarks,
  hiddenParts,
  jump,
  selectionFrames,
  selectionMs,
  sentences,
  timelineSchema,
  toPeaks,
  wallOf,
  windowStart,
  wordRange,
  wordSelected,
  wordSelection,
  wordsText,
  visible,
  type Timeline,
  type Word,
} from '../../web/src/mimic/history';
import { commandSchema, isGuestAllowed, parseSnapshot, EMPTY } from '../../web/src/protocol';
import { supports } from '../../web/src/version';

const RATE = 16000;
const s = (seconds: number) => Math.round(seconds * RATE);
const T0 = Date.UTC(2026, 10, 13, 20, 0, 0);

/** 60 s recorded, a 30 s stop after 40 s, 20 s more: frames 0..60 s, wall 0..90 s. */
function timeline(extra: Partial<Timeline> = {}): Timeline {
  return timelineSchema.parse({
    sessionId: 'abc',
    rate: RATE,
    liveFrame: s(60),
    liveWallUtcMs: T0 + 90000,
    nowUtcMs: T0 + 90500,
    recording: true,
    windowSeconds: 600,
    available: [{ startFrame: 0, endFrame: s(60) }],
    spans: [
      { startFrame: 0, endFrame: s(40), wallStartUtcMs: T0 },
      { startFrame: s(40), endFrame: s(60), wallStartUtcMs: T0 + 70000 },
    ],
    gaps: [
      { atFrame: s(40), wallFromUtcMs: T0 + 40000, wallToUtcMs: T0 + 70000, reason: 'stopped' },
    ],
    fromFrame: null,
    shared: [],
    transcribing: true,
    ...extra,
  });
}

let nextId = 1;
const word = (
  text: string,
  from: number,
  to: number,
  phraseId = 1,
  status: Word['status'] = 'done',
): Word => ({
  id: nextId++,
  start: s(from),
  end: s(to),
  text,
  probability: 0.9,
  stretchMs: 0,
  pauseBefore: true,
  pauseAfter: true,
  status,
  phraseId,
  revision: 0,
  corrected: false,
});

describe('recording history timeline', () => {
  it('reads wall time from spans and counts gaps in "behind live"', () => {
    const t = timeline();
    expect(wallOf(t, s(10))).toBe(T0 + 10000);
    expect(wallOf(t, s(50))).toBe(T0 + 80000);
    // 30 s behind on the frame axis, but the 30 s stop in between makes it 60 s of wall time.
    expect(behindLiveMs(t, s(30))).toBe(60000);
    expect(behindLiveMs(t, s(60))).toBe(0);
    expect(formatSpan(60000)).toBe('1 min');
    expect(formatSpan(75000)).toBe('1 min 15 s');
    expect(formatSpan(3720000)).toBe('1 h 2 min');
  });

  it('keeps the view inside the history and follows live at the end', () => {
    const t = timeline({ available: [{ startFrame: s(20), endFrame: s(60) }] });
    expect(clampViewEnd(t, s(100))).toBe(s(60));
    expect(clampViewEnd(t, 0)).toBe(s(21)); // never a window with nothing held in it
    expect(jump(t, s(60), -10)).toEqual({ end: s(50), live: false });
    expect(jump(t, s(55), 10)).toEqual({ end: s(60), live: true });
    expect(jump(t, s(25), -10)).toEqual({ end: s(21), live: false });
  });

  it('marks gaps where they are and hatches what this person cannot see', () => {
    const t = timeline();
    const start = windowStart(s(60), RATE); // the 30 s window ending at live: 30..60 s
    expect(start).toBe(s(30));
    expect(gapMarks(t, start)).toEqual([{ ms: 10000, label: 'Stopped · 30 s missing' }]);
    // A guest who joined at 45 s: the first 15 s of that window are hidden, never drawn as silence.
    const guest = timeline({
      available: [{ startFrame: s(45), endFrame: s(60) }],
      fromFrame: s(45),
    });
    expect(hiddenParts(guest, start)).toEqual([{ from: 0, to: 15000 }]);
    expect(visible(guest, { startFrame: s(44), endFrame: s(46) })).toBe(false);
    expect(visible(guest, { startFrame: s(46), endFrame: s(47) })).toBe(true);
    // At the start of a session the window reaches before frame 0: that part is hidden too.
    expect(
      hiddenParts(
        timeline({ liveFrame: s(10), available: [{ startFrame: 0, endFrame: s(10) }] }),
        windowStart(s(10), RATE),
      ),
    ).toEqual([{ from: 0, to: 20000 }]);
  });

  it('converts selections between the window and history frames', () => {
    const start = s(30);
    const frames = selectionFrames({ from: 26000, to: 29000 }, start, RATE);
    expect(frames).toEqual({ startFrame: s(56), endFrame: s(59) });
    expect(selectionMs(frames, start, RATE)).toEqual({ from: 26000, to: 29000 });
    expect(selectionFrames({ from: 0, to: 500 }, -s(10), RATE).startFrame).toBe(0); // never before frame 0
  });

  it('turns min/max pairs into peaks', () => {
    expect(toPeaks([-32767, 100, -10, 16384, 0, 0])).toEqual([1000, 500, 0]);
  });
});

describe('word selection', () => {
  const words = [
    word('So', 1, 1.2),
    word('my', 1.2, 1.4),
    word('fellow', 1.4, 1.9),
    word('Americans,', 1.9, 2.5),
    word('ask', 3.2, 3.8, 2, 'uncertain'),
  ];

  it('pads a word when it becomes a cut (20 ms before, 10 ms after)', () => {
    expect(wordRange(words[2], RATE)).toEqual({ startFrame: s(1.38), endFrame: s(1.91) });
  });

  it('click selects a word; Shift-click selects from the anchor to the clicked word, either way', () => {
    const one = wordSelection(words, null, words[1].id, false, RATE)!;
    expect(one).toEqual({ startFrame: s(1.18), endFrame: s(1.41) });
    const forward = wordSelection(words, words[1].id, words[3].id, true, RATE)!;
    expect(forward).toEqual({ startFrame: s(1.18), endFrame: s(2.51) });
    expect(wordSelection(words, words[3].id, words[1].id, true, RATE)).toEqual(forward);
    // Without Shift the anchor does not matter.
    expect(wordSelection(words, words[0].id, words[3].id, false, RATE)).toEqual({
      startFrame: s(1.88),
      endFrame: s(2.51),
    });
    expect(words.filter((w) => wordSelected(w, forward)).map((w) => w.text)).toEqual([
      'my',
      'fellow',
      'Americans,',
    ]);
    expect(wordsText(words, forward)).toBe('my fellow Americans,');
    expect(wordSelection(words, null, 999, false, RATE)).toBeNull();
  });

  it('never makes a selection shorter than 150 ms or longer than 30 s', () => {
    const tiny = [word('a', 5, 5.01)];
    const r = wordSelection(tiny, null, tiny[0].id, false, RATE)!;
    expect(r.endFrame - r.startFrame).toBe(s(0.15));
    const long = [word('start', 0, 1), word('end', 40, 41)];
    const both = wordSelection(long, long[0].id, long[1].id, true, RATE)!;
    expect(both.endFrame - both.startFrame).toBe(s(30));
  });

  it('shows sentences: one per phrase, split after . ? !', () => {
    const phrase = [
      word('Hi.', 0, 0.3, 7),
      word('Who', 0.4, 0.6, 7),
      word('is', 0.6, 0.7, 7),
      word('it?', 0.7, 1, 7),
      word('Next', 2, 2.4, 8),
    ];
    expect(sentences([...phrase].reverse()).map((x) => x.map((w) => w.text).join(' '))).toEqual([
      'Hi.',
      'Who is it?',
      'Next',
    ]);
  });
});

describe('history protocol', () => {
  it('accepts the history commands and their fields, and nothing out of range', () => {
    const base = { v: 2, id: crypto.randomUUID(), at: Date.now() };
    expect(
      commandSchema.safeParse({
        ...base,
        op: 'historyLevels',
        startFrame: 0,
        endFrame: 480000,
        bins: 600,
      }).success,
    ).toBe(true);
    expect(
      commandSchema.safeParse({
        ...base,
        op: 'historyLevels',
        startFrame: 0,
        endFrame: 1,
        bins: 5000,
      }).success,
    ).toBe(false);
    expect(commandSchema.safeParse({ ...base, op: 'historySettings', seconds: 30 }).success).toBe(
      false,
    );
    expect(
      commandSchema.safeParse({ ...base, op: 'historySettings', seconds: 600, subtitles: 'gaming' })
        .success,
    ).toBe(true);
    expect(
      commandSchema.safeParse({ ...base, op: 'historySettings', subtitles: 'turbo' }).success,
    ).toBe(false);
    expect(
      commandSchema.safeParse({ ...base, op: 'mimicClip', startFrame: -1, endFrame: 10 }).success,
    ).toBe(false);
  });

  it('guests may only look at the history (with Mimic allowed); sharing and settings are the host’s', () => {
    const allowed = {
      ...EMPTY,
      permissions: { remotePlayback: true, guestCounter: false, guestMimic: true },
    };
    const denied = {
      ...EMPTY,
      permissions: { remotePlayback: true, guestCounter: false, guestMimic: false },
    };
    const c = (op: string) =>
      commandSchema.parse({ v: 2, id: crypto.randomUUID(), at: Date.now(), op });
    for (const op of ['historyTimeline', 'historyLevels', 'historyTranscript']) {
      expect(isGuestAllowed(c(op), allowed)).toBe(true);
      expect(isGuestAllowed(c(op), denied)).toBe(false);
    }
    for (const op of [
      'historyShare',
      'historyUnshare',
      'historySettings',
      'historyDiscard',
      'historyCorrect',
    ])
      expect(isGuestAllowed(c(op), allowed)).toBe(false);
  });

  it('reads the history state from a 2.39 snapshot and works without it', () => {
    const raw = {
      v: 1,
      revision: 1,
      available: true,
      error: null,
      library: { version: 'x', sounds: [], categories: [] },
      playback: { status: 'STOPPED', positionMs: null, durationMs: null },
      counter: 0,
      permissions: { remotePlayback: true, guestCounter: false },
      mimic: {
        active: true,
        source: 'Output: Speakers',
        status: 'Listening',
        windowMs: 30000,
        history: {
          sessionId: 'abc',
          seconds: 600,
          subtitles: 'gaming',
          sourceLost: false,
          recoveredMs: 0,
          problem: null,
        },
      },
    };
    expect(parseSnapshot(raw).mimic?.history?.seconds).toBe(600);
    const { history, ...older } = raw.mimic;
    expect(history.sessionId).toBe('abc');
    expect(parseSnapshot({ ...raw, mimic: older }).mimic?.history).toBeUndefined();
    expect(supports('2.38.0', 'history')).toBe(false);
    expect(supports('2.39.0', 'history')).toBe(true);
  });
});
