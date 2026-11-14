import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Check, Info, TriangleAlert, UserMinus, UserPlus, Users } from 'lucide-react';
import {
  markActivitySeen,
  runNoticeAction,
  useNoticeHistory,
  type NoticeKind,
} from '../../notifications';

/**
 * What happened in the session lately, as this page saw it (2.43): guests joining and leaving and
 * guests waiting in the lobby. Built from the snapshot and the controller's view, so nothing new is
 * asked of the companion. Kept in memory for this page only, the newest 50.
 */
export type SharedEvent = {
  id: number;
  at: number;
  text: string;
  kind: 'join' | 'leave' | 'lobby';
};
let events: SharedEvent[] = [];
let nextId = 1;
const listeners = new Set<() => void>();
function add(kind: SharedEvent['kind'], text: string) {
  events = [{ id: nextId++, at: Date.now(), text, kind }, ...events].slice(0, 50);
  for (const l of listeners) l();
}
const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
};
const useSharedEvents = () => useSyncExternalStore(subscribe, () => events);

type PeopleView = {
  guests: string[];
  people: Record<string, { name?: string } | undefined>;
  requests: { name?: string }[];
};
/** Mounted once by the app: notes joins, leaves and lobby requests as they happen. */
export function useSharedActivity(view: PeopleView, enabled: boolean) {
  const seen = useRef<Map<string, string> | null>(null);
  const waiting = useRef(0);
  useEffect(() => {
    if (!enabled) {
      seen.current = null;
      return;
    }
    const now = new Map(
      view.guests.map((id, i) => [id, view.people[id]?.name ?? `Guest ${i + 1}`]),
    );
    // Who was already here when this page started is not news.
    if (seen.current) {
      for (const [id, name] of now) if (!seen.current.has(id)) add('join', `${name} joined`);
      for (const [id, name] of seen.current) if (!now.has(id)) add('leave', `${name} left`);
    }
    seen.current = now;
  }, [enabled, view.guests, view.people]);
  useEffect(() => {
    if (!enabled) return;
    const count = view.requests.length;
    if (count > waiting.current) {
      const newest = view.requests[count - 1];
      add('lobby', `${newest?.name ?? 'A guest'} is waiting to join`);
    }
    waiting.current = count;
  }, [enabled, view.requests]);
}

const time = (at: number) =>
  new Date(at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const icon = (kind: NoticeKind) =>
  kind === 'error' ? (
    <TriangleAlert size={15} aria-hidden="true" />
  ) : kind === 'success' ? (
    <Check size={15} aria-hidden="true" />
  ) : (
    <Info size={15} aria-hidden="true" />
  );

/**
 * Activity (2.43), in the editor panel: every recent notice with its time, and its button (Undo, Jump,
 * Open) for 60 s after the notice itself has gone, so nothing that can be undone is lost to a timer.
 * Below, what the session's guests did.
 */
export function ActivityView() {
  const history = useNoticeHistory();
  const shared = useSharedEvents();
  // Re-render once a second while an action is still usable, so it leaves on time.
  const [now, setNow] = useState(() => Date.now());
  const live = history.some((h) => h.action && !h.done && h.actionUntil > now);
  useEffect(() => {
    if (!live) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [live]);
  useEffect(() => {
    markActivitySeen();
  }, [history]);
  return (
    <div className="activity-view">
      <section aria-labelledby="activity-notices">
        <h3 id="activity-notices">Notices</h3>
        {history.length ? (
          <ol className="activity-list">
            {history.map((h) => (
              <li key={h.id} className={`activity-item notice-${h.kind}`}>
                {icon(h.kind)}
                <time dateTime={new Date(h.at).toISOString()}>{time(h.at)}</time>
                <span>{h.message}</span>
                {h.action && !h.done && now < h.actionUntil && (
                  <button
                    type="button"
                    className="btn activity-action"
                    onClick={() => {
                      runNoticeAction(h.id);
                      setNow(Date.now());
                    }}
                  >
                    {h.action.label}
                  </button>
                )}
              </li>
            ))}
          </ol>
        ) : (
          <p className="muted">
            Nothing yet. Notices stay listed here after they leave the screen.
          </p>
        )}
      </section>
      <section aria-labelledby="activity-shared">
        <h3 id="activity-shared">Shared activity</h3>
        {shared.length ? (
          <ol className="activity-list">
            {shared.map((e) => (
              <li key={e.id} className="activity-item">
                {e.kind === 'join' ? (
                  <UserPlus size={15} aria-hidden="true" />
                ) : e.kind === 'leave' ? (
                  <UserMinus size={15} aria-hidden="true" />
                ) : (
                  <Users size={15} aria-hidden="true" />
                )}
                <time dateTime={new Date(e.at).toISOString()}>{time(e.at)}</time>
                <span>{e.text}</span>
              </li>
            ))}
          </ol>
        ) : (
          <p className="muted">Guests joining, leaving and waiting show here.</p>
        )}
      </section>
    </div>
  );
}
