import { Link, LogOut, Radio, RotateCcw, Trash2 } from 'lucide-react';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { SessionAccess } from './SessionAccess';
import { ConnectionDetails } from './ConnectionDetails';
import { missingFeatures, supports } from '../version';
import { DEMO_COPY, hostCopy } from '../hosting';
import { themes, type Theme, type useAppearance } from '../appearance';

const COMPANION_DOWNLOAD = `${(import.meta.env.VITE_REPOSITORY_URL as string | undefined) || 'https://github.com/uh-oh-man/soundpad-web'}/releases/latest/download/SoundpadWeb.exe`;

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
  /** Requires this companion feature. */
  feature?: Parameters<typeof supports>[1];
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
  {
    key: 'guestMimic',
    title: 'Guest Mimic',
    description: "Guests can see Mimic's live buffer and clip from it while you have it listening.",
    label: 'Allow guest Mimic',
    v2: true,
    feature: 'mimic',
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
  // Who keeps the session running (2.36): this tab, the companion window, or another page this tab views.
  // The example session runs in this tab and reaches no Soundpad, and says so.
  const copy = view.demo
    ? DEMO_COPY
    : hostCopy(view.hosting, state.companionVersion, view.ownerWindow);
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
          <strong>
            {status === 'Connected' && view.connection?.transport === 'relay'
              ? 'Connected (relayed, slower)'
              : status}
          </strong>
          <p>{host ? copy.status : 'Your host controls playback and permissions.'}</p>
        </div>
      </div>
      {host && !view.demo && missingFeatures(state.companionVersion).length > 0 && (
        // Features needing a newer companion are hidden, so say what an update would add. The example has no
        // companion to update (it stands in for a 2.37 one, without the recording history, 2.39).
        <p className="companion-features" role="note">
          Your Windows companion is {state.companionVersion}. A newer one adds{' '}
          {missingFeatures(state.companionVersion).join(', ')}.{' '}
          <a href={COMPANION_DOWNLOAD} target="_blank" rel="noreferrer">
            Download the latest companion
          </a>
        </p>
      )}
      {host && (
        <>
          {copy.note && (
            <p className="desktop-view-note" role="note">
              {copy.note}
            </p>
          )}
          {permissionToggles
            .filter(
              (toggle) =>
                (v2 || !toggle.v2) &&
                (!toggle.feature || supports(state.companionVersion, toggle.feature)),
            )
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
          <SessionAccess view={view} />
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
      <ConnectionDetails info={view.connection} host={host} />
      <div className="dialog-foot">
        <span>{host ? copy.foot : hostCopy('tab', null).foot}</span>
        {/* A view does not end the session (2.36); its owner, the window, keeps End hosting. */}
        {!(host && copy.place === 'view') && (
          <button
            onClick={() => {
              close();
              void controller.leave();
            }}
          >
            <LogOut size={16} />{' '}
            {view.demo ? 'Leave example' : host ? 'End hosting' : 'Leave session'}
          </button>
        )}
      </div>
    </>
  );
}
