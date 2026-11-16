/**
 * The example's companion version is a promise (Stage G, spec 11.4): the page shows every control of every
 * feature up to `DEMO_COMPANION`, so the example must answer every command those controls send, with data
 * the page's own schemas accept, or refuse it with an honest reason listed here. Raising the version without
 * the ops fails here, and so does answering an op of a newer feature without raising it (a stale raise).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { z, type ZodType } from 'zod';
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { commandSchema, command, type Command } from '../../web/src/protocol';
import { FEATURES, supports } from '../../web/src/version';
import { DEMO_COMPANION, UNAVAILABLE, GUEST_JOIN_MS } from '../../web/src/demo/model';
import { ALEX_BAND_MS } from '../../web/src/demo/drafts';
import { LOOP_FRAMES, msToFrames } from '../../web/src/demo/history';
import { levelsSchema, timelineSchema, transcriptSchema } from '../../web/src/mimic/history';
import {
  draftClipSchema,
  draftListSchema,
  draftSchema,
  hostBudgetSchema,
} from '../../web/src/mimic/drafts';
import {
  auditSchema,
  getSchema,
  historySearchSchema,
  listSchema as recordingsSchema,
  searchSchema,
  speakerGroupsSchema,
  statusSchema,
  vocabularySchema,
} from '../../web/src/library/voices';
import {
  alternativesSchema,
  changedSchema,
  listSchema as sentencesSchema,
  paletteSchema,
  performanceSchema,
  sentenceSchema,
  setsSchema,
  takeSchema,
} from '../../web/src/library/sentences';
import { LIBRARY_UNAVAILABLE } from '../../web/src/demo/voices';
import { HOST, setup } from './demo-engine';

type Op = Command['op'];
type Feature = keyof typeof FEATURES;

/** Ops older than every gate: the 2.3 companion answers them, and so does the example (see demo.test.ts). */
const BASE_OPS: Op[] = [
  'play',
  'stop',
  'counter',
  'reset',
  'refresh',
  'permissions',
  'icon',
  'seek',
  'pause',
  'startAt',
  'region',
  'waveform',
  'statistics',
  'clearStatistics',
  'spotify',
  'reverse',
  'keepAudio',
  'endSession',
  'beginSession',
  'audioAssets',
  'audioPlay',
  'removeAudio',
  'retryAudio',
  'retryCleanup',
  'cleanupCategories',
  'revokePeer',
];

/**
 * The ops each gated feature added or changed, from protocol.ts's command list and the controls behind each
 * `supports(…, feature)` call. An op can belong to several features (trimAudio grew with five of them).
 */
