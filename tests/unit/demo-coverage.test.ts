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
};
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
  [
    'removeAudio',
    (c) => ({ fields: { assetId: c.clip }, schema: z.object({ removed: z.literal(true) }) }),
  ],
  ['clearTemp', () => ({ schema: z.object({ cleared: z.number(), pending: z.number() }) })],
  [
    'panic',
    () => ({ fields: { action: 'enable' }, schema: z.object({ suspended: z.literal(true) }) }),
  ],
  ['mimicStop', () => ({ schema: nothing })],
];
const CHECKED = new Set(CHECKS.map(([op]) => op));
/** Ops the scenario sends to build its context (their answers are checked there). */
const SETUP: Op[] = ['mimicStart', 'draftCreate'];

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
    const ctx: Context = {
      sessionId: timeline.sessionId!,
      live: timeline.liveFrame,
      clip,
      draft,
      band: band.id,
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
    expect(newer.length).toBeGreaterThan(0);
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
