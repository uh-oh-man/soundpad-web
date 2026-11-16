import { useEffect, useSyncExternalStore } from 'react';
export type NoticeKind = 'info' | 'success' | 'error';
/** One button on a notice (2.28), e.g. Undo after Edit copy. It runs once, then the notice closes. */
export type NoticeAction = { label: string; run: () => void };
export type Notice = {
  id: string;
  message: string;
  kind: NoticeKind;
  expires: number;
  action?: NoticeAction;
};
/**
 * A notice as Activity keeps it (2.43): the last 50, newest first. Its action stays usable for 60 s
 * after the card leaves (`actionUntil`), so nothing that can be undone is lost to the card's timer
 * (WCAG 2.2.1). `done` once the action ran.
 */
export type NoticeRecord = {
  id: string;
  message: string;
  kind: NoticeKind;
  at: number;
  action?: NoticeAction;
  actionUntil: number;
  done?: boolean;
};
/** How long a notice's action stays in Activity after its card has gone. */
export const ACTION_GRACE_MS = 60_000;
const HISTORY_SIZE = 50;

let notices: Notice[] = [];
let history: NoticeRecord[] = [];
/** Notices pushed since Activity was last looked at (the topbar badge). */
let unread = 0;
/** Notices with a button that the pointer or keyboard focus is on (2.34.2): they don't expire meanwhile. */
const held = new Set<string>();
const listeners = new Set<() => void>();
const pushListeners = new Set<(notice: Notice) => void>();
const emit = () => {
  for (const listener of listeners) listener();
};
/** Keeps a record's action usable for 60 s from now (its card just left), unless it already ran. */
function retire(ids: string[]) {
  if (!ids.length) return;
  const until = Date.now() + ACTION_GRACE_MS;
  history = history.map((h) => (ids.includes(h.id) && !h.done ? { ...h, actionUntil: until } : h));
}
export function dismissNotice(id: string) {
  held.delete(id);
  if (notices.some((n) => n.id === id)) retire([id]);
  notices = notices.filter((n) => n.id !== id);
  emit();
}
/**
 * Holds a notice that has a button while the pointer is over it or focus is inside it, so its button
 * can't vanish as someone reaches it (WCAG timing). On release it stays at least 3 more seconds.
 */
export function holdNotice(id: string, on: boolean) {
  const notice = notices.find((n) => n.id === id);
  if (!notice?.action) return;
  if (on) held.add(id);
  else if (held.delete(id)) {
    notices = notices.map((n) =>
      n.id === id ? { ...n, expires: Math.max(n.expires, Date.now() + 3000) } : n,
    );
    emit();
  }
}
/** Shows a notice and returns its id (for `dismissNotice`); '' when there was nothing to show. */
export function notify(message: string, kind: NoticeKind = 'info', action?: NoticeAction) {
  if (!message) return '';
  const existing = notices.find((n) => n.message === message);
  const now = Date.now();
  const entry: Notice = {
    id: existing?.id ?? crypto.randomUUID(),
    message,
    kind,
    // A notice with a button stays long enough to reach it (2.28); errors 8 s as before, others 4 s.
    expires: now + (action ? 10000 : kind === 'error' ? 8000 : 4000),
    ...(action && { action }),
  };
  const next = [...notices.filter((n) => n.id !== entry.id), entry];
  // At most 4 visible; the ones pushed out keep their action in Activity.
  retire(next.slice(0, -4).map((n) => n.id));
  notices = next.slice(-4);
  const record: NoticeRecord = {
    id: entry.id,
    message,
    kind,
    at: now,
    ...(action && { action }),
    actionUntil: entry.expires + ACTION_GRACE_MS,
  };
  history = [record, ...history.filter((h) => h.id !== entry.id)].slice(0, HISTORY_SIZE);
  unread++;
  emit();
  for (const listener of pushListeners) listener(entry);
  return entry.id;
}
/** Runs a notice's action from Activity, once, while it is still usable. */
export function runNoticeAction(id: string) {
  const record = history.find((h) => h.id === id);
  if (!record?.action || record.done || Date.now() >= record.actionUntil) return false;
  history = history.map((h) => (h.id === id ? { ...h, done: true } : h));
  held.delete(id);
  notices = notices.filter((n) => n.id !== id);
  emit();
  record.action.run();
  return true;
}
/** Whether the notice `id` is still on screen (its card, not its Activity record). */
export const noticeShown = (id: string) => notices.some((n) => n.id === id);
/**
 * What the notice's action would undo has now happened for good (2.46, Mimic's Delete): Activity stops
 * offering the action. The card itself is left alone.
 */
export function retireNoticeAction(id: string) {
  if (!history.some((h) => h.id === id && !h.done)) return;
  history = history.map((h) => (h.id === id ? { ...h, done: true } : h));
  emit();
}
/** A notice's action ran from its card: Activity no longer offers it. */
export function noticeActionRan(id: string) {
  history = history.map((h) => (h.id === id ? { ...h, done: true } : h));
  dismissNotice(id);
}
/** Calls `listener` once for every notice pushed (the live announcer, notice sounds). */
export function onNoticePushed(listener: (notice: Notice) => void) {
  pushListeners.add(listener);
  return () => {
    pushListeners.delete(listener);
  };
}
/** Activity was opened: the badge starts counting again from zero. */
export function markActivitySeen() {
  if (!unread) return;
  unread = 0;
  emit();
}
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
/** Drops the visible notices whose time is up (Activity keeps their actions for 60 s more). */
function expire() {
  const now = Date.now();
  const gone = notices.filter((n) => !held.has(n.id) && n.expires <= now);
  if (!gone.length) return;
  // Counted from when each card's time ran out, however late this check runs.
  for (const n of gone)
    history = history.map((h) =>
      h.id === n.id && !h.done ? { ...h, actionUntil: n.expires + ACTION_GRACE_MS } : h,
    );
  notices = notices.filter((n) => !gone.includes(n));
  emit();
}
export function useNotices() {
  const value = useSyncExternalStore(subscribe, () => notices);
  useEffect(() => {
    if (!value.length) return;
    const timed = value.filter((n) => !held.has(n.id));
    if (!timed.length) return;
    const timer = setTimeout(
      expire,
      Math.max(0, Math.min(...timed.map((n) => n.expires)) - Date.now()) + 10,
    );
    return () => clearTimeout(timer);
  }, [value]);
  return value;
}
export const useNoticeHistory = () => useSyncExternalStore(subscribe, () => history);
export const useUnreadNotices = () => useSyncExternalStore(subscribe, () => unread);
/** Test seam: the store's state without React. */
export const noticeState = () => ({ notices, history, unread });
/** Test seam: an empty store. */
export function resetNotices() {
  notices = [];
  history = [];
  unread = 0;
  held.clear();
  emit();
}
/** Test seam: what the visible list looks like after expiry at the current time. */
export const expireNotices = expire;
