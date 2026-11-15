import { useEffect, useRef, type ReactNode } from 'react';
import { AudioLines, History, Settings2 } from 'lucide-react';
import { controller } from '../../controller';
import { APP_VERSION_LABEL } from '../../version';
import { UpdateChip } from '../updates/UpdateOffer';

type View = ReturnType<typeof controller.getSnapshot>;

/** The header's connection label (Stage G P0.6: moved out of main.tsx unchanged). */
export function statusLabel(
  mode: string,
  connected: boolean,
  host: boolean,
  available: boolean,
  status: string,
  relayed = false,
  demo = false,
) {
  if (mode === 'landing') return 'Not connected';
  if (demo) return 'Example session';
  if (!connected) return status.split('.')[0];
  if (!available) return 'Soundpad unavailable';
  // Through the relay (2.37) everything works, only slower; the header says so.
  return host ? 'Hosting' : relayed ? 'Connected (relayed, slower)' : 'Connected';
}

export type TopBarProps = {
  view: View;
  connected: boolean;
  host: boolean;
  /** Mimic is listening (shows "Mimic on"). */
  mimicListening: boolean;
  /** How much Mimic keeps, for the badge's title ("the last 10 min"). */
  kept: string;
  canMimic: boolean;
  openMimic: () => void;
  openSettings: () => void;
  /** The section tabs, when they are shown in the topbar (top tabs style, narrow screens). */
  nav?: ReactNode;
  /** Notices since Activity was last opened, and whether it is open now. */
  activity?: { unread: number; open: boolean; show: () => void };
};

/**
 * The header (2.43): brand and version, the section tabs in top tabs style, connection status, what
 * Mimic is capturing, Activity, the display name and Access settings.
 */
export function TopBar({
  view,
  connected,
  host,
  mimicListening,
  kept,
  canMimic,
  openMimic,
  openSettings,
  nav,
  activity,
}: TopBarProps) {
  const { state, mode, status } = view;
  // The editor sheet (narrow screens) starts where the header ends; the header's height changes as it wraps.
  const header = useRef<HTMLElement>(null);
  useEffect(() => {
    const element = header.current;
    if (!element) return;
    const root = document.documentElement;
    const resize = new ResizeObserver(() =>
      root.style.setProperty('--topbar-bottom', `${element.getBoundingClientRect().height}px`),
    );
    resize.observe(element);
    return () => {
      resize.disconnect();
      root.style.removeProperty('--topbar-bottom');
    };
  }, []);
  // A 2.39 companion records a history while Mimic listens: say what is being captured, in words.
  const capturing =
    mode !== 'landing' && state.mimic?.active && state.mimic.history
      ? (state.mimic.source ?? 'a source')
      : null;
  return (
    <header className="topbar" ref={header}>
      <a href={location.pathname} className="brand">
        <span className="brand-icon">
          <AudioLines size={23} />
        </span>
        <span>
          soundpad<span className="brand-light"> web</span>
        </span>{' '}
        {/* The space keeps "web" and the version apart in the link's text; flex layout ignores it. */}
        <span className="release-version">v{APP_VERSION_LABEL}</span>
        {/* Named by its visible text (which changes with screen width) so voice control always matches. */}
        <span className="sr-only">, home</span>
      </a>
      <span className="edition">BROWSER REMOTE</span>
      {nav}
      <div className="top-actions">
        {/* 2.44 (gate updater): after Later, the update stays one press away. */}
        {mode !== 'landing' && <UpdateChip />}
        <span className={`status ${connected && state.available ? 'online' : ''}`} title={status}>
          <i />
          {statusLabel(
            mode,
            connected,
            host,
            state.available,
            status,
            view.connection?.transport === 'relay',
            view.demo,
          )}
        </span>
        {capturing && (
          <span className="capture-chip" role="status" title={`Capturing ${capturing}`}>
            <i aria-hidden="true" />
            <span className="capture-long">Capturing · {capturing}</span>
            <span className="capture-short" aria-hidden="true">
              Rec
            </span>
          </span>
        )}
        {mimicListening && (
          <button
            className="mimic-badge"
            title={`Mimic is keeping ${kept} of ${state.mimic?.source ?? 'a source'} on the host's PC`}
            disabled={!canMimic}
            onClick={openMimic}
          >
            <i /> Mimic on
          </button>
        )}
        {mode !== 'landing' && activity && (
          <button
            type="button"
            className="icon-button activity-button"
            aria-label="Activity"
            aria-expanded={activity.open}
            aria-controls={activity.open ? 'editor-panel' : undefined}
            aria-describedby={activity.unread ? 'activity-unread' : undefined}
            title="Activity: recent notices and what guests did"
            onClick={activity.show}
          >
            <History size={20} aria-hidden="true" />
            {activity.unread > 0 && (
              // The count is drawn by CSS, so it never becomes part of the button's name.
              <span
                className="badge"
                aria-hidden="true"
                data-count={activity.unread > 99 ? '99+' : activity.unread}
              />
            )}
          </button>
        )}
        {mode !== 'landing' && activity && activity.unread > 0 && (
          // Its description: the button's name stays "Activity" whatever the count.
          <span id="activity-unread" hidden>
            {activity.unread} new
          </span>
        )}
        {mode !== 'landing' && (
          <input
            className="profile-name"
            key={view.profile.name}
            aria-label="Your display name"
            title="Display names are not authenticated identities"
            maxLength={40}
            defaultValue={view.profile.name}
            onBlur={(e) => controller.rename(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') e.currentTarget.blur();
            }}
          />
        )}
        {mode !== 'landing' && (
          <button
            className="icon-button"
            // Waiting guests show as a badge until the host decides (2.37).
            aria-label={`Access settings${host && view.requests.length ? `, ${view.requests.length} waiting` : ''}`}
            onClick={openSettings}
          >
            <Settings2 size={20} />
            {host && view.requests.length > 0 && (
              <span className="badge" aria-hidden="true">
                {view.requests.length}
              </span>
            )}
          </button>
        )}
      </div>
    </header>
  );
}
