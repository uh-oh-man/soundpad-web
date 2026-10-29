import { z } from 'zod';
export const commandSchema = z
  .object({
    v: z.literal(1),
    id: z.uuid(),
    at: z.number().int().safe(),
    op: z.enum(['play', 'stop', 'counter', 'reset', 'refresh', 'permissions', 'icon']),
    soundId: z.string().max(64).optional(),
    libraryVersion: z.string().max(64).optional(),
    delta: z.union([z.literal(-1), z.literal(1)]).optional(),
    image: z.string().max(180000).optional(),
    permissions: z
      .object({ remotePlayback: z.boolean(), guestCounter: z.boolean() })
      .strict()
      .optional(),
  })
  .strict();
export type Command = z.infer<typeof commandSchema>;
export type Sound = { id: string; name: string; icon: string | null };
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
  library: z.object({
    version: z.string().max(64),
    sounds: z
      .array(z.object({ id: z.string().max(64), name: z.string().max(4096), icon: image }))
      .max(50000),
    categories: z.array(category).max(5000),
  }),
  playback: z.object({
    status: z.enum(['UNKNOWN', 'STOPPED', 'PLAYING', 'PAUSED', 'SEEKING']),
    positionMs: z.number().nullable(),
    durationMs: z.number().nullable(),
  }),
  counter: z.number().int().nonnegative(),
  permissions: z.object({ remotePlayback: z.boolean(), guestCounter: z.boolean() }),
});
export type Snapshot = z.infer<typeof snapshotSchema>;
export type Ack = { type: 'ack'; id: string; ok: boolean; error?: string };
export const ackSchema = z.object({
  type: z.literal('ack'),
  id: z.uuid(),
  ok: z.boolean(),
  error: z.string().max(2000).nullable().optional(),
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
  return { ...fields, v: 1, id: crypto.randomUUID(), at: Date.now(), op };
}
export function isGuestAllowed(c: Command, s: Snapshot) {
  return c.op === 'play' || c.op === 'stop'
    ? s.permissions.remotePlayback
    : c.op === 'counter' && s.permissions.guestCounter;
}
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
