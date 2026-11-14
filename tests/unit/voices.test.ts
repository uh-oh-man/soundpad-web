import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  NOTHING_PICKED,
  NO_FILTERS,
  canEditLibrary,
  combineResults,
  countTypes,
  describeAudit,
  extendQueue,
  filterFields,
  formatDuration,
  hasFilters,
  initials,
  pick,
  playRange,
  queueDone,
  reviewAction,
  searchSchema,
  startQueue,
  statusSchema,
  stepQueue,
  type ResultType,
  type SearchResult,
} from '../../web/src/library/voices';
import {
  commandSchema,
  EMPTY,
  guestReply,
  isGuestAllowed,
  parseSnapshot,
  type Sound,
} from '../../web/src/protocol';
import { missingFeatures, siteRelaysLibrary, supports } from '../../web/src/version';
import { effectivePermissions, PERMISSION_KEYS } from '../../web/src/network/policy';
import { controller } from '../../web/src/controller';
import {
  noticeText,
  refreshLibrary,
  requestOpen,
  resetVoiceStore,
  takeOpen,
} from '../../web/src/library/voice-store';

const hex = (n: number) => n.toString(16).padStart(32, '0');
const sound = (id: string, name: string, extra: Partial<Sound> = {}): Sound => ({
  id,
  name,
  icon: null,
  ...extra,
});
const clip = (n: number, title: string, hit = 'tone'): SearchResult => ({
  type: 'clip',
  id: hex(n),
  soundId: 's' + n,
  title,
  voiceId: null,
  durationMs: 1200,
  matches: 1,
  match: 1,
  length: 1,
  context: [
    { i: 0, s: 0, e: 300, t: 'Fixture', hit: false, u: false },
    { i: 1, s: 300, e: 600, t: hit, hit: true, u: false },
  ],
});
const indexed = (id: string, title: string): SearchResult => ({
  ...clip(9, title),
  type: 'sound',
  id,
  soundId: id,
});
const ALL = new Set<ResultType>(['sound', 'clip', 'sentence']);

