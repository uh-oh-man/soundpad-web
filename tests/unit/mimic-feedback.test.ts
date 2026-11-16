import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { atEnd, FOLLOW_SLACK_PX } from '../../web/src/mimic/follow';
import {
  CHECK_MS,
  clipDeletable,
  deletedNow,
  deleteWithUndo,
  flushPendingDeletes,
  resetPendingDeletes,
} from '../../web/src/mimic/pending-delete';
import {
  dismissNotice,
  expireNotices,
  noticeShown,
  noticeState,
  resetNotices,
  retireNoticeAction,
  runNoticeAction,
} from '../../web/src/notifications';
import { PREF_DEFAULTS, readPref, resetPrefsForTests, writePref } from '../../web/src/prefs';
import type { TempAsset } from '../../web/src/temp-assets';

describe('the live transcript follows the newest words (2.46)', () => {
  it('counts a list as at its end within the slack, and a short list always', () => {
    expect(atEnd({ scrollTop: 300, scrollHeight: 468, clientHeight: 168 })).toBe(true);
    expect(atEnd({ scrollTop: 300 - FOLLOW_SLACK_PX, scrollHeight: 468, clientHeight: 168 })).toBe(
      true,
    );
    expect(
      atEnd({ scrollTop: 300 - FOLLOW_SLACK_PX - 1, scrollHeight: 468, clientHeight: 168 }),
    ).toBe(false);
    // Scrolled back to the oldest words.
    expect(atEnd({ scrollTop: 0, scrollHeight: 468, clientHeight: 168 })).toBe(false);
    // Nothing to scroll yet.
    expect(atEnd({ scrollTop: 0, scrollHeight: 100, clientHeight: 168 })).toBe(true);
  });
});

/** The Undo notice that `deleteWithUndo` showed last. */
const lastNotice = () => noticeState().notices.at(-1)!;

describe('Delete with Undo (2.46)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-15T22:00:00Z'));
    resetNotices();
    resetPendingDeletes();
  });
  afterEach(() => vi.useRealTimers());

  it('hides the item at once and asks the companion only when the Undo notice has gone', async () => {
    const commit = vi.fn(() => Promise.resolve());
    deleteWithUndo('clip-1', 'Deleted “Oops”.', commit);
    expect(deletedNow().has('clip-1')).toBe(true);
    const notice = lastNotice();
    expect(notice.message).toBe('Deleted “Oops”.');
    expect(notice.action?.label).toBe('Undo');
    // While the card shows, nothing is deleted.
    await vi.advanceTimersByTimeAsync(9000);
    expect(commit).not.toHaveBeenCalled();
    // The card's time runs out (10 s for a notice with a button).
    vi.advanceTimersByTime(1500);
    expireNotices();
    expect(noticeShown(notice.id)).toBe(false);
    await vi.advanceTimersByTimeAsync(CHECK_MS + 10);
    expect(commit).toHaveBeenCalledTimes(1);
    // Deleted: it stays hidden, and Activity no longer offers an Undo that can't work.
    expect(deletedNow().has('clip-1')).toBe(true);
    expect(noticeState().history.find((h) => h.id === notice.id)?.done).toBe(true);
    expect(runNoticeAction(notice.id)).toBe(false);
  });

  it('Undo shows the item again and never deletes it', async () => {
    const commit = vi.fn(() => Promise.resolve());
    const undone = vi.fn();
    deleteWithUndo('draft-1', 'Deleted the draft.', commit, undone);
    expect(runNoticeAction(lastNotice().id)).toBe(true);
    expect(deletedNow().has('draft-1')).toBe(false);
    expect(undone).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(commit).not.toHaveBeenCalled();
  });

  it('a refused delete shows the item again', async () => {
    deleteWithUndo('clip-2', 'Deleted “Busy”.', () => Promise.reject(new Error('Stop playback')));
    dismissNotice(lastNotice().id);
    await vi.advanceTimersByTimeAsync(CHECK_MS + 10);
    expect(deletedNow().has('clip-2')).toBe(false);
  });

  it('a second Delete of the same item is ignored; leaving the page deletes what is waiting', async () => {
    const commit = vi.fn(() => Promise.resolve());
    deleteWithUndo('clip-3', 'Deleted “A”.', commit);
    deleteWithUndo('clip-3', 'Deleted “A”.', commit);
    flushPendingDeletes();
    await vi.advanceTimersByTimeAsync(0);
    expect(commit).toHaveBeenCalledTimes(1);
    expect(deletedNow().has('clip-3')).toBe(true);
  });

  it('retiring an action is only for one not yet used', () => {
    deleteWithUndo('clip-4', 'Deleted “B”.', () => Promise.resolve());
    const { id } = lastNotice();
    retireNoticeAction(id);
    expect(noticeState().history.find((h) => h.id === id)?.done).toBe(true);
  });
});

describe('which clips Delete can take out of Temp (2.46)', () => {
  const variant = (permanent: boolean) => ({
    id: 'v',
    soundId: 's',
    orientation: 'normal',
    state: 'ready',
    ready: true,
    permanent,
    legacyApproval: false,
    current: true,
  });
  const asset = (hasTemporary: boolean, permanent = false): TempAsset => ({
    id: 'a',
    name: 'Clip',
    origin: 'mimic',
    uploader: 'Host',
    state: 'ready',
    normal: variant(permanent),
    reversed: null,
    variants: [variant(permanent)],
    hasTemporary,
  });
  it('a temporary clip yes; a kept one or one already gone no; unknown leaves it to the companion', () => {
    expect(clipDeletable(asset(true))).toBe(true);
    expect(clipDeletable(asset(true, true))).toBe(false);
    expect(clipDeletable(asset(false))).toBe(false);
    expect(clipDeletable(undefined)).toBe(true);
  });
});

describe('Mimic full screen is a per-browser preference (2.46)', () => {
  afterEach(() => resetPrefsForTests());
  it('is off by default and holds a change for this tab when storage is unavailable', () => {
    expect(PREF_DEFAULTS.mimicFull).toBe(false);
    expect(readPref('mimicFull')).toBe(false);
    writePref('mimicFull', true);
    expect(readPref('mimicFull')).toBe(true);
  });
});
