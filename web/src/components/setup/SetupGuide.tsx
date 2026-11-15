import { useEffect, useReducer, useState, useSyncExternalStore } from 'react';
import { CheckCircle2, Link, RefreshCw } from 'lucide-react';
import { controller } from '../../controller';
import { useStageH } from '../../stage-h';
import { supports } from '../../version';
import { Modal } from '../Modal';
import { SessionAccess } from '../SessionAccess';
import { STEPS, guideReducer, initialGuide, soundpadAdvice } from './guide-state';
import { HostHotkeyPickers } from './HostHotkeyPickers';
import { ShortcutList } from './ShortcutList';
import {
  closeGuide,
  loadSetup,
  setupEligible,
  useHotkeysSync,
  useSetupStore,
  useSetupSync,
  type SetupInfo,
} from './setup-store';

/** The session-wide permissions the guide offers (the rest stay in Settings). */
const MAIN_PERMISSIONS = [
  { key: 'remotePlayback', label: 'Guests can play and stop sounds', feature: null },
  { key: 'guestSeeking', label: 'Guests can seek, pause and use cues', feature: null },
  { key: 'guestUploads', label: 'Guests can upload audio into Temp', feature: null },
  {
    key: 'guestMimic',
    label: 'Guest Mimic: guests can clip from what Mimic hears',
    feature: 'mimic',
  },
  { key: 'guestKeep', label: 'Guest Keep: guests can keep clips permanently', feature: 'drafts' },
] as const;

/**
 * The guided setup (2.44, gate setup): a dialog for a host on a real companion that has not finished
 * it. An upgraded install (kept data from before) gets the SetupCard instead, and Settings → Run setup
 * again opens this from anywhere. Steps: Soundpad (live status and the fix), the capture output
 * (remembered without listening), invitations and permissions, shortcuts, and how to launch it.
 * Finish tells the companion (setup.json); closing it early asks again on the next launch.
 */
export function SetupGuide() {
  const { state, mode, demo } = useStageH();
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const connected = view.status === 'Host ready';
  const eligible = setupEligible(state, mode, demo) && connected;
  const { info, open, closed } = useSetupStore();
  useSetupSync(state, eligible);
  const shown =
    eligible &&
    !!info &&
    (open || (state.setupDone === false && !info.existing && !info.done && !closed));
  useHotkeysSync(state, shown && supports(state.companionVersion, 'hostHotkeys'));
  if (!shown || !info) return null;
  return <Guide info={info} connected={connected} />;
}

function Guide({ info, connected }: { info: SetupInfo; connected: boolean }) {
  const { state } = useStageH();
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const [guide, dispatch] = useReducer(guideReducer, initialGuide);
  const [finishing, setFinishing] = useState(false);
  const [error, setError] = useState('');
  const { hotkeys } = useSetupStore();
  const step = STEPS[guide.step];
  const last = guide.step === STEPS.length - 1;
  useEffect(() => {
    if (!guide.finished) return;
    closeGuide();
  }, [guide.finished]);
  async function finish() {
    if (finishing) return;
    setFinishing(true);
    setError('');
    try {
      await controller.run('setupSet', { body: { action: 'done' } }, { quiet: true });
      dispatch({ type: 'finish' });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setFinishing(false);
    }
  }
  return (
    <Modal
      label="Set up Soundpad Web"
      onClose={closeGuide}
      // Opened from the setup card (which hides meanwhile) or by itself at first run: focus goes back to
      // the card if it is shown again, else to the sounds, never to the page.
      returnTo={() =>
        document.querySelector<HTMLElement>('.setup-card .primary') ??
        document.getElementById('board')
      }
    >
      <div className="setup-guide">
        <ol className="setup-steps" aria-label="Setup steps">
          {STEPS.map((s, i) => (
            <li key={s.id}>
              <button
                type="button"
                aria-current={i === guide.step ? 'step' : undefined}
                data-seen={guide.seen.includes(s.id) || undefined}
                onClick={() => dispatch({ type: 'go', step: i })}
              >
                <span className="setup-step-number" aria-hidden="true">
                  {i + 1}
                </span>{' '}
                {s.title}
              </button>
            </li>
          ))}
        </ol>
        <section className="setup-step" aria-labelledby="setup-step-title">
          <h3 id="setup-step-title">
            {guide.step + 1}. {step.title}
          </h3>
          {step.id === 'soundpad' && <SoundpadStep info={info} connected={connected} />}
          {step.id === 'capture' && <CaptureStep info={info} connected={connected} />}
          {step.id === 'invitations' && (
            <>
              <p>
                Guests join with your invitation link. It is new every time the companion starts, so
                send it again after a restart. Anyone with the link can ask to join.
              </p>
              <div className="invite-field">
                <label htmlFor="setup-invite">Invitation link</label>
                <input
                  id="setup-invite"
                  value={view.invite}
                  readOnly
                  onFocus={(e) => e.target.select()}
                />
                <button
                  type="button"
                  aria-disabled={!view.invite || undefined}
                  title={view.invite ? undefined : 'Start hosting first'}
                  onClick={() => view.invite && void controller.copyInvite()}
                >
                  <Link size={15} aria-hidden="true" /> Copy Invite
                </button>
              </div>
              {!view.invite && <p className="muted">Start hosting first: the link appears here.</p>}
              <p className="muted">
                Replace invite (Settings) makes a new link and disconnects everyone; use it when a
                link reached someone it shouldn’t. Admission and places below apply to this session.
              </p>
              <SessionAccess view={view} />
              <fieldset className="setup-permissions">
                <legend>What guests may do (you can change these any time in Settings)</legend>
                {MAIN_PERMISSIONS.filter(
                  (p) => !p.feature || supports(state.companionVersion, p.feature),
                ).map((p) => (
                  <label key={p.key}>
                    <input
                      type="checkbox"
                      checked={state.permissions[p.key] !== false}
                      disabled={!connected}
                      onChange={(e) =>
                        void controller
                          .run('permissions', {
                            permissions: { ...state.permissions, [p.key]: e.target.checked },
                          })
                          .catch(() => {})
                      }
                    />
                    {p.label}
                  </label>
                ))}
              </fieldset>
            </>
          )}
          {step.id === 'shortcuts' && (
            <>
              <p>
                Optional. Change the keys of this page (per browser), and choose host-wide hotkeys
                that work while you are in a game.
              </p>
              <h4>This page</h4>
              <ShortcutList
                host
                panic={supports(state.companionVersion, 'intents')}
                hostKeys={hotkeys ? Object.values(hotkeys.bindings) : []}
              />
              {supports(state.companionVersion, 'hostHotkeys') && (
                <>
                  <h4>Host-wide hotkeys</h4>
                  <HostHotkeyPickers connected={connected} />
                </>
              )}
            </>
          )}
          {step.id === 'launch' && (
            <>
              <p>
                Soundpad Web does not start with Windows. Open it from its EXE or shortcut when you
                host. Closing the window quits it.
              </p>
              <p className="muted">
                To keep copies of your kept sounds, voice library and settings, set a backup folder
                in Settings → Backups.
              </p>
            </>
          )}
        </section>
        {error && (
          <p className="inline-error" role="alert">
            {error}
          </p>
        )}
        <div className="setup-nav">
          <button
            type="button"
            aria-disabled={guide.step === 0 || undefined}
            onClick={() => guide.step > 0 && dispatch({ type: 'back' })}
          >
            Back
          </button>
          {last ? (
            <button
              type="button"
              className="primary"
              disabled={!connected}
              aria-disabled={finishing || undefined}
              onClick={() => void finish()}
            >
              <CheckCircle2 size={16} aria-hidden="true" /> Finish
            </button>
          ) : (
            <button type="button" className="primary" onClick={() => dispatch({ type: 'next' })}>
              Next
            </button>
          )}
        </div>
      </div>
    </Modal>
  );
}

