import { AudioLines, Settings2 } from 'lucide-react';
import { controller } from '../../controller';
import { APP_VERSION_LABEL } from '../../version';

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
};

/**
 * The header: brand and version, connection status, Mimic's badge, the display name and Access
 * settings (Stage G P0.6: the JSX moved out of main.tsx unchanged; WP-A owns it from here).
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
}: TopBarProps) {
  const { state, mode, status } = view;
  return (
    <header className="topbar">
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
      <div className="top-actions">
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