describe('voice library search (2.41)', () => {
  it('combines Sounds by name, indexed Sounds by their words, and Voice clips, with type filters', () => {
    const sounds = [
      sound('a', 'Tone deaf'),
      sound('b', 'Airhorn'),
      sound('c', 'Laugh'),
      sound('d', 'tone (internal)', { internalVariant: true }),
    ];
    const results = [clip(1, 'Robot line'), indexed('c', 'Laugh'), indexed('a', 'Tone deaf')];
    const all = combineResults('tone', sounds, results, ALL);
    // A sound found by name and by its words shows once, with the words; an internal variant never shows.
    expect(all.map((r) => r.key)).toEqual(['sound:a', 'sound:c', 'clip:' + hex(1)]);
    expect(all[0].type === 'sound' && all[0].words?.id).toBe('a');
    expect(countTypes(all)).toEqual({ sound: 2, clip: 1, sentence: 0 });
    // Filters by type; Sentences are an empty type until the sentence builder (Stage F2).
    expect(combineResults('tone', sounds, results, new Set(['clip'])).map((r) => r.type)).toEqual([
      'clip',
    ]);
    expect(combineResults('tone', sounds, results, new Set(['sentence']))).toEqual([]);
    expect(combineResults('tone', sounds, results, ALL, 1)).toHaveLength(1);
  });

  it('turns filters into command fields and plays a word with padding', () => {
    expect(filterFields(NO_FILTERS)).toEqual({});
    expect(hasFilters(NO_FILTERS)).toBe(false);
    expect(filterFields({ voice: hex(3), tag: 'laugh', favorite: true, query: 'x' })).toEqual({
      voiceId: hex(3),
      tag: 'laugh',
      favorite: true,
    });
    expect(filterFields({ ...NO_FILTERS, voice: 'none' })).toEqual({ unassigned: true });
    expect(playRange({ s: 300, e: 600 })).toEqual({ positionMs: 280, endMs: 610 });
    expect(playRange({ s: 5, e: 40 })).toEqual({ positionMs: 0, endMs: 50 });
    expect(playRange({ s: 300, e: 600 }, { s: 600, e: 900 })).toEqual({
      positionMs: 280,
      endMs: 910,
    });
    // Every field the page sends passes the strict command check.
    expect(() =>
      commandSchema.parse({
        v: 2,
        id: crypto.randomUUID(),
        at: 1,
        op: 'librarySearch',
        query: 'tone',
        kind: 'all',
        ...filterFields({ voice: hex(3), tag: 't', favorite: true, query: '' }),
        limit: 100,
      }),
    ).not.toThrow();
    expect(() =>
      commandSchema.parse({
        v: 2,
        id: crypto.randomUUID(),
        at: 1,
        op: 'libraryEdit',
        recordingIds: [hex(1), hex(2)],
        voiceId: '',
        addTags: ['funny'],
        favorite: true,
        reviewed: true,
        correctedText: 'what they said',
      }),
    ).not.toThrow();
    expect(() =>
      commandSchema.parse({
        v: 2,
        id: crypto.randomUUID(),
        at: 1,
        op: 'libraryEdit',
        recordingId: 'not-an-id',
      }),
    ).toThrow();
  });

  it('reads the companion answers and says what a change was', () => {
    const status = statusSchema.parse({
      revision: 4,
      blocked: false,
      problem: null,
      count: 2,
      unreviewed: 1,
      pending: 0,
      voices: [{ id: hex(5), name: 'Alex Mercer', color: '#3fb68b', count: 2 }],
      unassigned: 0,
      tags: ['laugh'],
      wanted: [
        { id: hex(6), voiceId: hex(5), text: 'hello', byName: 'Host', available: true, count: 1 },
      ],
      canEdit: true,
      settings: {
        guestLibraryEdit: true,
        groupSpeakers: false,
        indexCategories: ['Reactions'],
        indexPaused: false,
      },
      index: { total: 2, indexed: 1, failed: 0, paused: false, current: null, active: true },
      worker: '',
      notices: [
        {
          id: 1,
          kind: 'history',
          voiceId: hex(5),
          voiceName: 'Alex Mercer',
          text: 'hello',
          recordingId: null,
          sessionId: 's',
          startFrame: 10,
          endFrame: 20,
          at: 1,
        },
      ],
    });
    expect(initials(status.voices[0].name)).toBe('AM');
    expect(initials('zed')).toBe('ZE');
    expect(initials('  ')).toBe('?');
    expect(noticeText(status.notices[0])).toBe(
      'Possible match for Alex Mercer: “hello” was just said.',
    );
    expect(
      searchSchema.parse({ total: 1, offset: 0, results: [clip(1, 'A')], sentences: [] }).results,
    ).toHaveLength(1);
    const entry = {
      id: 3,
      group: 3,
      at: 1,
      byName: 'Bob',
      target: 'recording',
      targetId: hex(1),
      field: 'voice',
      before: null,
      after: JSON.stringify(hex(5)),
      undoOf: null,
    };
    expect(describeAudit(entry, status.voices)).toBe('Bob set the voice to Alex Mercer');
    expect(
      describeAudit({ ...entry, field: 'tags', after: '["a","b"]', undoOf: 2 }, status.voices),
    ).toBe('Bob set the tags: a, b (Undo)');
    expect(formatDuration(1234)).toBe('1.2 s');
    expect(formatDuration(125000)).toBe('2:05');
  });
});

