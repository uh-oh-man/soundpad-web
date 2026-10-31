import { Link, LogOut, Radio, RotateCcw, Trash2, Users } from 'lucide-react';
import { controller } from '../controller';
import { MAX_GUESTS } from '../network/session';
import type { Snapshot } from '../protocol';
import { themes, type Theme, type useAppearance } from '../appearance';

type View = ReturnType<typeof controller.getSnapshot>;
type Run = (
  op: Parameters<typeof controller.run>[0],
  fields?: Parameters<typeof controller.run>[1],
) => Promise<void>;
type Permission = keyof Snapshot['permissions'];

const permissionToggles: {
  key: Permission;
  title: string;
  description: string;
  label: string;
  /** Requires a protocol 2 companion. */
  v2?: boolean;
}[] = [
  {
    key: 'remotePlayback',
    title: 'Remote playback',
    description: 'Guests can play and stop sounds.',
    label: 'Allow remote playback',
  },
  {
    key: 'guestCounter',
    title: 'Guest Kick counter',
    description: 'Allow guests to add or subtract kicks.',
    label: 'Allow guest counter changes',
  },
  {
    key: 'guestSpotify',
    title: 'Guest Spotify controls',
    description: "Guests can control the host's enabled Spotify player.",
    label: 'Allow guest Spotify controls',
    v2: true,
  },
  {
    key: 'guestSeeking',
    title: 'Guest seeking & pause',
    description: 'Allow waveform seeking, cues and regions.',
    label: 'Allow guest seeking',
    v2: true,
  },
  {
    key: 'guestUploads',
    title: 'Guest session uploads',
    description: 'Allow bounded audio uploads into Temp.',
    label: 'Allow guest uploads',
  },
  {
    key: 'guestTransforms',
    title: 'Guest reverse generation',
    description: 'Allow guests to prepare reversed variants.',
    label: 'Allow guest reverse generation',
  },
];

export function SettingsDialog({
  view,
  connected,
  appearance,
  run,
  close,
}: {
  view: View;
  connected: boolean;
  appearance: ReturnType<typeof useAppearance>;
  run: Run;
  close: () => void;
}) {
  const { state, status } = view;
  const host = view.mode === 'host';
  const v2 = (state.protocol ?? 1) >= 2;
  return (
    <>
      <section className="appearance-setting" aria-label="Appearance">
        <h3>Appearance</h3>
        <label>
          Theme
          <select
            aria-label="Theme"
            value={appearance.theme}
            onChange={(event) => appearance.setTheme(event.target.value as Theme)}
          >
            {themes.map((theme) => (
              <option key={theme.id} value={theme.id}>
                {theme.name}
              </option>
            ))}
          </select>
        </label>
        <p>Only your browser's appearance changes.</p>
        {appearance.error && <p className="inline-error">{appearance.error}</p>}
      </section>
      <div className="settings-status">
        <Radio size={18} />
        <div>
          <strong>{status}</strong>
          <p>
            {host
              ? 'The companion controls Soundpad. This tab connects your guests.'
              : 'Your host controls playback and permissions.'}
          </p>
        </div>
      </div>
      {host && (
        <>
          {permissionToggles
            .filter((toggle) => v2 || !toggle.v2)
            .map(({ key, title, description, label }) => (
              <div className="setting-row" key={key}>
                <div>
                  <strong>{title}</strong>
                  <p>{description}</p>
                </div>
                <input
                  type="checkbox"
                  aria-label={label}
                  checked={state.permissions[key] !== false}
                  onChange={(e) =>
                    void run('permissions', {
                      permissions: { ...state.permissions, [key]: e.target.checked },
                    })
                  }
                />
              </div>
            ))}
          <div className="invite-field">
            <label htmlFor="invite">Invitation link · anyone with this link can join</label>
            <input id="invite" value={view.invite} readOnly onFocus={(e) => e.target.select()} />
            <button disabled={!view.invite} onClick={() => void controller.copyInvite()}>
              <Link size={15} /> Copy Invite
            </button>
            <button
              onClick={() => {
                if (confirm('Revoke the current invite and disconnect all guests?'))
                  void controller.rotate();
              }}
            >
              <RotateCcw size={15} /> Replace invite
            </button>
          </div>
          <p className="muted">
            Disconnect removes this connection only. Replace invite revokes the bearer link; a
            disconnected guest can otherwise rejoin.
          </p>
          <h3>
            Connected guests{' '}
            <small>
              {view.guests.length} / {MAX_GUESTS}
            </small>
          </h3>
          {view.guests.length ? (
            view.guests.map((id, i) => (
              <div className="guest-row" key={id}>
                <span>
                  <Users size={17} /> {view.people[id]?.name ?? `Guest ${i + 1}`}{' '}
                  <small>{id.slice(0, 8)}</small>
                </span>
                <button onClick={() => controller.disconnect(id)}>Disconnect</button>
              </div>
            ))
          ) : (
            <p className="muted">Share your invitation to bring friends in.</p>
          )}
          <button
            className="danger-link"
            disabled={!connected}
            onClick={() => {
              if (confirm('Reset the Kick counter to zero?')) void run('reset');
            }}
          >
            <Trash2 size={15} /> Reset Kick counter
          </button>
          {v2 && (
            <button
              className="danger-link"
              disabled={!connected}
              onClick={() => {
                if (confirm('Clear web play statistics? The Kick counter is preserved.'))
                  void run('clearStatistics');
              }}
            >
              Clear web statistics
            </button>
          )}
        </>
      )}
      <div className="dialog-foot">
        <span>Keep the host tab and companion open. Closing either ends guest control.</span>
        <button
          onClick={() => {
            close();
            void controller.leave();
          }}
        >
          <LogOut size={16} /> {host ? 'End hosting' : 'Leave session'}
        </button>
      </div>
    </>
  );
}