function SoundpadStep({ info, connected }: { info: SetupInfo; connected: boolean }) {
  const { state } = useStageH();
  const [busy, setBusy] = useState(false);
  // Live from the snapshot; setupGet's copy is only the first answer.
  const problem = state.available ? null : (state.soundpadProblem ?? info.soundpad.problem);
  const elevated = state.companionElevated ?? info.soundpad.elevated;
  return (
    <>
      <p className="setup-status" role="status" data-ok={state.available || undefined}>
        {state.available ? (
          <>
            <CheckCircle2 size={16} aria-hidden="true" /> Soundpad answers. Its library has{' '}
            {state.library.sounds.length} sound{state.library.sounds.length === 1 ? '' : 's'}.
          </>
        ) : problem ? (
          soundpadAdvice(problem, elevated)
        ) : (
          (state.error ?? 'Waiting for Soundpad.')
        )}
      </p>
      {elevated && state.available && (
        <p className="muted">
          Soundpad Web is running as administrator. That works while Soundpad does too; start both
          the same way.
        </p>
      )}
      <button
        type="button"
        disabled={!connected}
        aria-disabled={busy || undefined}
        onClick={() => {
          if (busy) return;
          setBusy(true);
          void controller
            .run('refresh', {}, { quiet: true })
            .catch(() => {})
            .finally(() => {
              setBusy(false);
              void loadSetup();
            });
        }}
      >
        <RefreshCw size={15} aria-hidden="true" /> Retry
      </button>
    </>
  );
}

function CaptureStep({ info, connected }: { info: SetupInfo; connected: boolean }) {
  const [choice, setChoice] = useState(info.remembered ?? info.outputs[0]?.id ?? '');
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null);
  const remembered = info.outputs.find((o) => o.id === info.remembered);
  return (
    <>
      <p>
        Mimic and the Capture hotkey listen to one output of this PC. Only what this PC plays; never
        a microphone. Nothing is recorded until you press Start listening.
      </p>
      {info.outputs.length === 0 ? (
        <p className="muted">No outputs were found. Check Windows’ sound settings, then Retry.</p>
      ) : (
        <div className="setup-output">
          <label>
            Output
            <select
              aria-label="Capture output"
              value={choice}
              onChange={(e) => setChoice(e.target.value)}
            >
              {info.outputs.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            disabled={!connected || !choice}
            aria-disabled={busy || undefined}
            onClick={() => {
              if (busy) return;
              setBusy(true);
              void controller
                .run('mimicSources', { action: 'remember', sourceId: choice }, { quiet: true })
                .then(() => {
                  setNote({ text: 'Remembered. Nothing is being recorded.', error: false });
                  return loadSetup();
                })
                .catch((e: unknown) =>
                  setNote({ text: e instanceof Error ? e.message : String(e), error: true }),
                )
                .finally(() => setBusy(false));
            }}
          >
            Remember this output
          </button>
        </div>
      )}
      <p className="muted" role="status">
        {remembered ? `Remembered: ${remembered.name}.` : 'No output remembered yet.'}
      </p>
      {note && (
        <p className={note.error ? 'inline-error' : 'muted'} role={note.error ? 'alert' : 'status'}>
          {note.text}
        </p>
      )}
    </>
  );
}
