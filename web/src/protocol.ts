import { z } from 'zod';
export const commandSchema = z
  .object({
    v: z.union([z.literal(1), z.literal(2)]),
    id: z.uuid(),
    at: z.number().int().safe(),
    op: z.enum([
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
      'clearTemp',
      'revokePeer',
      'mimicSources',
      'mimicStart',
      'mimicStop',
      'mimicPeek',
      'mimicClip',
      'mimicTranscribe',
      'mimicHotkey',
      'mimicPreview',
      'mimicLibrary',
      'mimicLibraryImport',
      'trimAudio',
      'trimPreview',
      // Host only (2.35): stops Soundpad at once and holds guest playback until the host resumes it.
      'panic',
      // Recording history (2.39). Timeline, levels and transcript are what a guest with Mimic may ask for,
      // scoped by the companion to what was recorded since they joined; the rest is the host's.
      'historyTimeline',
      'historyLevels',
      'historyTranscript',
      'historyShare',
      'historyUnshare',
      'historySettings',
      'historyDiscard',
      'historyCorrect',
      // Drafts (2.40): one person's editable selection on the history, pinned by the companion. A guest
      // with Mimic has their own; the companion scopes every one to what that guest may see.
      'draftCreate',
      'draftList',
      'draftUpdate',
      'draftDiscard',
      'draftCopy',
      'draftKeep',
      'draftPlay',
      // Keep and guest storage (2.40): settings and grants are the host's; a guest may read their own status.
      'keepSettings',
      'budgetStatus',
      'budgetGrant',
      // After a crash (2.40): the host recovers or discards the previous session's Temp.
      'tempRecover',
      // The voice library (2.41): everyone may read and search it; editing needs guestLibraryEdit for
      // guests; settings and the category word index are the host's.
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
      // Word search in the recording history, and speaker groups (2.41): guests with Mimic, scoped.
      'historySearch',
      'speakerGroups',
      'speakerName',
      'speakerLink',
    ]),
    // Voice library (2.41): which recording(s), metadata, an audit group to undo, search text and paging.
    recordingId: z
      .string()
      .regex(/^[a-f0-9]{32}$/)
      .optional(),
    recordingIds: z
      .array(z.string().regex(/^[a-f0-9]{32}$/))
      .min(1)
      .max(500)
      .optional(),
    // A voice's id, or '' to clear it.
    voiceId: z
      .string()
      .regex(/^([a-f0-9]{32})?$/)
      .optional(),
    tags: z.array(z.string().min(1).max(40)).max(32).optional(),
    addTags: z.array(z.string().min(1).max(40)).max(32).optional(),
    removeTags: z.array(z.string().min(1).max(40)).max(32).optional(),
    favorite: z.boolean().optional(),
    title: z.string().max(200).optional(),
    correctedText: z.string().max(4000).optional(),
    reviewed: z.boolean().optional(),
    auditId: z.number().int().positive().safe().optional(),
    query: z.string().max(200).optional(),
    offset: z.number().int().min(0).max(100000).optional(),
    limit: z.number().int().min(1).max(500).optional(),
    color: z
      .string()
      .regex(/^#[0-9a-f]{6}$/)
      .optional(),
    categories: z.array(z.string().min(1).max(256)).max(256).optional(),
    groupId: z.number().int().min(1).max(64).optional(),
    kind: z.enum(['all', 'clip', 'sound']).optional(),
    unassigned: z.boolean().optional(),
    tag: z.string().max(40).optional(),
    groupSpeakers: z.boolean().optional(),
    guestLibraryEdit: z.boolean().optional(),
    assetId: z.uuid().optional(),
    variantId: z.uuid().optional(),
    orientation: z.enum(['normal', 'reversed']).optional(),
    destinationDirectory: z.string().max(1024).optional(),
    categoryId: z.number().int().nonnegative().optional(),
    categoryName: z.string().max(256).optional(),
    confirmed: z.boolean().optional(),
    audienceId: z.uuid().optional(),
    ownerId: z.string().max(128).optional(),
    // revokePeer (2.39): the participant the host disconnected or turned away; their history join time ends.
    guestId: z.string().min(1).max(128).optional(),
    // beginSession (2.39): a new invitation (Replace invite), so guest join times and shares start over.
    newInvite: z.boolean().optional(),
    // Drafts and Keep (2.40): the draft a command is about; each guest's Temp and kept allowance (MB),
    // and whether kept audio counts per session or in total.
    draftId: z
      .string()
      .regex(/^[a-f0-9]{32}$/)
      .optional(),
    tempMb: z.number().int().min(1).max(4096).optional(),
    keptMb: z.number().int().min(1).max(65536).optional(),
    budgetMode: z.enum(['session', 'total']).optional(),
    guestKeep: z.boolean().optional(),
    sourceId: z.string().max(512).optional(),
    mimicEnd: z.number().int().nonnegative().safe().optional(),
    // Recording history (2.39): frames on the history timeline, waveform bins, the history length, the
    // subtitles setting, and a transcript correction.
    startFrame: z.number().int().nonnegative().safe().optional(),
    endFrame: z.number().int().nonnegative().safe().optional(),
    bins: z.number().int().min(1).max(2048).optional(),
    seconds: z.number().int().min(60).max(3600).optional(),
    subtitles: z.enum(['gaming', 'balanced', 'fast']).optional(),
    wordId: z.number().int().positive().safe().optional(),
    text: z.string().max(100).optional(),
    name: z.string().max(100).optional(),
    speed: z.number().min(0.05).max(20).optional(),
    keepPitch: z.boolean().optional(),
    soundId: z.string().max(64).optional(),
    libraryVersion: z.string().max(64).optional(),
    delta: z.union([z.literal(-1), z.literal(1)]).optional(),
    image: z.string().max(180000).optional(),
    positionMs: z.number().int().min(0).max(1800000).optional(),
    endMs: z.number().int().min(0).max(1800000).optional(),
    epoch: z.uuid().optional(),
    repeat: z.boolean().optional(),
    // With trimAudio, mimicClip or mimicLibraryImport (2.35): play the new clip when it is ready, but
    // only if nothing newer was asked for meanwhile. The companion decides, in arrival order.
    autoplay: z.boolean().optional(),
    action: z
      .enum([
        'status',
        'enable',
        'disable',
        'toggle',
        'next',
        'previous',
        'download',
        'none',
        'chipmunk',
        'slowmo',
        'echo',
        'speed',
        'length',
        'key',
        'stopIfPlaying',
        // 2.40: Quick Mimic (draftCreate), Keep and close a draft, a budget request, Temp after a crash.
        'phrase',
        'recent',
        'close',
        'allow',
        'deny',
        'recover',
        'discard',
        // 2.41: draftKeep / mimicLibraryImport into the voice library; voices and wanted words; the index.
        'library',
        'create',
        'update',
        'delete',
        'add',
        'remove',
        'pause',
        'resume',
      ])
      .optional(),
    permissions: z
      .object({
        remotePlayback: z.boolean(),
        guestCounter: z.boolean(),
        guestSeeking: z.boolean().optional(),
        guestSpotify: z.boolean().optional(),
        guestUploads: z.boolean().optional(),
        guestTransforms: z.boolean().optional(),
        guestMimic: z.boolean().optional(),
        guestKeep: z.boolean().optional(),
        guestLibraryEdit: z.boolean().optional(),
      })
      .strict()
      .optional(),
  })
  .strict();
export type Command = z.infer<typeof commandSchema>;
export type Sound = {
  id: string;
  name: string;
  icon: string | null;
  durationMs?: number | null;
  nativePlays?: number | null;
  nativeLastPlayed?: string | null;
  assetId?: string | null;
  orientation?: string | null;
  audioKind?: string | null;
  internalVariant?: boolean;
  aliasOf?: string | null;
  workspaceId?: string | null;
  aliases?: string[] | null;
  temporary?: boolean;
  uploadedBy?: string | null;
};
export type Category = {
  id: string;
  name: string;
  icon: string | null;
  soundIds: string[];
  children: Category[];
};
const image = z
  .string()
  .max(180000)
  .regex(/^data:image\/(png|jpeg);base64,[A-Za-z0-9+/=]+$/)
  .nullable();
const category: z.ZodType<Category> = z.object({
  id: z.string().max(64),
  name: z.string().max(4096),
  icon: image,
  soundIds: z.array(z.string().max(64)).max(50000),
  children: z.lazy(() => z.array(category).max(5000)),
});
export const snapshotSchema = z.object({
  v: z.literal(1),
  revision: z.number().int().nonnegative(),
  available: z.boolean(),
  error: z.string().max(2000).nullable(),
  protocol: z.number().int().optional(),
  companionVersion: z.string().max(40).nullable().optional(),
  hostId: z
    .string()
    .regex(/^[a-f0-9]{64}$/)
    .nullable()
    .optional(),
  managedAudioVersion: z.number().int().nonnegative().optional(),
  statisticsVersion: z.number().int().nonnegative().optional(),
  library: z.object({
    version: z.string().max(64),
    sounds: z
      .array(
        z.object({
          id: z.string().max(64),
          name: z.string().max(4096),
          icon: image,
          durationMs: z.number().nonnegative().nullable().optional(),
          nativePlays: z.number().int().nonnegative().nullable().optional(),
          nativeLastPlayed: z.string().nullable().optional(),
          assetId: z.string().nullable().optional(),
          orientation: z.string().nullable().optional(),
          audioKind: z.string().nullable().optional(),
          internalVariant: z.boolean().optional(),
          aliasOf: z.string().nullable().optional(),
          workspaceId: z.string().nullable().optional(),
          aliases: z.array(z.string().max(64)).max(64).nullable().optional(),
          temporary: z.boolean().optional(),
          uploadedBy: z.string().max(40).nullable().optional(),
        }),
      )
      .max(50000),
    categories: z.array(category).max(5000),
  }),
  playback: z.object({
    status: z.enum(['UNKNOWN', 'STOPPED', 'PLAYING', 'PAUSED', 'SEEKING']),
    positionMs: z.number().nullable(),
    durationMs: z.number().nullable(),
    soundId: z.string().max(64).nullable().optional(),
    epoch: z.uuid().nullable().optional(),
    requester: z.string().max(40).nullable().optional(),
    sampledAt: z.number().optional(),
    identitySource: z.enum(['unknown', 'web']).optional(),
    regionEndMs: z.number().nullable().optional(),
    repeat: z.boolean().optional(),
    positionRevision: z.number().int().nonnegative().optional(),
  }),
  counter: z.number().int().nonnegative(),
  permissions: z.object({
    remotePlayback: z.boolean(),
    guestCounter: z.boolean(),
    guestSeeking: z.boolean().optional(),
    guestSpotify: z.boolean().optional(),
    guestUploads: z.boolean().optional(),
    guestTransforms: z.boolean().optional(),
    guestMimic: z.boolean().optional(),
    // 2.40: guests may Keep clips into the host's chosen destination. Older companions omit it.
    guestKeep: z.boolean().optional(),
    // 2.41: guests may name and tag voice-library recordings (with Undo and audit). Older companions omit it.
    guestLibraryEdit: z.boolean().optional(),
  }),
  // 2.41: rises when the voice library, its word index, notices or speaker groups change (fetch libraryStatus).
  voiceLibrary: z.number().int().nonnegative().optional(),
  // 2.40: these rise when drafts or guest budgets change, so pages fetch draftList or budgetStatus;
  // tempRecovery counts the clips a crashed companion left in Temp until the host decides.
  drafts: z.number().int().nonnegative().optional(),
  budgets: z.number().int().nonnegative().optional(),
  tempRecovery: z.number().int().positive().nullish(),
  // True after the host's Panic until they resume guest playback (2.35). Older companions omit it.
  playbackSuspended: z.boolean().nullish(),
  mimic: z
    .object({
      active: z.boolean(),
      source: z.string().max(512).nullable(),
      status: z.string().max(600),
      windowMs: z.number().int().positive(),
      transcribing: z.boolean().optional(),
      transcriptModel: z.boolean().optional(),
      transcriptStatus: z.string().max(600).optional(),
      hotkey: z.boolean().optional(),
      hotkeyStatus: z.string().max(600).optional(),
      hotkeySeconds: z.number().int().positive().max(60).optional(),
      hotkeyKey: z.string().max(8).optional(),
      recent: z
        .array(
          z.object({
            assetId: z.string().max(64),
            name: z.string().max(200),
            by: z.string().max(80),
            at: z.number(),
          }),
        )
        .max(20)
        .nullish(),
      // The rolling recording history (2.39). Older companions omit it.
      history: z
        .object({
          sessionId: z.string().max(64).nullable(),
          seconds: z.number().int().positive().max(3600),
          subtitles: z.enum(['gaming', 'balanced', 'fast']),
          sourceLost: z.boolean(),
          recoveredMs: z.number().int().nonnegative(),
          problem: z.string().max(600).nullish(),
        })
        .nullish(),
    })
    .nullable()
    .optional(),
});
export type Snapshot = z.infer<typeof snapshotSchema>;
const lightSnapshotSchema = snapshotSchema.omit({ library: true });
export function parseSnapshot(raw: unknown, library?: Snapshot['library']): Snapshot {
  return library ? { ...lightSnapshotSchema.parse(raw), library } : snapshotSchema.parse(raw);
}
/**
 * Why a command could not be sent or answered: the companion or the host is not connected (2.35). The
 * page already shows the connection state, so playback clicks that fail this way add no notice.
 */
export class Disconnected extends Error {}
export type Ack = { type: 'ack'; id: string; ok: boolean; error?: string };
export const ackSchema = z.object({
  type: z.literal('ack'),
  id: z.uuid(),
  ok: z.boolean(),
  error: z.string().max(2000).nullable().optional(),
  data: z.unknown().optional(),
});
export const EMPTY: Snapshot = {
  v: 1,
  revision: 0,
  available: false,
  error: null,
  library: { version: 'empty', sounds: [], categories: [] },
  playback: { status: 'UNKNOWN', positionMs: null, durationMs: null },
  counter: 0,
  permissions: { remotePlayback: true, guestCounter: false },
};
export function command(op: Command['op'], fields: Partial<Command> = {}): Command {
  return { ...fields, v: 2, id: crypto.randomUUID(), at: Date.now(), op };
}
/** Commands that may take a while on the companion (audio preparation, capture start). */
export const slowOps: readonly Command['op'][] = [
  'reverse',
  'audioPlay',
  'keepAudio',
  'removeAudio',
  'retryAudio',
  'retryCleanup',
  'cleanupCategories',
  'clearTemp',
  'endSession',
  'mimicStart',
  'mimicClip',
  'mimicLibraryImport',
  'trimAudio',
  'trimPreview',
  'mimicPreview',
  'draftKeep',
  'draftPlay',
  'tempRecover',
  'libraryPreview',
  'libraryAdd',
];
/**
 * Playback and transport commands (2.35). They are answered quickly and a burst of them is normal use
 * (two people alternating clicks), so they get their own rate budget in the host tab and are never
 * held back by the cap on commands still waiting for an answer.
 */
export const controlOps: readonly Command['op'][] = [
  'play',
  'startAt',
  'region',
  'stop',
  'seek',
  'pause',
  'audioPlay',
  'reverse',
  'panic',
];
export function isGuestAllowed(c: Command, s: Snapshot) {
  // "Make it and play it" (2.35) needs the same permission as playing it afterwards did.
  if (c.autoplay && !s.permissions.remotePlayback) return false;
  if (c.op === 'mimicPeek' || c.op === 'mimicClip') return s.permissions.guestMimic === true;
  // The recording history (2.39): only what a guest may see of it, which the companion limits to
  // what was recorded since they joined (and what the host shared).
  if (c.op === 'historyTimeline' || c.op === 'historyLevels' || c.op === 'historyTranscript')
    return s.permissions.guestMimic === true;
  // Drafts and private preview (2.40): their own, scoped by the companion to what they may see.
  // Play into game needs playback as well, like Mimic & play.
  if (c.op === 'draftPlay')
    return s.permissions.guestMimic === true && s.permissions.remotePlayback;
  // Into the voice library from a draft (2.41): the same as keeping it.
  if (c.op === 'draftKeep' && c.action === 'library')
    return s.permissions.guestMimic === true && s.permissions.guestKeep === true;
  if (c.op.startsWith('draft') || c.op === 'mimicPreview') return s.permissions.guestMimic === true;
  // Keep (2.40): into the host's destination, within their budget; never a path of their own.
  if (c.op === 'keepAudio')
    return (
      s.permissions.guestKeep === true &&
      c.destinationDirectory === undefined &&
      c.categoryId === undefined &&
      c.categoryName === undefined
    );
  if (c.op === 'budgetStatus') return true;
  // The voice library (2.41): guests see and search the kept library by default; private previews
  // are answered to them only. Editing (names, tags, voices, wanted words, Undo) needs the host's
  // guestLibraryEdit; the settings and the category word index are the host's.
  if (
    c.op === 'libraryStatus' ||
    c.op === 'libraryList' ||
    c.op === 'libraryGet' ||
    c.op === 'librarySearch' ||
    c.op === 'libraryVocabulary' ||
    c.op === 'libraryAudit' ||
    c.op === 'libraryPreview'
  )
    return true;
  if (
    c.op === 'libraryEdit' ||
    c.op === 'libraryRemove' ||
    c.op === 'libraryUndo' ||
    c.op === 'libraryVoice' ||
    c.op === 'libraryWanted' ||
    c.op === 'libraryAdd' ||
    c.op === 'libraryRetry'
  )
    return s.permissions.guestLibraryEdit !== false;
  if (c.op === 'historySearch' || c.op === 'speakerGroups')
    return s.permissions.guestMimic === true;
  if (c.op === 'speakerName' || c.op === 'speakerLink')
    return s.permissions.guestMimic === true && s.permissions.guestLibraryEdit !== false;
  if (c.op === 'trimAudio') return s.permissions.guestUploads !== false;
  // Undo of a guest's own clip or copy (2.32): the companion checks the guest added it.
  if (c.op === 'removeAudio') return s.permissions.guestUploads !== false;
  if (c.op === 'spotify')
    return (
      c.action !== 'enable' &&
      c.action !== 'disable' &&
      (c.action === 'status' || c.action === undefined || s.permissions.guestSpotify !== false)
    );
  if (c.op === 'reverse')
    return s.permissions.remotePlayback && s.permissions.guestTransforms !== false;
  if (c.op === 'audioPlay') return s.permissions.remotePlayback;
  if (c.op === 'audioAssets') return true;
  if (c.op === 'waveform' || c.op === 'statistics') return true;
  if (['seek', 'pause', 'startAt', 'region'].includes(c.op))
    return s.permissions.remotePlayback && s.permissions.guestSeeking !== false;
  return c.op === 'play' || c.op === 'stop'
    ? s.permissions.remotePlayback
    : c.op === 'counter' && s.permissions.guestCounter;
}
export const participantSchema = z
  .object({
    id: z.uuid(),
    name: z
      .string()
      .min(1)
      .max(40)
      .regex(/^[^\p{Cc}\p{Cf}]+$/u),
  })
  .strict();
export type Participant = z.infer<typeof participantSchema>;
export function cleanName(value: string) {
  return (
    Array.from(value.replace(/[\p{Cc}\p{Cf}]/gu, '').trim())
      .slice(0, 40)
      .join('') || 'Guest'
  );
}
export const waveformSchema = z
  .object({
    soundId: z.string().max(64),
    version: z.string().max(64),
    status: z.enum(['ready', 'loading', 'unavailable']),
    durationMs: z.number().int().positive().max(1800000).nullable().optional(),
    analyzedMs: z.number().int().nonnegative().max(1800000).nullable().optional(),
    error: z.string().max(2000).nullable().optional(),
    levels: z
      .array(
        z
          .object({
            bins: z.number().int().min(1).max(32768),
            peaks: z.array(z.number().int().min(-32767).max(32767)).max(65536),
          })
          .refine((l) => l.peaks.length === l.bins * 2),
      )
      .length(3)
      .nullable()
      .optional(),
  })
  .refine((x) => x.status !== 'ready' || (x.durationMs && x.levels));
export type Waveform = z.infer<typeof waveformSchema>;
export const statisticsSchema = z.object({
  statistics: z.object({
    total: z.number().int().nonnegative(),
    lastPlayedAt: z.number().nullable(),
    participants: z.record(
      z.string(),
      z.object({ name: z.string().max(40), count: z.number().int().nonnegative() }),
    ),
  }),
  recent: z
    .array(
      z.object({
        commandId: z.string(),
        soundId: z.string(),
        at: z.number(),
        participantId: z.string(),
        name: z.string().max(40),
        source: z.enum(['host', 'guest']),
      }),
    )
    .max(100),
});
export class RateLimit {
  private times: number[] = [];
  constructor(
    private limit = 10,
    private interval = 1000,
  ) {}
  accept(now = Date.now()) {
    this.times = this.times.filter((t) => now - t < this.interval);
    if (this.times.length >= this.limit) return false;
    this.times.push(now);
    return true;
  }
}
export function flattenCategories(
  cats: Category[],
  depth = 0,
): { category: Category; depth: number }[] {
  return cats.flatMap((category) => [
    { category, depth },
    ...flattenCategories(category.children, depth + 1),
  ]);
}
export function selectSounds(s: Snapshot, categoryId: string, search: string) {
  const query = search.trim().toLocaleLowerCase();
  const ids =
    categoryId === 'all' || query
      ? undefined
      : flattenCategories(s.library.categories).find((c) => c.category.id === categoryId)?.category
          .soundIds;
  const byId = new Map(s.library.sounds.map((sound) => [sound.id, sound]));
  return (ids ? ids.map((id) => byId.get(id)).filter((x): x is Sound => !!x) : s.library.sounds)
    .filter((sound) => !sound.internalVariant)
    .filter((sound) => !query || sound.name.toLocaleLowerCase().includes(query));
}

export const soundKeys = (sound: Sound) => [
  ...new Set([sound.workspaceId ?? sound.id, sound.id, ...(sound.aliases ?? [])]),
];
export const hasSound = (ids: string[], sound: Sound) =>
  soundKeys(sound).some((id) => ids.includes(id));