describe('review queue and bulk selection (2.41)', () => {
  it('maps keys to review actions and leaves typing alone', () => {
    expect(reviewAction(' ')).toEqual({ kind: 'preview' });
    expect(reviewAction('Enter')).toEqual({ kind: 'keep' });
    expect(reviewAction('s')).toEqual({ kind: 'skip' });
    expect(reviewAction('ArrowRight')).toEqual({ kind: 'skip' });
    expect(reviewAction('ArrowLeft')).toEqual({ kind: 'back' });
    expect(reviewAction('f')).toEqual({ kind: 'favorite' });
    expect(reviewAction('3')).toEqual({ kind: 'voice', index: 2 });
    expect(reviewAction('0')).toBeNull();
    expect(reviewAction('Enter', { typing: true })).toBeNull();
    expect(reviewAction('s', { typing: true })).toBeNull();
    expect(reviewAction('Escape', { typing: true })).toEqual({ kind: 'leave' });
    expect(reviewAction('s', { ctrl: true })).toBeNull();
  });

  it('keeps, skips, goes back and takes newcomers at the end', () => {
    let q = startQueue(['a', 'b', 'c']);
    q = stepQueue(q, 'keep');
    q = stepQueue(q, 'skip');
    expect(q).toMatchObject({ at: 2, kept: ['a'], skipped: ['b'] });
    q = stepQueue(q, 'back');
    q = stepQueue(q, 'keep'); // b, changed its mind
    expect(q).toMatchObject({ at: 2, kept: ['a', 'b'], skipped: [] });
    q = extendQueue(q, ['c', 'd', 'a']);
    expect(q.ids).toEqual(['a', 'b', 'c', 'd']);
    q = stepQueue(stepQueue(q, 'keep'), 'keep');
    expect(queueDone(q)).toBe(true);
    expect(stepQueue(q, 'keep')).toBe(q); // nothing left: unchanged
  });

  it('selects one by one, or a range with Shift', () => {
    const order = ['a', 'b', 'c', 'd', 'e'];
    let p = pick(NOTHING_PICKED, order, 'b', false);
    p = pick(p, order, 'd', true);
    expect(p.ids).toEqual(['b', 'c', 'd']);
    p = pick(p, order, 'c', false);
    expect(p.ids).toEqual(['b', 'd']);
  });
});

