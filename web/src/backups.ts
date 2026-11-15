import { z } from 'zod';
import { controller } from './controller';
import { parseWorkspace, type Workspace } from './workspace';

/**
 * Backups and restore (Stage H, 2.44, gate backups). Host only: the companion refuses these ops for
 * guests and relayed commands, and the snapshot's `backups` revision never reaches a guest. Each op's
 * body is checked here before it is sent, and strictly again by the companion (BridgeBackups.cs).
 * The page never builds a host path: "Use suggested folder" asks the companion for its own.
 */

const snapshotId = z.string().regex(/^\d{8}T\d{6}Z-[a-f0-9]{8}$/);
export const restoreKinds = ['clip', 'recording', 'voice', 'sentence', 'library'] as const;
export type RestoreKind = (typeof restoreKinds)[number];
export type LayoutSource = 'window' | 'browser';

export const bodies = {
  backupSettings: z.discriminatedUnion('action', [
    z.object({ action: z.literal('get') }).strict(),
    z
      .object({
        action: z.literal('set'),
        folder: z.string().max(1024).optional(),
        useSuggested: z.literal(true).optional(),
        keepCount: z.number().int().min(1).max(200).optional(),
        keepDays: z.number().int().min(1).max(3650).optional(),
        intervalHours: z.number().int().min(1).max(168).optional(),
        afterChangeMinutes: z.number().int().min(0).max(1440).optional(),
        includeNativeFiles: z.boolean().optional(),
      })
      .strict(),
  ]),
  backupNow: z.object({}).strict(),
  backupList: z
    .object({ limit: z.number().int().min(1).max(100).optional(), before: snapshotId.optional() })
    .strict(),
  backupDetail: z.object({ snapshot: snapshotId }).strict(),
  backupRestore: z.union([
    z
      .object({
        snapshot: snapshotId,
        kind: z.enum(restoreKinds),
        ids: z
          .array(z.string().regex(/^[a-fA-F0-9-]{32,36}$/))
          .max(500)
          .optional(),
        mode: z.enum(['skip', 'replace']).optional(),
        adoptIdentity: z.boolean().optional(),
      })
      .strict(),
    z
      .object({
        kind: z.literal('sentence'),
        undo: z.string().regex(/^[a-f0-9]{32}$/),
      })
      .strict(),
  ]),
  backupAttach: z
    .object({
      source: z.enum(['window', 'browser']),
      workspace: z.record(z.string(), z.json()),
    })
    .strict(),
  backupLayout: z.object({ snapshot: snapshotId, source: z.enum(['window', 'browser']) }).strict(),
} as const;
export type BackupOp = keyof typeof bodies;

const lastResult = z.object({
  atUtcMs: z.number(),
  ok: z.boolean(),
  bytes: z.number(),
  snapshots: z.number(),
  message: z.string(),
});
export const statusSchema = z.object({
  settings: z.object({
    folder: z.string().nullable(),
    keepCount: z.number(),
    keepDays: z.number(),
    intervalHours: z.number(),
    afterChangeMinutes: z.number(),
    includeNativeFiles: z.boolean(),
    suggested: z.string(),
    nativeBytes: z.number(),
    problem: z.string().nullable(),
  }),
  status: z.object({
    running: z.boolean(),
    lastResult: lastResult.nullable(),
    totalBytes: z.number(),
    snapshots: z.number(),
    freeBytes: z.number().nullable(),
  }),
});
export type BackupStatus = z.infer<typeof statusSchema>;
export const listSchema = z.object({
  snapshots: z.array(
    z.object({
      id: snapshotId,
      kind: z.string(),
      createdUtcMs: z.number(),
      bytes: z.number(),
      fuzzy: z.boolean(),
      warnings: z.array(z.string()),
      other: z.boolean().optional(),
    }),
  ),
});
export type BackupEntry = z.infer<typeof listSchema>['snapshots'][number];
const item = z.object({ id: z.string(), name: z.string(), status: z.string() });
export const detailSchema = z.object({
  snapshot: snapshotId,
  kind: z.string(),
  createdUtcMs: z.number(),
  fuzzy: z.boolean(),
  warnings: z.array(z.string()),
  clips: z.array(item.extend({ file: z.string(), path: z.string() })),
  recordings: z.array(item),
  voices: z.array(item),
  sentences: z.array(item),
  layouts: z.array(item),
  library: z.object({
    recordings: z.number(),
    voices: z.number(),
    sentences: z.number(),
    clips: z.number(),
    icons: z.number(),
    sounds: z.number(),
    counter: z.number().nullable(),
    identity: z.enum(['same', 'different']),
  }),
});
export type BackupDetail = z.infer<typeof detailSchema>;
export const restoreSchema = z.object({
  restored: z.number(),
  skipped: z.number(),
  conflicts: z.array(z.string()),
  attention: z.array(z.object({ file: z.string(), path: z.string(), message: z.string() })),
  warnings: z.array(z.string()),
  auditGroup: z.number().nullable().optional(),
  undoToken: z.string().nullable().optional(),
  undone: z.number().optional(),
});
export type RestoreResult = z.infer<typeof restoreSchema>;
export const layoutSchema = z.object({ workspace: z.unknown(), savedUtcMs: z.number() });

