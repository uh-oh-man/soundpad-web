import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  NUDGE_LARGE_MS,
  NUDGE_MS,
  SNAP_MS,
  changedSchema,
  fillTemplate,
  itemRef,
  listSchema,
  missingMarker,
  overlapping,
  pieceLabel,
  placeAt,
  prefersReducedMotion,
  prune,
  sameRef,
  select,
  sentenceRights,
  sentenceSchema,
  sourceProblem,
  templateSlots,
  timelineKey,
  wordMarks,
  type Piece,
  type Sentence,
} from '../../web/src/library/sentences';
import { commandSchema, EMPTY, isGuestAllowed, parseSnapshot } from '../../web/src/protocol';
import { missingFeatures, siteRelaysSentences, supports } from '../../web/src/version';
import { controller } from '../../web/src/controller';
import { combineResults, type ResultType } from '../../web/src/library/voices';
import {
  refreshSentences,
  resetSentenceStore,
  sentenceStoreSnapshot,
} from '../../web/src/library/sentence-store';

const hex = (n: number, length = 32) => n.toString(16).padStart(length, '0');
const piece = (n: number, extra: Partial<Piece> = {}): Piece => ({
  id: hex(n, 12),
  kind: 'word',
  tokenStart: n,
  tokenEnd: n + 1,
  text: `w${n}`,
  offsetMs: n * 400,
  durationMs: 330,
  manual: false,
  chosen: false,
  fadeInMs: 5,
  fadeOutMs: 8,
  gain: null,
  missing: null,
  substitute: null,
  namedId: null,
  effect: null,
  voiceId: hex(7),
  source: {
    recordingId: hex(1),
    startMs: 0,
    endMs: 330,
    voiceId: hex(7),
    label: null,
    recordingMs: 2000,
    state: 'ok',
  },
  parts: null,
  ...extra,
});
/** A sentence as the companion sends it (with fields the page does not use, which are ignored). */
const reply = (pieces: Piece[], extra: Partial<Sentence> = {}) => ({
  id: hex(9),
  title: '',
  text: 'w0 w1 zebra',
  ownerName: 'Host',
  mine: true,
  origin: 'typed',
  revision: 3,
  created: 1,
  updated: 2,
  kept: false,
  pinned: false,
  canEdit: true,
  settings: {
    chaos: 45,
    timing: 'fluent',
    balance: true,
    cleanup: false,
    altered: false,
    voiceIds: [],
    effect: null,
  },
  tokens: ['w0', 'w1', 'zebra'],
  pieces,
  overlaps: [],
  durationMs: 1200,
  missing: 1,
  suggestContrast: false,
  versions: [],
  ...extra,
});