describe('permissions, snapshot and gates (2.41)', () => {
  const base = {
    ...EMPTY,
    permissions: { remotePlayback: true, guestCounter: false, guestMimic: true, guestKeep: true },
  };
  const c = (op: string, extra: object = {}) =>
    commandSchema.parse({ v: 2, id: crypto.randomUUID(), at: 1, op, ...extra });

  it('lets guests read the library, edit it unless the host says no, and keeps settings the host’s', () => {
    expect(isGuestAllowed(c('libraryStatus'), base)).toBe(true);
    expect(isGuestAllowed(c('librarySearch', { query: 'x' }), base)).toBe(true);
    expect(isGuestAllowed(c('libraryPreview', { recordingId: hex(1) }), base)).toBe(true);
    expect(isGuestAllowed(c('libraryEdit', { recordingId: hex(1), tags: ['a'] }), base)).toBe(true);
    const off = { ...base, permissions: { ...base.permissions, guestLibraryEdit: false } };
    expect(isGuestAllowed(c('libraryEdit', { recordingId: hex(1), tags: ['a'] }), off)).toBe(false);
    expect(isGuestAllowed(c('libraryUndo', { auditId: 3 }), off)).toBe(false);
    expect(isGuestAllowed(c('librarySearch', { query: 'x' }), off)).toBe(true);
    expect(isGuestAllowed(c('libraryIndex', { categories: ['A'] }), base)).toBe(false);
    expect(isGuestAllowed(c('librarySettings', { groupSpeakers: true }), base)).toBe(false);
    expect(isGuestAllowed(c('historySearch', { query: 'x' }), base)).toBe(true);
    expect(isGuestAllowed(c('speakerName', { groupId: 1, name: 'A' }), off)).toBe(false);
    // Into the voice library from a draft needs guest Keep, like any Keep.
    expect(isGuestAllowed(c('draftKeep', { draftId: hex(1), action: 'library' }), base)).toBe(true);
    expect(
      isGuestAllowed(c('draftKeep', { draftId: hex(1), action: 'library' }), {
        ...base,
        permissions: { ...base.permissions, guestKeep: false },
      }),
    ).toBe(false);
    // A per-guest override can switch library editing off for one guest.
    expect(PERMISSION_KEYS).toContain('guestLibraryEdit');
    expect(
      effectivePermissions(base.permissions, { guestLibraryEdit: false }).guestLibraryEdit,
    ).toBe(false);
  });

  it('reads the snapshot fields and gates the feature on the 2.41 companion and host page', () => {
    const snapshot = parseSnapshot({
      ...EMPTY,
      voiceLibrary: 7,
      permissions: { ...EMPTY.permissions, guestLibraryEdit: false },
    });
    expect(snapshot.voiceLibrary).toBe(7);
    expect(snapshot.permissions.guestLibraryEdit).toBe(false);
    expect(supports('2.40.0', 'voiceLibrary')).toBe(false);
    expect(supports('2.41.0', 'voiceLibrary')).toBe(true);
    expect(missingFeatures('2.40.3')).toHaveLength(2); // the voice library and the 2.41 review fixes
    expect(siteRelaysLibrary('2.40.9')).toBe(false);
    expect(siteRelaysLibrary('2.41.0')).toBe(true);
  });

  it('gives history matches only to a guest allowed Mimic, by their own setting (2.41 review)', () => {
    const notices = [
      { id: 1, kind: 'recording', recordingId: hex(1) },
      { id: 2, kind: 'history', sessionId: 's', startFrame: 1, endFrame: 2 },
    ];
    const reply = { revision: 3, notices };
    const status = c('libraryStatus');
    expect(guestReply(status, reply, base)).toEqual(reply);
    const noMimic = { ...base, permissions: { ...base.permissions, guestMimic: false } };
    expect(guestReply(status, reply, noMimic)).toEqual({ revision: 3, notices: [notices[0]] });
    // Other replies pass as they are.
    expect(guestReply(c('libraryList'), reply, noMimic)).toBe(reply);
  });

  const statusOf = (canEdit: boolean) =>
    statusSchema.parse({
      revision: 1,
      blocked: false,
      problem: null,
      count: 0,
      unreviewed: 0,
      pending: 0,
      voices: [],
      unassigned: 0,
      tags: [],
      wanted: [],
      canEdit,
      settings: { guestLibraryEdit: canEdit, groupSpeakers: false, indexPaused: false },
      index: { total: 0, indexed: 0, failed: 0, paused: false, current: null, active: false },
      worker: '',
      notices: [],
    });
  it('lets a guest edit only when both the companion and the host’s own setting for them allow it', () => {
    const status = statusOf(true);
    expect(canEditLibrary(status, {}, false)).toBe(true);
    expect(canEditLibrary(status, { guestLibraryEdit: false }, false)).toBe(false);
    expect(canEditLibrary(status, { guestLibraryEdit: false }, true)).toBe(true);
    expect(canEditLibrary(statusOf(false), {}, false)).toBe(false);
    expect(canEditLibrary(null, {}, true)).toBe(false);
  });
});

describe('the library store (2.41)', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetVoiceStore();
  });

  it('fetches the status quietly and empties it when the companion refuses', async () => {
    const run = vi.spyOn(controller, 'run').mockRejectedValueOnce(new Error('no'));
    await refreshLibrary();
    expect(run).toHaveBeenCalledWith('libraryStatus', {}, { quiet: true });
  });

  it('holds a jump request until the window that shows it takes it, once', () => {
    requestOpen({ kind: 'history', sessionId: 's', startFrame: 1, endFrame: 2 });
    expect(takeOpen('recording')).toBeNull();
    expect(takeOpen('history')).toEqual({
      kind: 'history',
      sessionId: 's',
      startFrame: 1,
      endFrame: 2,
    });
    expect(takeOpen('history')).toBeNull();
  });
});
