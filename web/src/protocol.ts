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
    ]),
    soundId: z.string().max(64).optional(),
    libraryVersion: z.string().max(64).optional(),
    delta: z.union([z.literal(-1), z.literal(1)]).optional(),
    image: z.string().max(180000).optional(),
    positionMs: z.number().int().min(0).max(1800000).optional(),
    endMs: z.number().int().min(0).max(1800000).optional(),
    epoch: z.uuid().optional(),
    repeat: z.boolean().optional(),
    action: z.enum(['status', 'enable', 'disable', 'toggle', 'next', 'previous']).optional(),
    permissions: z
      .object({
        remotePlayback: z.boolean(),
        guestCounter: z.boolean(),
        guestSeeking: z.boolean().optional(),
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
  }),
});
export type Snapshot = z.infer<typeof snapshotSchema>;
const lightSnapshotSchema = snapshotSchema.omit({ library: true });
export function parseSnapshot(raw: unknown, library?: Snapshot['library']): Snapshot {
  return library ? { ...lightSnapshotSchema.parse(raw), library } : snapshotSchema.parse(raw);
}
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
export function isGuestAllowed(c: Command, s: Snapshot) {
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
    status: z.enum(['ready', 'unavailable']),
    durationMs: z.number().int().positive().max(1800000).nullable().optional(),
    error: z.string().max(2000).nullable().optional(),
    levels: z
      .array(
        z
          .object({
            bins: z.number().int().min(1).max(8192),
            peaks: z.array(z.number().int().min(-32767).max(32767)).max(16384),
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
  return (
    ids ? ids.map((id) => byId.get(id)).filter((x): x is Sound => !!x) : s.library.sounds
  ).filter((sound) => !query || sound.name.toLocaleLowerCase().includes(query));
}