const FEATURE_OPS: Record<Feature, Op[]> = {
  mimic: ['mimicSources', 'mimicStart', 'mimicStop', 'mimicPeek', 'mimicClip'],
  transcripts: ['mimicTranscribe'],
  hotkey: ['mimicHotkey'],
  trim: ['trimAudio'],
  effects: ['trimAudio'],
  hotkeyLength: ['mimicHotkey'],
  preview: ['mimicPreview'],
  hotkeyKey: ['mimicHotkey'],
  voiceReplayLibrary: ['mimicLibrary', 'mimicLibraryImport'],
  speed: ['trimAudio'],
  keepPitch: ['trimAudio'],
  clearTemp: ['clearTemp'],
  trimPreview: ['trimPreview'],
  trimWords: ['trimAudio'],
  undoStop: ['removeAudio'],
  guestUndo: ['removeAudio'],
  intents: ['panic'],
  // The companion window and the guest settings it keeps travel outside commands (bridge messages).
  desktopSession: [],
  sessionControl: [],
  history: [
    'historyTimeline',
    'historyLevels',
    'historyTranscript',
    'historyShare',
    'historyUnshare',
    'historySettings',
    'historyDiscard',
    'historyCorrect',
    'revokePeer',
    'beginSession',
  ],
  drafts: [
    'draftCreate',
    'draftList',
    'draftUpdate',
    'draftDiscard',
    'draftCopy',
    'draftKeep',
    'draftPlay',
    'keepSettings',
    'budgetStatus',
    'budgetGrant',
    'tempRecover',
    'mimicPreview',
    'keepAudio',
  ],
  reviewFixes: ['historyDiscard'],
  voiceLibrary: [
    'libraryStatus',
    'libraryList',
    'libraryGet',
    'librarySearch',
    'libraryVocabulary',
    'libraryAudit',
    'libraryEdit',
    'libraryRemove',
    'libraryUndo',
    'libraryVoice',
    'libraryWanted',
    'libraryAdd',
    'libraryRetry',
    'libraryPreview',
    'librarySettings',
    'libraryIndex',
    'historySearch',
    'speakerGroups',
    'speakerName',
    'speakerLink',
  ],
  sentences: [
    'sentenceList',
    'sentenceCreate',
    'sentenceGet',
    'sentenceText',
    'sentenceEdit',
    'sentenceAlternatives',
    'sentenceDiscard',
    'sentencePreview',
    'sentenceKeep',
    'sentencePlay',
    'paletteSearch',
    'paletteTap',
    'paletteMark',
    'performance',
    'performanceSets',
  ],
  kickSession: ['counter'],
  soundEdit: ['trimAudio', 'trimPreview'],
  uiSync: ['uiPrefsGet', 'uiPrefsSet'],
  // Stage H (2.44): never shown in the example (DEMO_COMPANION stays 2.43.0).
  backups: [
    'backupSettings',
    'backupNow',
    'backupList',
    'backupDetail',
    'backupRestore',
    'backupAttach',
    'backupLayout',
  ],
  updater: [
    'updateStatus',
    'updateCheck',
    'updateDownload',
    'updateInstall',
    'updateLater',
    'updateSettings',
  ],
  organize: [
    'organizeGet',
    'organizeEdit',
    'organizeApply',
    'organizeResolve',
    'organizeDiscard',
    'organizePropose',
    'organizeWithdraw',
    'organizeSettings',
  ],
  missingFiles: [],
  setup: ['setupGet', 'setupSet', 'mimicSources'],
  hostHotkeys: ['hotkeysGet', 'hotkeysSet'],
  feedback: ['feedbackSend'],
  noLimits: ['budgetStatus'],
  guestTempClear: ['tempRemoveMine'],
  tempUndo: ['tempRestore', 'removeAudio'],
};

/** What the example honestly cannot do, and the reason it gives. Each must be refused with exactly that. */
const HONESTLY_UNAVAILABLE: Partial<Record<Op, string>> = {
  mimicHotkey: UNAVAILABLE.hotkey,
  mimicLibrary: UNAVAILABLE.library,
  mimicLibraryImport: UNAVAILABLE.library,
  keepAudio: UNAVAILABLE.keep,
  historyShare: UNAVAILABLE.share,
  keepSettings: UNAVAILABLE.other,
  budgetGrant: UNAVAILABLE.other,
  tempRecover: UNAVAILABLE.other,
  libraryAdd: LIBRARY_UNAVAILABLE.add,
  libraryIndex: LIBRARY_UNAVAILABLE.index,
  speakerName: LIBRARY_UNAVAILABLE.speakers,
  speakerLink: LIBRARY_UNAVAILABLE.speakers,
  // Host style sync (2.43): the example's style stays on this browser; the page never asks (not a host page).
  uiPrefsGet: UNAVAILABLE.other,
  uiPrefsSet: UNAVAILABLE.other,
};

const wav = z.object({ wav: z.string().min(1), seconds: z.number().positive() });
const temp = z.object({ assetId: z.uuid(), state: z.literal('temporary') });
const nothing = z.undefined();

type Context = {
  sessionId: string;
  live: number;
  clip: string;
  draft: z.infer<typeof draftSchema>;
  band: string;
  recording: string;
  voice: string;
  sentence: z.infer<typeof sentenceSchema>;
  named: string;
  /** One of the example's own sounds (not Temp). */
  sound: string;
};
const group = z.object({ group: z.number().int().positive() });
const played = z.object({ played: z.literal(true), epoch: z.uuid() });
type Check = (ctx: Context) => { fields?: Partial<Command>; schema: ZodType };

