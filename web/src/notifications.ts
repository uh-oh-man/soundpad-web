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
let notices: Notice[] = [];
/** Notices with a button that the pointer or keyboard focus is on (2.34.2): they don't expire meanwhile. */
const held = new Set<string>();
const listeners = new Set<() => void>();
const emit = () => {
  for (const listener of listeners) listener();
};
export function dismissNotice(id: string) {
  held.delete(id);
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
  const entry: Notice = {
    id: existing?.id ?? crypto.randomUUID(),
    message,
    kind,
    // A notice with a button stays long enough to reach it (2.28); errors 8 s as before, others 4 s.
    expires: Date.now() + (action ? 10000 : kind === 'error' ? 8000 : 4000),
    ...(action && { action }),
  };
  notices = [...notices.filter((n) => n.id !== entry.id), entry].slice(-4);
  emit();
  return entry.id;
}
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
export function useNotices() {
  const value = useSyncExternalStore(subscribe, () => notices);
  useEffect(() => {
    if (!value.length) return;
    const timed = value.filter((n) => !held.has(n.id));
    if (!timed.length) return;
    const timer = setTimeout(
      () => {
        notices = notices.filter((n) => held.has(n.id) || n.expires > Date.now());
        emit();
      },
      Math.max(0, Math.min(...timed.map((n) => n.expires)) - Date.now()) + 10,
    );
    return () => clearTimeout(timer);
  }, [value]);
  return value;
}
