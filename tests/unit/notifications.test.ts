import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import {
  ACTION_GRACE_MS,
  dismissNotice,
  expireNotices,
  markActivitySeen,
  noticeActionRan,
  noticeState,
  notify,
  onNoticePushed,
  resetNotices,
  runNoticeAction,
} from '../../web/src/notifications';

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

it('keeps the last 50 notices in Activity', () => {
  for (let i = 0; i < 60; i++) notify(`N${i}`);
  const { history } = noticeState();
  expect(history).toHaveLength(50);
  expect(history[0].message).toBe('N59');
  expect(history[49].message).toBe('N10');
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
