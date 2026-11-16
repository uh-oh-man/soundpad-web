import { z } from 'zod';
import { restoreNoticeHistory, type Notice } from './notifications';

/**
 * The host's Activity kept in the companion (2.45, gate diagnostics, D07). Activity used to live in each page's
 * memory, so a reload lost it. Now every host page sends what it lists (notices with their technical details,
 * guests joining, leaving and waiting) to the companion, which keeps the newest 200 until it quits, and a page
 * that loads asks for them back. Guests and the example never send anything; nothing is written to disk.
 */
export type ActivitySource = 'notice' | 'join' | 'leave' | 'lobby';
export type ActivityEntry = {
  id: string;
  kind: 'info' | 'success' | 'error';
  source: ActivitySource;
  text: string;
  at: number;
  details?: string;
};
/** The companion's limits (BridgeDiagnostics.cs): longer texts would get the whole batch refused. */
export const ACTIVITY_LIMITS = { batch: 20, text: 500, details: 4000, kept: 200 } as const;
const entrySchema = z.object({
  id: z.string().min(1).max(64),
  kind: z.enum(['info', 'success', 'error']),
  source: z.enum(['notice', 'join', 'leave', 'lobby']),
  text: z.string().min(1).max(ACTIVITY_LIMITS.text),
  at: z.number(),
  details: z.string().max(ACTIVITY_LIMITS.details).nullable().optional(),
});
export const activityListSchema = z.object({
  entries: z.array(entrySchema).max(ACTIVITY_LIMITS.kept),
});
export type KeptEntry = z.infer<typeof entrySchema>;

const clip = (text: string, max: number) =>
  text.length > max ? text.slice(0, max - 1) + '…' : text;
/** A notice as the companion keeps it: one entry per time it was shown (the same words twice are two lines). */
export const noticeEntry = (notice: Notice, at = Date.now()): ActivityEntry => ({
  id: `${notice.id}-${at}`.slice(0, 64),
  kind: notice.kind,
  source: 'notice',
  text: clip(notice.message, ACTIVITY_LIMITS.text),
  at,
  ...(notice.details && { details: clip(notice.details, ACTIVITY_LIMITS.details) }),
});

/** A batch lost on the way is sent again after this long. */
const RETRY_MS = 5000;
type Send = (op: 'activityAdd' | 'activityList', body: unknown) => Promise<unknown>;
type Timers = { set: (run: () => void, ms: number) => unknown; clear: (handle: unknown) => void };
const realTimers: Timers = {
  set: (run, ms) => setTimeout(run, ms),
  clear: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

/**
 * One page's sync. `arm(true)` once the page is a host page (not the example): from then on its entries are
 * queued, at most the companion's 200. `connect(true)` once the companion answers and supports diagnostics:
 * Activity is asked for once per connection, then the queue goes out in batches of 20, one request at a time.
 * A batch that fails on the way stays queued for the next connection; one the companion refuses is dropped.
 */
export function createActivitySync({
  send,
  restoreShared,
  timers = realTimers,
  delayMs = 300,
}: {
  send: Send;
  /** Guests joining, leaving and waiting that the companion kept, for the Shared activity list. */
  restoreShared: (entries: KeptEntry[]) => void;
  timers?: Timers;
  delayMs?: number;
}) {
  let armed = false;
  let connected = false;
  let restored = false;
  let busy = false;
  let timer: unknown;
  let queue: ActivityEntry[] = [];
  /** Ids this page sent: they are its own lines when Activity comes back. */
  const sent = new Set<string>();
  const schedule = (ms = delayMs) => {
    if (!connected || busy || !queue.length) return;
    timers.clear(timer);
    timer = timers.set(() => void flush(), ms);
  };
  async function restore() {
    restored = true;
    try {
      const { entries } = activityListSchema.parse(await send('activityList', {}));
      const mine = entries.filter((e) => !sent.has(e.id));
      restoreNoticeHistory(mine.filter((e) => e.source === 'notice'));
      restoreShared(mine.filter((e) => e.source !== 'notice'));
    } catch {
      /* Activity from before the reload is a convenience; this page's own still shows. */
    }
  }
  async function flush() {
    if (!connected || busy || !queue.length) return;
    busy = true;
    const batch = queue.slice(0, ACTIVITY_LIMITS.batch);
    let wait = delayMs;
    try {
      await send('activityAdd', { entries: batch });
      for (const e of batch) sent.add(e.id);
      queue = queue.filter((e) => !batch.includes(e));
    } catch (e) {
      // Lost on the way: tried again a little later. Refused: those lines would be refused again.
      if (e instanceof Error && /disconnect|unavailable|timed out/i.test(e.message))
        wait = RETRY_MS;
      else queue = queue.filter((x) => !batch.includes(x));
    } finally {
      busy = false;
      schedule(wait);
    }
  }
  return {
    arm(on: boolean) {
      armed = on;
      if (!on) {
        queue = [];
        connected = false;
        restored = false;
      }
    },
    /** The companion answers and supports diagnostics (true), or went away or lacks them (false). */
    async connect(on: boolean) {
      if (on === connected) return;
      connected = on && armed;
      if (!connected) {
        restored = false;
        return;
      }
      if (!restored) await restore();
      schedule();
    },
    push(entry: ActivityEntry) {
      if (!armed) return;
      queue = [...queue, entry].slice(-ACTIVITY_LIMITS.kept);
      schedule();
    },
    /** For tests. */
    state: () => ({ armed, connected, queued: queue.length, sent: sent.size, busy }),
    flush,
  };
}