describe('sentence builder state (2.42)', () => {
  const zebra = piece(2, {
    kind: 'missing',
    text: 'zebra',
    missing: 'gap',
    source: null,
    voiceId: null,
    durationMs: 470,
  });
  const sentence = sentenceSchema.parse(reply([piece(0), piece(1), zebra]));

  it('reads the companion’s sentence and change replies', () => {
    expect(changedSchema.parse(reply([piece(0)])).sentence.pieces).toHaveLength(1);
    const withResult = changedSchema.parse({ sentence: reply([piece(0)]), result: { found: 1 } });
    expect(withResult.result).toEqual({ found: 1 });
    expect(() => sentenceSchema.parse(reply([piece(0, { kind: 'speech' as 'word' })]))).toThrow();
    expect(
      listSchema.parse({ revision: 1, canBuild: true, canKeep: false, sentences: [], named: [] })
        .canKeep,
    ).toBe(false);
  });

  it('selects pieces, adds with Ctrl/Cmd, and drops pieces that left the sentence', () => {
    let s = select([], hex(0, 12), false);
    expect(s).toEqual([hex(0, 12)]);
    s = select(s, hex(1, 12), true);
    expect(s).toEqual([hex(0, 12), hex(1, 12)]);
    s = select(s, hex(0, 12), true);
    expect(s).toEqual([hex(1, 12)]);
    expect(select(s, hex(2, 12), false)).toEqual([hex(2, 12)]);
    const kept = [hex(1, 12)];
    expect(prune(kept, sentence.pieces)).toBe(kept); // unchanged: the same array
    expect(prune([hex(1, 12), hex(5, 12)], sentence.pieces)).toEqual([hex(1, 12)]);
  });

  it('marks each word with the piece that plays it, and missing words as missing', () => {
    const marks = wordMarks(sentence);
    expect(marks.map((m) => m.piece?.kind)).toEqual(['word', 'word', 'missing']);
    expect(pieceLabel(zebra)).toBe('Missing “zebra” (gap)');
    expect(pieceLabel(piece(0, { kind: 'pause', durationMs: 250 }))).toBe('Pause, 250 ms');
    expect(pieceLabel(piece(0, { substitute: 'cats', text: 'cat' }))).toBe('“cats” for “cat”');
    expect(sourceProblem(piece(0))).toBeNull();
    expect(sourceProblem(piece(0, { source: { ...piece(0).source!, state: 'changed' } }))).toBe(
      'changed',
    );
    expect(overlapping({ overlaps: [{ first: hex(0, 12), second: hex(1, 12), ms: 20 }] })).toEqual(
      new Set([hex(0, 12), hex(1, 12)]),
    );
  });

  it('turns palette items into references (no audio) and compares them', () => {
    const word = itemRef({
      kind: 'word',
      text: 'tone',
      recordingId: hex(1),
      namedId: null,
      startMs: 380.4,
      endMs: 710,
      voiceId: null,
      durationMs: 330,
      favorite: false,
    });
    expect(word).toEqual({ recordingId: hex(1), startMs: 380, endMs: 710, text: 'tone' });
    const laugh = itemRef({
      kind: 'named',
      text: '[laugh]',
      namedId: hex(4),
      recordingId: hex(1),
      startMs: 0,
      endMs: 300,
      voiceId: null,
      durationMs: 300,
      favorite: true,
    });
    expect(laugh).toEqual({ namedId: hex(4), startMs: 0, endMs: 0, text: '[laugh]' });
    expect(sameRef(word, { ...word, text: 'other' })).toBe(true);
    expect(sameRef(word, laugh)).toBe(false);
    expect(templateSlots('Hello {name}, {name} is {place}')).toEqual(['name', 'place']);
    expect(fillTemplate('Hello {name}, {place}', { name: 'Sam' })).toBe('Hello Sam, {place}');
  });
});

describe('timeline keyboard and moves (2.42)', () => {
  it('maps keys to the same operations as the pointer', () => {
    expect(timelineKey({ key: 'ArrowRight' })).toEqual({ kind: 'focus', step: 1 });
    expect(timelineKey({ key: 'ArrowLeft' })).toEqual({ kind: 'focus', step: -1 });
    expect(timelineKey({ key: 'ArrowRight', altKey: true })).toEqual({
      kind: 'move',
      deltaMs: NUDGE_MS,
    });
    expect(timelineKey({ key: 'ArrowLeft', altKey: true, shiftKey: true })).toEqual({
      kind: 'move',
      deltaMs: -NUDGE_LARGE_MS,
    });
    expect(timelineKey({ key: 'Delete' })).toEqual({ kind: 'delete' });
    expect(timelineKey({ key: 'Backspace' })).toEqual({ kind: 'delete' });
    expect(timelineKey({ key: 'Enter' })).toEqual({ kind: 'preview' });
    // Space selects like a click, so a piece with no neighbour can be selected too (2.42 review).
    expect(timelineKey({ key: ' ' })).toEqual({ kind: 'select', add: false });
    expect(timelineKey({ key: ' ', shiftKey: true })).toEqual({ kind: 'select', add: true });
    expect(timelineKey({ key: 'Escape' })).toEqual({ kind: 'clear' });
    expect(timelineKey({ key: 'a' })).toBeNull();
    expect(timelineKey({ key: 'Delete', ctrlKey: true })).toBeNull(); // browser shortcuts stay theirs
  });

  it('moves freely by default and snaps only with the modifier', () => {
    const others = [{ start: 1000, end: 1330 }];
    expect(placeAt(1012.6, false, others)).toBe(1013);
    expect(placeAt(-40, false, others)).toBe(0);
    // Snap: to a neighbour's edge within 60 ms, otherwise to the grid.
    expect(placeAt(1300, true, others)).toBe(1330);
    expect(placeAt(1040, true, others)).toBe(1000);
    expect(placeAt(2024, true, others)).toBe(2000);
    expect(2000 % SNAP_MS).toBe(0);
  });
});