/** One schema-checked command per answered op, in the order a visitor might use them. */
const CHECKS: [Op, Check][] = [
  [
    'mimicSources',
    () => ({
      schema: z.object({
        sources: z.array(z.object({ id: z.string(), name: z.string(), kind: z.string() })).min(1),
      }),
    }),
  ],
  ['mimicTranscribe', () => ({ fields: { action: 'enable' }, schema: nothing })],
  [
    'mimicPeek',
    () => ({
      schema: z.object({
        end: z.number(),
        rate: z.number(),
        windowMs: z.number(),
        levels: z.array(z.number()),
        transcript: z.array(z.object({ startMs: z.number(), endMs: z.number(), text: z.string() })),
        ringId: z.string(),
      }),
    }),
  ],
  ['historyTimeline', () => ({ schema: timelineSchema })],
  [
    'historyLevels',
    (c) => ({
      fields: { startFrame: 0, endFrame: c.live, bins: 300, sourceId: c.sessionId },
      schema: levelsSchema,
    }),
  ],
  [
    'historyTranscript',
    (c) => ({
      fields: { startFrame: 0, endFrame: c.live, sourceId: c.sessionId },
      schema: transcriptSchema,
    }),
  ],
  [
    'mimicClip',
    (c) => ({
      fields: { startFrame: 0, endFrame: msToFrames(3000), sourceId: c.sessionId },
      schema: temp,
    }),
  ],
  [
    'mimicPreview',
    (c) => ({
      fields: { startFrame: 0, endFrame: msToFrames(3000), sourceId: c.sessionId },
      schema: wav,
    }),
  ],
  ['historySettings', () => ({ fields: { seconds: 900, subtitles: 'balanced' }, schema: nothing })],
  ['historyCorrect', () => ({ fields: { wordId: 1, text: 'Thats' }, schema: z.object({}) })],
  ['historyDiscard', () => ({ schema: z.object({ discarded: z.literal(0) }) })],
  ['historyUnshare', () => ({ schema: nothing })],
  ['revokePeer', () => ({ fields: { guestId: 'x' }, schema: nothing })],
  ['beginSession', () => ({ fields: { newInvite: true }, schema: nothing })],
  ['draftList', () => ({ schema: draftListSchema })],
  [
    'draftUpdate',
    (c) => ({
      fields: { draftId: c.draft.id, name: 'Renamed', sourceId: c.sessionId },
      schema: draftSchema,
    }),
  ],
  ['draftCopy', (c) => ({ fields: { draftId: c.band }, schema: draftSchema })],
  [
    'draftPlay',
    (c) => ({
      fields: { draftId: c.draft.id, sourceId: c.sessionId, autoplay: true },
      schema: draftClipSchema.extend({ played: z.literal(true) }),
    }),
  ],
  [
    'draftKeep',
    (c) => ({
      fields: { draftId: c.draft.id, sourceId: c.sessionId },
      schema: draftClipSchema.extend({ assetId: z.uuid() }),
    }),
  ],
  [
    'draftDiscard',
    (c) => ({ fields: { draftId: c.draft.id }, schema: z.object({ discarded: z.literal(true) }) }),
  ],
  ['budgetStatus', () => ({ schema: hostBudgetSchema })],
  [
    'trimAudio',
    (c) => ({
      fields: { assetId: c.clip, action: 'speed', speed: 1.5, keepPitch: true, endMs: 2000 },
      schema: temp,
    }),
  ],
  ['trimPreview', (c) => ({ fields: { assetId: c.clip, action: 'echo' }, schema: wav })],
  // 2.43 (soundEdit): Speed and effects on any example sound.
  [
    'trimAudio',
    (c) => ({
      fields: { soundId: c.sound, action: 'speed', speed: 1.5, keepPitch: true, endMs: 1000 },
      schema: temp,
    }),
  ],
  ['trimPreview', (c) => ({ fields: { soundId: c.sound, action: 'echo' }, schema: wav })],
  [
    'removeAudio',
    (c) => ({ fields: { assetId: c.clip }, schema: z.object({ removed: z.literal(true) }) }),
  ],
  ['clearTemp', () => ({ schema: z.object({ cleared: z.number(), pending: z.number() }) })],
  // The voice library (2.41).
  ['libraryStatus', () => ({ schema: statusSchema.extend({ count: z.literal(3) }) })],
  ['libraryList', () => ({ fields: { reviewed: false, limit: 100 }, schema: recordingsSchema })],
  ['libraryGet', (c) => ({ fields: { recordingId: c.recording }, schema: getSchema })],
  [
    'librarySearch',
    () => ({
      fields: { query: 'eagle', limit: 100 },
      schema: searchSchema.extend({ total: z.literal(1) }),
    }),
  ],
  [
    'libraryVocabulary',
    (c) => ({ fields: { voiceId: c.voice, limit: 100 }, schema: vocabularySchema }),
  ],
  [
    'libraryEdit',
    (c) => ({ fields: { recordingId: c.recording, addTags: ['moon'] }, schema: group }),
  ],
  [
    'libraryAudit',
    (c) => ({
      fields: { recordingId: c.recording },
      schema: z.object({ entries: z.array(auditSchema).min(1) }),
    }),
  ],
  ['libraryUndo', () => ({ fields: { auditId: 1 }, schema: group })],
  ['libraryVoice', () => ({ fields: { action: 'create', name: 'Robo' }, schema: group })],
  [
    'libraryWanted',
    (c) => ({ fields: { action: 'add', voiceId: c.voice, query: 'hello' }, schema: group }),
  ],
  ['libraryRetry', (c) => ({ fields: { recordingId: c.recording }, schema: z.object({}) })],
  [
    'libraryPreview',
    (c) => ({ fields: { recordingId: c.recording, positionMs: 300, endMs: 600 }, schema: wav }),
  ],
  ['librarySettings', () => ({ fields: { guestLibraryEdit: false }, schema: nothing })],
  [
    'historySearch',
    (c) => ({ fields: { query: 'landed', sourceId: c.sessionId }, schema: historySearchSchema }),
  ],
  ['speakerGroups', () => ({ schema: speakerGroupsSchema })],
  // Sentences and the speaking palette (2.42).
  [
    'sentenceList',
    () => ({ schema: sentencesSchema.extend({ named: sentencesSchema.shape.named.min(1) }) }),
  ],
  ['sentenceGet', (c) => ({ fields: { sentenceId: c.sentence.id }, schema: sentenceSchema })],
  [
    'sentenceText',
    (c) => ({
      fields: { sentenceId: c.sentence.id, sentence: { text: 'one small step [laugh] hello' } },
      schema: sentenceSchema,
    }),
  ],
  [
    'sentenceEdit',
    (c) => ({
      fields: {
        sentenceId: c.sentence.id,
        sentence: { do: 'missing', pieces: [c.sentence.pieces.at(-1)!.id], mode: 'bleep' },
      },
      schema: changedSchema,
    }),
  ],
  [
    'sentenceAlternatives',
    (c) => ({
      fields: { sentenceId: c.sentence.id, sentence: { pieces: [c.sentence.pieces[0].id] } },
      schema: alternativesSchema,
    }),
  ],
  ['sentencePreview', (c) => ({ fields: { sentenceId: c.sentence.id }, schema: wav })],
  [
    'sentenceKeep',
    (c) => ({
      fields: { sentenceId: c.sentence.id },
      schema: z.object({ version: z.literal(1), durationMs: z.number().positive() }),
    }),
  ],
  [
    'sentencePlay',
    (c) => ({ fields: { sentenceId: c.sentence.id, sentence: { version: 1 } }, schema: played }),
  ],
  ['paletteSearch', () => ({ fields: { query: 'step' }, schema: paletteSchema })],
  ['performance', () => ({ fields: { action: 'start' }, schema: performanceSchema })],
  [
    'paletteTap',
    (c) => ({
      fields: {
        action: 'game',
        sentence: { item: { namedId: c.named, startMs: 0, endMs: 0, text: '[laugh]' } },
      },
      schema: played,
    }),
  ],
  // The tap above was recorded: stopping makes it an editable take.
  ['performance', () => ({ fields: { action: 'stop' }, schema: takeSchema })],
  [
    'paletteMark',
    (c) => ({
      fields: { name: 'gasp', recordingId: c.recording, sentence: { startMs: 100, endMs: 600 } },
      schema: z.object({ namedId: z.string(), name: z.literal('gasp') }),
    }),
  ],
  [
    'performanceSets',
    () => ({ fields: { action: 'create', set: { name: 'Moon' } }, schema: setsSchema }),
  ],
  [
    'sentenceDiscard',
    (c) => ({
      fields: { sentenceId: c.sentence.id },
      schema: z.object({ removed: z.literal(true) }),
    }),
  ],
  ['libraryRemove', (c) => ({ fields: { recordingId: c.recording }, schema: group })],
  [
    'panic',
    () => ({ fields: { action: 'enable' }, schema: z.object({ suspended: z.literal(true) }) }),
  ],
  ['mimicStop', () => ({ schema: nothing })],
];
const CHECKED = new Set(CHECKS.map(([op]) => op));
/** Ops the scenario sends to build its context (their answers are checked there). */
const SETUP: Op[] = ['mimicStart', 'draftCreate', 'sentenceCreate'];

