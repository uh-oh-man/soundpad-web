import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import {
  ACTION_GRACE_MS,
  dismissNotice,
  expireNotices,
  markActivitySeen,
  holdNotice,
  noticeActionRan,
  noticeState,
  notify,
  onNoticePushed,
  resetNotices,
  restoreNoticeHistory,
  runNoticeAction,
  setDetailsContext,
} from '../../web/src/notifications';
import { commandDetails, redactPaths } from '../../web/src/technical-details';

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2026-11-14T12:00:00Z'));
  resetNotices();
});
afterEach(() => vi.useRealTimers());

it('keeps at most four visible notices and deduplicates by text', () => {
  for (let i = 0; i < 6; i++) notify(`Notice ${i}`);
  notify('Notice 5');
  const { notices, history } = noticeState();
  expect(notices.map((n) => n.message)).toEqual(['Notice 2', 'Notice 3', 'Notice 4', 'Notice 5']);
  // Activity keeps every notice once, newest first.
  expect(history.map((h) => h.message)).toEqual([
    'Notice 5',
    'Notice 4',
    'Notice 3',
    'Notice 2',
    'Notice 1',
    'Notice 0',
  ]);
});

it('keeps the last 200 notices in Activity (50 before 2.45)', () => {
  for (let i = 0; i < 210; i++) notify(`N${i}`);
  const { history } = noticeState();
  expect(history).toHaveLength(200);
  expect(history[0].message).toBe('N209');
  expect(history[199].message).toBe('N10');
});

it('tells listeners about each push exactly once (the single announcer)', () => {
  const heard: string[] = [];
  const stop = onNoticePushed((n) => heard.push(n.message));
  notify('One');
  notify('Two', 'error');
  notify('');
  stop();
  notify('Three');
  expect(heard).toEqual(['One', 'Two']);
});

it("keeps a notice's action usable in Activity for 60 s after its card expires", () => {
  const run = vi.fn();
  const pushed = Date.now();
  const id = notify('Copy added', 'success', { label: 'Undo', run });
  // A notice with a button stays 10 s; the 60 s count from then, however late the check runs.
  vi.advanceTimersByTime(15_000);
  expireNotices();
  expect(noticeState().notices).toHaveLength(0);
  const record = noticeState().history[0];
  expect(record.actionUntil).toBe(pushed + 10_000 + ACTION_GRACE_MS);
  vi.advanceTimersByTime(ACTION_GRACE_MS - 6000);
  expect(runNoticeAction(id)).toBe(true);
  expect(run).toHaveBeenCalledTimes(1);
  // Once only.
  expect(runNoticeAction(id)).toBe(false);
  expect(run).toHaveBeenCalledTimes(1);
});

it('no longer offers an action after its 60 s', () => {
  const run = vi.fn();
  const id = notify('Copy added', 'success', { label: 'Undo', run });
  dismissNotice(id);
  vi.advanceTimersByTime(ACTION_GRACE_MS + 1);
  expect(runNoticeAction(id)).toBe(false);
  expect(run).not.toHaveBeenCalled();
});

it('an action run from its card is not offered again in Activity', () => {
  const run = vi.fn();
  const id = notify('Copy added', 'success', { label: 'Undo', run });
  noticeActionRan(id);
  expect(noticeState().history[0].done).toBe(true);
  expect(runNoticeAction(id)).toBe(false);
});

it('counts notices since Activity was looked at', () => {
  notify('a');
  notify('b');
  expect(noticeState().unread).toBe(2);
  markActivitySeen();
  expect(noticeState().unread).toBe(0);
  notify('c');
  expect(noticeState().unread).toBe(1);
});

// 2.45 (D07): technical details on error notices, and Activity brought back from the companion.
it('gives every error notice technical details, its own or the page context', () => {
  setDetailsContext(() => 'Page: host\nWebsite: 2.45.0');
  try {
    notify('Saved', 'success');
    notify('Soundpad refused', 'error');
    notify('Upload failed', 'error', undefined, 'Command: upload\nError: 413');
    const { notices, history } = noticeState();
    expect(notices[0].details).toBeUndefined();
    expect(notices[1].details).toBe(
      'Time: 2026-11-14T12:00:00.000Z\nPage: host\nWebsite: 2.45.0\nMessage: Soundpad refused',
    );
    expect(notices[2].details).toBe('Command: upload\nError: 413');
    expect(history[0].details).toBe('Command: upload\nError: 413');
  } finally {
    setDetailsContext(null);
  }
});

it('holds a notice with details while someone reads them, then lets it go', () => {
  const id = notify('Failed', 'error', undefined, 'Error: x');
  holdNotice(id, true);
  vi.advanceTimersByTime(20000);
  expireNotices();
  expect(noticeState().notices).toHaveLength(1);
  holdNotice(id, false);
  vi.advanceTimersByTime(3001);
  expireNotices();
  expect(noticeState().notices).toHaveLength(0);
});

it('restores Activity from the companion once, in time order, without buttons or unread', () => {
  const now = Date.now();
  notify('Fresh on this page');
  markActivitySeen();
  const entries = [
    { id: 'c1', kind: 'error' as const, text: 'Older error', at: now - 5000, details: 'Error: x' },
    { id: 'c2', kind: 'success' as const, text: 'Newer', at: now - 1000, details: null },
  ];
  expect(restoreNoticeHistory(entries)).toBe(2);
  expect(restoreNoticeHistory(entries)).toBe(0);
  const { history, unread } = noticeState();
  expect(history.map((h) => h.message)).toEqual(['Fresh on this page', 'Newer', 'Older error']);
  expect(history[2]).toMatchObject({ restored: true, details: 'Error: x', actionUntil: 0 });
  expect(history[1].details).toBeUndefined();
  expect(unread).toBe(0);
  expect(runNoticeAction('c1')).toBe(false);
});

it('writes command details, and hides folders for guests', () => {
  const details = commandDetails({
    op: 'play',
    id: '0f0e0d0c-0000-4000-8000-000000000000',
    error: String.raw`Could not open C:\Users\Pat\Music\horn.wav`,
    role: 'guest',
    companion: '2.45.0',
    website: '2.45.0',
    at: Date.now(),
  });
  expect(details).toBe(
    [
      'Time: 2026-11-14T12:00:00.000Z',
      'Page: guest',
      'Command: play',
      'Command id: 0f0e0d0c-0000-4000-8000-000000000000',
      'Website: 2.45.0',
      'Companion: 2.45.0',
      String.raw`Answer: Could not open …\horn.wav`,
    ].join('\n'),
  );
  expect(redactPaths(String.raw`Saved to \\nas\share\x\one.wav and D:\a b\c.wav`)).toBe(
    String.raw`Saved to …\one.wav and …\c.wav`,
  );
  expect(redactPaths('no paths here')).toBe('no paths here');
});