describe('missing-word markers and reduced motion (2.42)', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('pulses normally, uses a static marker under reduced motion, and always says it in words', () => {
    const p = { text: 'zebra', missing: 'bleep' as const };
    expect(missingMarker(p, false).className).toBe('missing-pulse');
    expect(missingMarker(p, true).className).toBe('missing-static');
    expect(missingMarker(p, true).label).toBe(
      'Missing word “zebra”: not in the voice library, plays as a bleep',
    );
    expect(missingMarker({ text: 'x', missing: 'gap' }, false).label).toContain('plays as a gap');
  });

  it('follows the browser’s reduced-motion setting, and copes without one', () => {
    vi.stubGlobal('window', { matchMedia: (q: string) => ({ matches: q.includes('reduce') }) });
    expect(prefersReducedMotion()).toBe(true);
    vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) });
    expect(prefersReducedMotion()).toBe(false);
    vi.stubGlobal('window', {});
    expect(prefersReducedMotion()).toBe(false);
  });
});

describe('sentence permissions, snapshot and gates (2.42)', () => {
  const base = {
    ...EMPTY,
    permissions: { remotePlayback: true, guestCounter: false, guestMimic: true, guestKeep: true },
  };
  const c = (op: string, extra: object = {}) =>
    commandSchema.parse({ v: 2, id: crypto.randomUUID(), at: 1, op, ...extra });
  const item = { recordingId: hex(1), startMs: 0, endMs: 300, text: 'tone' };

  it('checks sentence commands strictly', () => {
    expect(() => c('sentenceEdit', { sentenceId: hex(1), sentence: { do: 'speak' } })).toThrow();
    expect(() =>
      c('sentenceEdit', { sentenceId: hex(1), sentence: { do: 'move', extra: 1 } }),
    ).toThrow();
    expect(() =>
      c('paletteTap', { action: 'game', sentence: { item: { ...item, wav: 'x' } } }),
    ).toThrow();
    expect(
      c('sentenceEdit', {
        sentenceId: hex(1),
        sentence: { do: 'missing', pieces: [hex(1, 12)], mode: 'bleep' },
      }).sentence?.mode,
    ).toBe('bleep');
    expect(() =>
      c('performanceSets', { action: 'create', set: { name: 'x', audio: [] } }),
    ).toThrow();
  });

  it('lets guests read and preview, build with library editing, keep with guest Keep, and play with remote playback', () => {
    expect(isGuestAllowed(c('sentenceList'), base)).toBe(true);
    expect(isGuestAllowed(c('sentencePreview', { sentenceId: hex(1) }), base)).toBe(true);
    expect(isGuestAllowed(c('paletteTap', { action: 'preview', sentence: { item } }), base)).toBe(
      true,
    );
    expect(isGuestAllowed(c('sentenceCreate', { sentence: { text: 'hi' } }), base)).toBe(true);
    expect(isGuestAllowed(c('performance', { action: 'start' }), base)).toBe(true);
    expect(isGuestAllowed(c('sentenceKeep', { sentenceId: hex(1) }), base)).toBe(true);
    expect(isGuestAllowed(c('sentencePlay', { sentenceId: hex(1) }), base)).toBe(true);
    const noEdit = { ...base, permissions: { ...base.permissions, guestLibraryEdit: false } };
    expect(isGuestAllowed(c('sentenceCreate', { sentence: { text: 'hi' } }), noEdit)).toBe(false);
    expect(isGuestAllowed(c('paletteMark', { recordingId: hex(1), name: 'laugh' }), noEdit)).toBe(
      false,
    );
    expect(isGuestAllowed(c('sentencePreview', { sentenceId: hex(1) }), noEdit)).toBe(true);
    const noKeep = { ...base, permissions: { ...base.permissions, guestKeep: false } };
    expect(isGuestAllowed(c('sentenceKeep', { sentenceId: hex(1) }), noKeep)).toBe(false);
    const noPlay = { ...base, permissions: { ...base.permissions, remotePlayback: false } };
    expect(isGuestAllowed(c('sentencePlay', { sentenceId: hex(1) }), noPlay)).toBe(false);
    expect(isGuestAllowed(c('paletteTap', { action: 'game', sentence: { item } }), noPlay)).toBe(
      false,
    );
    expect(isGuestAllowed(c('paletteTap', { action: 'preview', sentence: { item } }), noPlay)).toBe(
      true,
    );
  });

  it('treats a word or a sentence rendered from words into the game as a region: guests need seeking (2.42 review)', () => {
    const noSeek = { ...base, permissions: { ...base.permissions, guestSeeking: false } };
    expect(isGuestAllowed(c('paletteTap', { action: 'game', sentence: { item } }), noSeek)).toBe(
      false,
    );
    expect(isGuestAllowed(c('sentencePlay', { sentenceId: hex(1) }), noSeek)).toBe(false);
    // A kept version is a finished sentence, like a sound on the board.
    expect(
      isGuestAllowed(c('sentencePlay', { sentenceId: hex(1), sentence: { version: 2 } }), noSeek),
    ).toBe(true);
    expect(isGuestAllowed(c('paletteTap', { action: 'preview', sentence: { item } }), noSeek)).toBe(
      true,
    );
    const noPlay = { ...base, permissions: { ...base.permissions, remotePlayback: false } };
    expect(
      isGuestAllowed(c('sentencePlay', { sentenceId: hex(1), sentence: { version: 2 } }), noPlay),
    ).toBe(false);
  });

  it('narrows the list’s session-wide rights by the host’s setting for this guest (2.42 review)', () => {
    const list = { canBuild: true, canKeep: true };
    const all = sentenceRights(list, { guestKeep: true }, false);
    expect([all.canBuild, all.canKeep, all.canPlayParts, all.canEdit(true)]).toEqual([
      true,
      true,
      true,
      true,
    ]);
    const off = sentenceRights(list, { guestLibraryEdit: false, guestKeep: true }, false);
    expect([off.canBuild, off.canKeep, off.canEdit(true)]).toEqual([false, false, false]);
    expect(sentenceRights(list, { guestKeep: false }, false).canKeep).toBe(false);
    expect(sentenceRights(list, { guestSeeking: false }, false).canPlayParts).toBe(false);
    // The host's own settings for guests never limit the host.
    const host = sentenceRights(list, { guestLibraryEdit: false, guestSeeking: false }, true);
    expect([host.canBuild, host.canKeep, host.canPlayParts, host.canEdit(true)]).toEqual([
      true,
      true,
      true,
      true,
    ]);
    expect(sentenceRights(null, {}, false).canBuild).toBe(false);
  });

  it('reads the snapshot field and gates the feature on the 2.42 companion and host page', () => {
    expect(parseSnapshot({ ...EMPTY, sentences: 4 }).sentences).toBe(4);
    expect(supports('2.41.0', 'sentences')).toBe(false);
    expect(supports('2.42.0', 'sentences')).toBe(true);
    expect(missingFeatures('2.41.9')).toHaveLength(5); // sentences, the three 2.43 features and backups (2.44)
    expect(siteRelaysSentences('2.41.9')).toBe(false);
    expect(siteRelaysSentences('2.42.0')).toBe(true);
  });
});

