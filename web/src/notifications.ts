import { useEffect, useSyncExternalStore } from 'react';
export type NoticeKind = 'info' | 'success' | 'error';
export type Notice = { id: string; message: string; kind: NoticeKind; expires: number };
let notices: Notice[] = [];
const listeners = new Set<() => void>();
const emit = () => {
  for (const listener of listeners) listener();
};
export function dismissNotice(id: string) {
  notices = notices.filter((n) => n.id !== id);
  emit();
}
export function notify(message: string, kind: NoticeKind = 'info') {
  if (!message) return;
  const existing = notices.find((n) => n.message === message);
  const entry = {
    id: existing?.id ?? crypto.randomUUID(),
    message,
    kind,
    expires: Date.now() + (kind === 'error' ? 8000 : 4000),
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