const inScope = (f: Feature) => supports(DEMO_COMPANION, f);
const scoped = (want: boolean) =>
  [
    ...new Set(
      (Object.keys(FEATURE_OPS) as Feature[])
        .filter((f) => inScope(f) === want)
        .flatMap((f) => FEATURE_OPS[f]),
    ),
  ].sort();

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(Date.UTC(2026, 10, 14, 12));
});
afterEach(() => {
  vi.useRealTimers();
});

describe('example coverage of DEMO_COMPANION', () => {
  it('classifies every command of the protocol', () => {
    const known = new Set([...BASE_OPS, ...Object.values(FEATURE_OPS).flat()]);
    expect(commandSchema.shape.op.options.filter((op) => !known.has(op))).toEqual([]);
    // Every gate in the website is a feature here (the type checks the keys; this checks the call sites).
    const files = readdirSync(resolve('web/src'), { recursive: true, encoding: 'utf8' }).filter(
      (f) => /\.tsx?$/.test(f),
    );
    const gates = files.flatMap((f) =>
      [
        ...readFileSync(resolve('web/src', f), 'utf8').matchAll(/supports\([^,()]+,\s*'(\w+)'\)/g),
      ].map((m) => m[1]),
    );
    expect(gates.length).toBeGreaterThan(40);
    expect(gates.filter((f) => !(f in FEATURE_OPS))).toEqual([]);
  });

  it('answers every op of every feature it reports, with data the page accepts', async () => {
    const { model, send, state } = setup();
    model.begin();
    const run = async (op: Op, fields: Partial<Command> = {}) =>
      model.send(command(op, fields), HOST).then(
        (data) => ({ ok: true as const, data }),
        (e: Error) => ({ ok: false as const, error: e.message }),
      );
    // Mimic listens for half a minute; Alex joins, and captures a phrase after the host's first draft.
    expect(await send('mimicStart', { sourceId: 'example:apollo-11' })).toBeUndefined();
    vi.advanceTimersByTime(GUEST_JOIN_MS);
    vi.setSystemTime(Date.now() + 26000);
    const draft = draftSchema.parse(await send('draftCreate', { action: 'phrase' }));
    await vi.advanceTimersByTimeAsync(ALEX_BAND_MS);
    const timeline = timelineSchema.parse(await send('historyTimeline'));
    const band = draftListSchema.parse(await send('draftList')).others[0];
    expect(band).toBeDefined();
    const clip = temp.parse(
      await send('mimicClip', {
        startFrame: LOOP_FRAMES / 4,
        endFrame: LOOP_FRAMES / 4 + msToFrames(2500),
        sourceId: timeline.sessionId!,
      }),
    ).assetId;
    // The voice library and a sentence with a recorded phrase, the named laugh and a missing word.
    const status = statusSchema.parse(await send('libraryStatus'));
    const recordings = recordingsSchema.parse(await send('libraryList', { limit: 100 }));
    const sentence = sentenceSchema.parse(
      await send('sentenceCreate', { sentence: { text: 'one small step [laugh] hello' } }),
    );
    expect(sentence.pieces.map((p) => p.kind)).toEqual(['word', 'named', 'missing']);
    const sentences = sentencesSchema.parse(await send('sentenceList'));
    const ctx: Context = {
      sessionId: timeline.sessionId!,
      live: timeline.liveFrame,
      clip,
      draft,
      band: band.id,
      recording: recordings.items.find((r) => r.voiceId === status.voices[0].id)!.id,
      voice: status.voices[0].id,
      sentence,
      named: sentences.named[0].id,
      sound: state().library.sounds.find((s) => !s.temporary)!.id,
    };
    const problems: string[] = [];
    for (const [op, check] of CHECKS) {
      const { fields, schema } = check(ctx);
      const result = await run(op, fields);
      if (!result.ok) problems.push(`${op}: refused (${result.error})`);
      else if (!schema.safeParse(result.data).success)
        problems.push(`${op}: ${JSON.stringify(schema.safeParse(result.data).error?.issues[0])}`);
    }
    for (const [op, reason] of Object.entries(HONESTLY_UNAVAILABLE) as [Op, string][]) {
      const result = await run(op, op === 'keepAudio' ? { assetId: clip } : {});
      if (result.ok || result.error !== reason)
        problems.push(`${op}: should be refused with “${reason}”, got ${JSON.stringify(result)}`);
    }
    // Every op in scope is checked, set up or honestly unavailable.
    for (const op of scoped(true))
      if (
        !CHECKED.has(op) &&
        !SETUP.includes(op) &&
        !BASE_OPS.includes(op) &&
        !(op in HONESTLY_UNAVAILABLE)
      )
        problems.push(`${op}: in scope (DEMO_COMPANION ${DEMO_COMPANION}) but not checked`);
    expect(problems).toEqual([]);
    expect(state().companionVersion).toBe(DEMO_COMPANION);
    model.close();
  });

  it('answers no op that only a newer feature has (raise DEMO_COMPANION with the ops it unlocks)', async () => {
    const { model } = setup();
    const older = new Set([...BASE_OPS, ...scoped(true)]);
    const newer = scoped(false).filter((op) => !older.has(op));
    // Since 2.43 the example reports every feature there is, so nothing is newer; this guards the next one.
    if (!newer.length) {
      expect((Object.keys(FEATURES) as Feature[]).filter((f) => !inScope(f))).toEqual([]);
      model.close();
      return;
    }
    const answered: string[] = [];
    for (const op of newer) {
      const outcome = await model.send(command(op), HOST).then(
        () => 'answered',
        (e: Error) => e.message,
      );
      if (outcome !== UNAVAILABLE.other) answered.push(`${op}: ${outcome}`);
    }
    expect(answered).toEqual([]);
    model.close();
  });

  it('refuses the permanent keep of a draft, which needs the host’s PC', async () => {
    const { send } = setup();
    await send('mimicStart', { sourceId: 'example:apollo-11' });
    vi.setSystemTime(Date.now() + 12000);
    const draft = draftSchema.parse(await send('draftCreate', { action: 'recent' }));
    await expect(send('draftKeep', { draftId: draft.id, action: 'library' })).rejects.toThrow(
      UNAVAILABLE.keep,
    );
  });
});