describe('kept sentences in the combined search (2.42)', () => {
  it('finds kept sentences by title or text, only when the Sentences type is shown', () => {
    const all = new Set<ResultType>(['sound', 'clip', 'sentence']);
    const sentences = [
      { id: hex(1), title: 'Spooky', text: 'behind you', versions: 2, kept: true },
      { id: hex(2), title: '', text: 'behind the door', versions: 0, kept: false },
    ];
    const found = combineResults('BEHIND', [], [], all, 60, sentences);
    expect(found).toEqual([
      {
        type: 'sentence',
        key: 'sentence:' + hex(1),
        id: hex(1),
        title: 'Spooky',
        text: 'behind you',
        versions: 2,
      },
    ]);
    expect(combineResults('spooky', [], [], all, 60, sentences)).toHaveLength(1);
    expect(combineResults('behind', [], [], new Set<ResultType>(['clip']), 60, sentences)).toEqual(
      [],
    );
    expect(combineResults('', [], [], all, 60, sentences)).toEqual([]);
  });
});

describe('the sentence store (2.42)', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSentenceStore();
  });

  it('fetches the list quietly', async () => {
    const run = vi.spyOn(controller, 'run').mockRejectedValueOnce(new Error('no'));
    await refreshSentences();
    expect(run).toHaveBeenCalledWith('sentenceList', {}, { quiet: true });
  });

  it('keeps the last list when a refresh fails, and tries again (2.42 review)', async () => {
    vi.useFakeTimers();
    try {
      const good = { revision: 1, canBuild: true, canKeep: true, sentences: [], named: [] };
      const run = vi
        .spyOn(controller, 'run')
        .mockResolvedValueOnce(good)
        .mockRejectedValueOnce(new Error('Too many requests'))
        .mockResolvedValueOnce({ ...good, revision: 2 });
      const read = sentenceStoreSnapshot;
      await refreshSentences();
      expect(read().list?.revision).toBe(1);
      await refreshSentences();
      expect(read().list?.revision).toBe(1);
      expect(read().failed).toBe(true);
      await vi.advanceTimersByTimeAsync(1100);
      expect(run).toHaveBeenCalledTimes(3);
      expect(read().list?.revision).toBe(2);
      expect(read().failed).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });
});
