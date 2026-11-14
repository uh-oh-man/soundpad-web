/**
 * The Mimic history window's fetch loop (2.41). Dragging the overview used to restart the whole polling effect on
 * every pointer move: each restart sent a new timeline, levels and transcript request while the old ones were
 * still on their way (their answers were dropped, but the companion did the work). Here one loop runs passes one
 * at a time, so at most one request of each kind is ever in flight:
 *
 * - `nudge()` (the view moved) asks for a pass now. During a pass it only notes that another is due, and that
 *   pass reads the newest wanted view when it starts, so a whole drag costs a few passes, not one per move.
 * - Nudged passes come at most every `minGapMs`, so a drag cannot use up the request budget that Play and Stop
 *   share with these reads on a guest's way through the host page.
 * - `stop()` ends it; a pass already running finishes, and its caller checks `alive()` before using a reply.
 */
export type HistoryPass = (info: {
  tick: number;
  nudged: boolean;
  alive: () => boolean;
}) => Promise<void>;

export type Timers = {
  setTimeout: (run: () => void, ms: number) => unknown;
  clearTimeout: (handle: unknown) => void;
  now: () => number;
};

const realTimers: Timers = {
  setTimeout: (run, ms) => setTimeout(run, ms),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  now: () => Date.now(),
};

export function historyPoller(
  pass: HistoryPass,
  {
    intervalMs,
    minGapMs = 120,
    timers = realTimers,
  }: { intervalMs: number; minGapMs?: number; timers?: Timers },
) {
  let running = false;
  /** A pass asked for while one ran: 'view' (the view moved) or 'full' (also the timeline and the overview). */
  let due: 'view' | 'full' | null = null;
  let stopped = false;
  let tick = 0;
  let lastStart = -Infinity;
  let timer: unknown = undefined;
  let waitingFull = false;
  const alive = () => !stopped;
  const schedule = (ms: number, nudged: boolean) => {
    timers.clearTimeout(timer);
    timer = timers.setTimeout(() => void run(nudged), Math.max(0, ms));
  };
  function ask(kind: 'view' | 'full') {
    if (stopped) return;
    if (running) due = due === 'full' || kind === 'full' ? 'full' : 'view';
    else {
      // A full pass already waiting stays full.
      waitingFull ||= kind === 'full';
      schedule(lastStart + minGapMs - timers.now(), !waitingFull);
    }
  }
  async function run(nudged: boolean) {
    if (stopped) return;
    if (running) {
      due = due === 'full' || !nudged ? 'full' : 'view';
      return;
    }
    timers.clearTimeout(timer);
    running = true;
    due = null;
    waitingFull = false;
    lastStart = timers.now();
    try {
      await pass({ tick, nudged, alive });
    } catch {
      /* Keep what is shown; the next pass retries. */
    }
    if (!nudged) tick++;
    running = false;
    if (stopped) return;
    if (due) schedule(lastStart + minGapMs - timers.now(), due === 'view');
    else schedule(intervalMs, false);
  }
  return {
    start: () => void run(false),
    /** The wanted view changed: fetch for it soon, without a second request of any kind in flight. */
    nudge: () => ask('view'),
    /** Something the timeline shows changed (a share): a full pass soon. */
    refresh: () => ask('full'),
    stop: () => {
      stopped = true;
      timers.clearTimeout(timer);
    },
    /** For tests: whether a pass is running now. */
    busy: () => running,
  };
}
