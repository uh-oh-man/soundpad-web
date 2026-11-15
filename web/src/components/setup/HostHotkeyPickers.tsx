import { useState } from 'react';
import { hostChordOf } from '../../shortcuts';
import { HOST_ACTIONS, setHostHotkey, useSetupStore, type HostAction } from './setup-store';
import { ShortcutRecorder } from './ShortcutRecorder';

const LABELS: Record<HostAction, { title: string; text: string }> = {
  capture: {
    title: 'Capture',
    text: 'Starts listening on your remembered output, or stops listening.',
  },
  panic: { title: 'Panic', text: 'Stops Soundpad and pauses guest playback (resume it here).' },
  stop: { title: 'Stop playback', text: 'Stops what Soundpad plays.' },
};
const STATE_TEXT: Record<string, string> = {
  off: 'Off',
  on: 'On',
  taken: 'Taken by another app',
  invalid: 'Not registered',
  conflict: 'Same as the Mimic key',
  disabled: 'Off for this run',
};

/**
 * The host-wide hotkeys (2.44, gate hostHotkeys): Capture, Panic and Stop playback, each off until the
 * host records a key, with what the companion says about it. The companion checks each key (a
 * modifier where needed, Windows' own keys, a key for two actions, the Mimic key) and registers it; a
 * key another app owns is reported and the previous one keeps working.
 */
export function HostHotkeyPickers({ connected }: { connected: boolean }) {
  const { hotkeys } = useSetupStore();
  const [busy, setBusy] = useState<HostAction | null>(null);
  const [errors, setErrors] = useState<Partial<Record<HostAction, string>>>({});
  async function change(action: HostAction, chord: string | null) {
    if (busy) return;
    setBusy(action);
    setErrors((e) => ({ ...e, [action]: undefined }));
    try {
      await setHostHotkey(action, chord);
    } catch (e) {
      setErrors((all) => ({ ...all, [action]: e instanceof Error ? e.message : String(e) }));
    } finally {
      setBusy(null);
    }
  }
  return (
    <div className="host-hotkeys" role="group" aria-label="Host-wide hotkeys">
      <p className="host-hotkeys-note">
        Works in every app on this PC while the companion runs; other apps can't use these keys
        meanwhile. All are off until you record one. F6 (save the last N seconds) stays in the Mimic
        window.
      </p>
      {hotkeys?.problem && <p className="inline-error">{hotkeys.problem}</p>}
      {!hotkeys ? (
        <p className="muted">{connected ? 'Reading the companion’s hotkeys…' : 'Connect first.'}</p>
      ) : (
        <ul className="shortcut-list">
          {HOST_ACTIONS.map((action) => {
            const bound = hotkeys.bindings[action];
            const status = hotkeys.status[action];
            const error = errors[action];
            return (
              <li key={action} className="shortcut-row" data-hotkey={action}>
                <div className="shortcut-name">
                  <strong>{LABELS[action].title}</strong>
                  <small>{LABELS[action].text}</small>
                </div>
                <span className="shortcut-key" aria-label={`Host-wide ${LABELS[action].title} key`}>
                  {bound ? <kbd className="kbd">{bound}</kbd> : <em>Off</em>}
                </span>
                <span className="shortcut-actions">
                  <ShortcutRecorder
                    label={`Record a host-wide key for ${LABELS[action].title}`}
                    busy={busy !== null}
                    disabled={!connected}
                    onKey={(e) => {
                      const chord = hostChordOf(e);
                      if (!chord)
                        setErrors((all) => ({
                          ...all,
                          [action]:
                            'That key can’t be a host-wide hotkey. Use an F-key, a letter or digit with Ctrl, Alt or Win, the keypad, Pause, ScrollLock, Insert, Home, End, PageUp or PageDown.',
                        }));
                      else void change(action, chord);
                    }}
                  />
                  <button
                    type="button"
                    aria-label={`Turn off the host-wide ${LABELS[action].title} key`}
                    aria-disabled={!connected || !bound || busy !== null || undefined}
                    onClick={() => {
                      // aria-disabled, not disabled: a pressed button that disables itself drops focus.
                      if (connected && bound && busy === null) void change(action, null);
                    }}
                  >
                    Off
                  </button>
                </span>
                <p
                  className={
                    status.state === 'taken' ||
                    status.state === 'invalid' ||
                    status.state === 'conflict'
                      ? 'inline-error shortcut-note'
                      : 'shortcut-note'
                  }
                  role="status"
                  data-state={status.state}
                >
                  {STATE_TEXT[status.state]}
                  {status.message ? `: ${status.message}` : ''}
                  {status.last ? ` · Last press: ${status.last}` : ''}
                </p>
                {error && (
                  <p className="inline-error shortcut-note" role="alert">
                    {error}
                  </p>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