/** Sends a backup op with its body checked first (a page bug is caught here, not by the companion). */
export async function backupCommand<T>(
  op: BackupOp,
  body: z.input<(typeof bodies)[BackupOp]>,
  reply: z.ZodType<T>,
  options: { quiet?: boolean } = {},
): Promise<T> {
  const checked = bodies[op].parse(body);
  return reply.parse(await controller.run(op, { body: checked }, options));
}

// ---- Wording ------------------------------------------------------------------------------------

export function formatBytes(bytes: number) {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${bytes} bytes`;
}

const two = (n: number) => String(n).padStart(2, '0');
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
/** "14:02" today, "13 Nov 14:02" another day (local time). */
export function when(ms: number, now = Date.now()) {
  const d = new Date(ms);
  const today = new Date(now);
  const time = `${two(d.getHours())}:${two(d.getMinutes())}`;
  return d.toDateString() === today.toDateString()
    ? time
    : `${d.getDate()} ${MONTHS[d.getMonth()]} ${time}`;
}

/** The status line under Backups: what happened last, in plain words. */
export function statusLine(s: BackupStatus, now = Date.now()) {
  if (s.settings.problem) return 'Backups are off: the settings file could not be read.';
  if (!s.settings.folder) return 'Backups are off. Choose a folder to turn them on.';
  if (s.status.running) return 'Backing up…';
  const last = s.status.lastResult;
  if (!last) return 'No backup yet.';
  if (!last.ok)
    return `The last backup (${when(last.atUtcMs, now)}) did not finish: ${last.message}`;
  const count = `${last.snapshots} backup${last.snapshots === 1 ? '' : 's'}`;
  const free = s.status.freeBytes === null ? '' : `, ${formatBytes(s.status.freeBytes)} free`;
  return `Last backup ${when(last.atUtcMs, now)}, ${formatBytes(last.bytes)} in ${count}${free}`;
}

const KIND_LABELS: Record<string, string> = {
  scheduled: 'Scheduled',
  manual: 'Manual',
  'pre-restore': 'Before restore',
  'pre-update': 'Before update',
  'pre-organize': 'Before organizing',
  unreadable: 'Damaged',
};
export const kindLabel = (kind: string) => KIND_LABELS[kind] ?? 'Backup';

/** What a restore says when it is done (the toast; Undo is offered with it when it can be undone). */
export function restoreMessage(kind: RestoreKind, r: RestoreResult) {
  const noun: Record<RestoreKind, [string, string]> = {
    clip: ['kept clip', 'kept clips'],
    recording: ['recording', 'recordings'],
    voice: ['voice', 'voices'],
    sentence: ['sentence', 'sentences'],
    library: ['item', 'items'],
  };
  if (kind === 'library') return 'The library was restored from the backup.';
  const [one, many] = noun[kind];
  const parts = [
    r.restored > 0
      ? `Restored ${r.restored} ${r.restored === 1 ? one : many}.`
      : `Nothing to restore.`,
  ];
  if (r.skipped > 0) parts.push(`${r.skipped} already here.`);
  if (r.conflicts.length > 0) parts.push(`${r.conflicts.length} left as they are.`);
  return parts.join(' ');
}

// ---- Layouts ------------------------------------------------------------------------------------

/** The most a backed-up layout may be (the companion refuses more, as parseWorkspace does). */
export const MAX_LAYOUT_BYTES = 2 * 1024 * 1024;
/** A layout is sent once it has not changed for this long (and once when the page loads). */
export const ATTACH_QUIET_MS = 30000;

/** Whose layout this page holds: the companion window's page, or a host browser's. */
export const layoutSource = (hosting: 'tab' | 'window' | 'view'): LayoutSource =>
  hosting === 'window' ? 'window' : 'browser';

/** The workspace as sent, or null when it is too large to back up. */
export function layoutPayload(workspace: Workspace | null | undefined) {
  if (!workspace) return null;
  const text = JSON.stringify(workspace);
  return new TextEncoder().encode(text).length > MAX_LAYOUT_BYTES ? null : text;
}

export type AttachState = { seen?: string; since: number; sent?: string };
/**
 * The debounced attach decision: send the layout once on load, then each time it has stayed the same
 * for 30 s and differs from what was last sent. A layout over 2 MiB is never sent.
 */
export function nextAttach(
  state: AttachState,
  current: string | null,
  now: number,
): { state: AttachState; send: boolean } {
  if (current === null) return { state, send: false };
  if (state.sent === undefined && state.seen === undefined)
    return { state: { seen: current, since: now, sent: current }, send: true };
  if (current !== state.seen)
    return { state: { ...state, seen: current, since: now }, send: false };
  if (current !== state.sent && now - state.since >= ATTACH_QUIET_MS)
    return { state: { ...state, sent: current }, send: true };
  return { state, send: false };
}

/**
 * A restored layout merged into the current one: the backup's folders that are missing, favourites
 * and hidden sounds added, order kept with the backup's missing sounds after it, and cues, regions and
 * zoom added where the current layout has none. Nothing of the current layout is removed.
 */
export function mergeLayouts(current: Workspace, restored: Workspace): Workspace {
  const ids = new Set(current.folders.map((f) => f.id));
  const union = (a: string[], b: string[]) => [...new Set([...a, ...b])];
  return {
    ...current,
    folders: [...current.folders, ...restored.folders.filter((f) => !ids.has(f.id))],
    favorites: union(current.favorites, restored.favorites),
    hidden: union(current.hidden, restored.hidden),
    order: union(current.order, restored.order),
    cues: { ...restored.cues, ...current.cues },
    regions: { ...restored.regions, ...current.regions },
    zoom: { ...restored.zoom, ...current.zoom },
  };
}

/** The page's live layout, so a restore can apply to it (BackupLayoutSync keeps it current). */
type LayoutTarget = { hostId: string; workspace: Workspace; replace: (w: Workspace) => void };
let target: LayoutTarget | null = null;
export function setLayoutTarget(next: LayoutTarget | null) {
  target = next;
}
export const layoutTarget = () => target;

/**
 * Applies a backed-up layout after the host confirmed it: checked like an import (parseWorkspace), then
 * replace or merge. Returns the previous layout, for Undo (kept in memory only).
 */
export function applyLayout(raw: unknown, mode: 'replace' | 'merge') {
  const t = target;
  if (!t) throw Error('Open the board first, then restore the layout.');
  const restored = parseWorkspace(JSON.stringify(raw), t.hostId);
  const previous = t.workspace;
  t.replace(mode === 'replace' ? restored : mergeLayouts(previous, restored));
  return previous;
}
