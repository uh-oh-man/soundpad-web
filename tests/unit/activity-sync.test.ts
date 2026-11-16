// 2.45 (D07): the host's Activity kept in the companion across a reload of the page.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ACTIVITY_LIMITS,
  createActivitySync,
  noticeEntry,
  type ActivityEntry,
  type KeptEntry,
} from '../../web/src/activity-sync';
import { noticeState, resetNotices, type Notice } from '../../web/src/notifications';

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2026-11-15T12:00:00Z'));
  resetNotices();
});
afterEach(() => vi.useRealTimers());

/** A companion that keeps entries like BridgeDiagnostics.cs (by id, newest first). */
function companion(kept: KeptEntry[] = []) {
  const calls: { op: string; body: unknown }[] = [];
  let fail: Error | null = null;
  const send = vi.fn(async (op: 'activityAdd' | 'activityList', body: unknown) => {
    calls.push({ op, body });
    if (fail) throw fail;
    if (op === 'activityList') return { entries: [...kept].sort((a, b) => b.at - a.at) };
    const { entries } = body as { entries: ActivityEntry[] };
    for (const e of entries) if (!kept.some((k) => k.id === e.id)) kept.push(e);
    return { added: entries.length };
  });
  return {
    send,
    calls,
    kept,
    failWith: (e: Error | null) => {
      fail = e;
    },
  };
}
const notice = (message: string, kind: Notice['kind'] = 'info', details?: string): Notice => ({
  id: crypto.randomUUID(),
  message,
  kind,
  expires: Date.now() + 4000,
  ...(details && { details }),
});

describe('Activity in the companion', () => {
  it('sends nothing from a guest page or the example, and nothing before the companion answers', async () => {
    const c = companion();
    const sync = createActivitySync({ send: c.send, restoreShared: () => {} });
    sync.push(noticeEntry(notice('Guest notice')));
    await sync.connect(true);
    await vi.runAllTimersAsync();
    expect(c.calls).toEqual([]);
    sync.arm(true);
    sync.push(noticeEntry(notice('Queued while connecting')));
    await vi.runAllTimersAsync();
    expect(c.calls).toEqual([]);
    expect(sync.state().queued).toBe(1);
  });

  it('asks for Activity once per connection, then sends the queue in batches of 20', async () => {
    const c = companion([
      {
        id: 'old-1',
        kind: 'error',
        source: 'notice',
        text: 'Before the reload',
        at: Date.now() - 60000,
        details: 'Error: x',
      },
      { id: 'old-2', kind: 'info', source: 'join', text: 'Alex joined', at: Date.now() - 50000 },
    ]);
    const shared: KeptEntry[] = [];
    const sync = createActivitySync({ send: c.send, restoreShared: (e) => shared.push(...e) });
    sync.arm(true);
    for (let i = 0; i < 25; i++) sync.push(noticeEntry(notice(`N${i}`), Date.now() + i));
    await sync.connect(true);
    await vi.runAllTimersAsync();
    expect(c.calls.map((x) => x.op)).toEqual(['activityList', 'activityAdd', 'activityAdd']);
    expect((c.calls[1].body as { entries: unknown[] }).entries).toHaveLength(ACTIVITY_LIMITS.batch);
    expect((c.calls[2].body as { entries: unknown[] }).entries).toHaveLength(5);
    expect(noticeState().history.map((h) => h.message)).toEqual(['Before the reload']);
    expect(noticeState().history[0]).toMatchObject({ restored: true, details: 'Error: x' });
    expect(shared.map((e) => e.text)).toEqual(['Alex joined']);
    // A reconnect (same companion) asks again but adds nothing twice, not even this page's own lines.
    await sync.connect(false);
    await sync.connect(true);
    await vi.runAllTimersAsync();
    expect(c.calls.filter((x) => x.op === 'activityList')).toHaveLength(2);
    expect(noticeState().history).toHaveLength(1);
  });

  it('keeps a batch lost on the way and drops one the companion refuses', async () => {
    const c = companion();
    const sync = createActivitySync({ send: c.send, restoreShared: () => {} });
    sync.arm(true);
    await sync.connect(true);
    c.failWith(new Error('Companion is disconnected.'));
    sync.push(noticeEntry(notice('Lost on the way')));
    await vi.advanceTimersByTimeAsync(400);
    expect(sync.state().queued).toBe(1);
    c.failWith(null);
    await vi.advanceTimersByTimeAsync(5000);
    expect(c.kept.map((e) => e.text)).toEqual(['Lost on the way']);
    c.failWith(new Error('Invalid Activity entry.'));
    sync.push(noticeEntry(notice('Refused')));
    await vi.advanceTimersByTimeAsync(400);
    expect(sync.state().queued).toBe(0);
  });

  it('cuts texts to what the companion accepts and gives each showing its own id', () => {
    const n = notice('x'.repeat(600), 'error', 'd'.repeat(5000));
    const a = noticeEntry(n, 1000);
    const b = noticeEntry(n, 2000);
    expect(a.text).toHaveLength(ACTIVITY_LIMITS.text);
    expect(a.details).toHaveLength(ACTIVITY_LIMITS.details);
    expect(a.id).not.toBe(b.id);
    expect(a.id.length).toBeLessThanOrEqual(64);
    expect(a.id).toMatch(/^[A-Za-z0-9-]+$/);
  });

  it('forgets the queue when the page stops being a host page', async () => {
    const c = companion();
    const sync = createActivitySync({ send: c.send, restoreShared: () => {} });
    sync.arm(true);
    sync.push(noticeEntry(notice('Host notice')));
    sync.arm(false);
    await sync.connect(true);
    await vi.runAllTimersAsync();
    expect(c.calls).toEqual([]);
  });
});
