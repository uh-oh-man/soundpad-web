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
const listeners = new Set<() => void>();
const emit = () => {
  for (const listener of listeners) listener();
};
export function dismissNotice(id: string) {
  notices = notices.filter((n) => n.id !== id);
  emit();
}
export function notify(message: string, kind: NoticeKind = 'info', action?: NoticeAction) {
  if (!message) return;
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
    const timer = setTimeout(
      () => {
        notices = notices.filter((n) => n.expires > Date.now());
        emit();
      },
      Math.max(0, Math.min(...value.map((n) => n.expires)) - Date.now()) + 10,
    );
    return () => clearTimeout(timer);
  }, [value]);
  return value;
}
