import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import {
  Activity,
  ArrowDownToLine,
  Expand,
  Headphones,
  Play,
  Scissors,
  Shrink,
  X,
} from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import { supports } from '../version';
import { usePreviewAudio } from '../preview-audio';
import { guestHeld, HELD_BY_HOST, playback } from '../playback';
import {
  ago,
  lastSeconds,
  lineSelection,
  overlaps,
  QUICK_KEYS,
  QUICK_SECONDS,
  transcriptFor,
  type Selection,
} from '../mimic/selection';
import { MimicWave } from './mimic/MimicWave';
import { MimicHostControls } from './mimic/MimicHostControls';
import { MimicRecent } from './mimic/MimicRecent';
import { MimicLibrary } from './mimic/MimicLibrary';
import { MimicHistory } from './mimic/MimicHistory';
import { mimicKeysApply } from './mimic/keys';
import { useFollowNewest } from '../mimic/follow';

const POLL_MS = 500;
const peekSchema = z.object({
  end: z.number().int().nonnegative(),
  rate: z.number().int().positive(),
  windowMs: z.number().int().positive(),
  levels: z.array(z.number().int().min(0).max(1000)).max(2000),
  active: z.boolean(),
  transcript: z
    .array(
      z.object({ startMs: z.number().int(), endMs: z.number().int(), text: z.string().max(2000) }),
    )
    .max(500)
    .nullish(),
  ringId: z.string().max(64).nullish(),
});
export type MimicPeek = z.infer<typeof peekSchema>;

/**
 * The Mimic window: live waveform and transcript of the host's last 30 s, selection, preview and
 * clipping. Host-only controls and the recent list live in ./mimic/.
 */
export function MimicPanel({
  state,
  host,
  connected,
  canPlay,
  onTempRequest,
  full,
  onFull,
}: {
  state: Snapshot;
  host: boolean;
  connected: boolean;
  canPlay: boolean;
  full?: boolean;
  onFull?: () => void;
  /** Opens Temp's Edit copy (2.21) or Keep permanently (2.26) for a clip. */
  onTempRequest?: (kind: 'edit' | 'keep', assetId: string) => void;
}) {
  const [tab, setTab] = useState<'live' | 'clips'>('live');
  const mimic = state.mimic;
  const active = !!mimic?.active;
  const allowed = host || state.permissions.guestMimic === true;
  /** The example session: since 2.43 it has the recording history too, and the intro says what it listens to. */
  const demo = useSyncExternalStore(controller.subscribe, () => controller.getSnapshot().demo);
  // A 2.39 companion keeps a rolling recording history: its own view replaces the live 30 s buffer below.
  const historyMode = supports(state.companionVersion, 'history');
  const recorded = !!mimic?.history?.sessionId;
  const [peek, setPeek] = useState<MimicPeek | null>(null);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const held = guestHeld(state, host);
  const lines = (peek?.transcript ?? []).filter((l) => l.endMs > 0);
  const suggestion = peek && selection ? transcriptFor(lines, selection) : '';
  // The newest line stays in view while the list is at its end (2.46, follow.ts).
  const newest = useFollowNewest<HTMLOListElement>();

  // After a keyboard quick-select, move focus to the name field so Enter mimics.
  const nameField = useRef<HTMLInputElement>(null);
  const [focusName, setFocusName] = useState(false);
  useEffect(() => {
    if (focusName && selection) {
      nameField.current?.focus();
      setFocusName(false);
    }
  }, [focusName, selection]);

  // Live view: refresh while listening and nothing is selected. A selection freezes the view so the
  // chosen moment stays put; the companion keeps extra history so it is still there on "Mimic it".
  const frozen = !!selection;
  useEffect(() => {
    if (!active || !allowed || !connected || frozen || historyMode) return;
    let live = true;
    let timer: ReturnType<typeof setTimeout>;
    const tick = async () => {
      try {
        const next = peekSchema.parse(await controller.peekMimic());
        if (live) setPeek(next);
      } catch {
        /* Keep the last view; the next tick retries. */
      }
      if (live) timer = setTimeout(tick, POLL_MS);
    };
    void tick();
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [active, allowed, connected, frozen, historyMode]);
  useEffect(() => {
    if (!active) {
      setPeek(null);
      setSelection(null);
    }
  }, [active]);

  /** The selection as the companion expects it: frames of the view this person saw. */
  const selected = () =>
    peek &&
    selection && {
      mimicEnd: peek.end,
      positionMs: selection.from,
      endMs: selection.to,
      ...(peek.ringId && { sourceId: peek.ringId }),
    };

  // Host-only private preview: heard on this PC only, never through Soundpad or by guests.
  const { previewing, play: preview, stop: stopPreview } = usePreviewAudio();
  function playPreview() {
    const fields = selected();
    if (fields) void preview('mimicPreview', fields);
  }

  async function clip(play: boolean) {
    const fields = selected();
    if (!fields || busy) return;
    setBusy(true);
    try {
      const named = {
        ...fields,
        ...((name.trim() || suggestion) && { name: name.trim() || suggestion }),
      };
      // Mimic & play goes through the playback path (2.35): the clip plays when it is ready only if
      // nothing newer was asked for meanwhile.
      if (play) await playback.make('mimicClip', named, 'make:mimic');
      else await controller.run('mimicClip', named);
      setSelection(null);
      setName('');
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }

  // Shortcuts work anywhere while the window is open: focus often leaves it when buttons swap
  // (Start/Stop listening). Capture phase runs before the dialog's own Escape handler.
  const shortcut = useRef<(e: KeyboardEvent) => void>(() => {});
  useEffect(() => {
    shortcut.current = (e: KeyboardEvent) => {
      if (!peek || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
      if (!mimicKeysApply(e)) return;
      // Only real text entry (and menus, which use keys) blocks shortcuts; a ticked checkbox must not.
      const typing =
        e.target instanceof HTMLElement &&
        e.target.matches(
          'select, textarea, input:not([type="checkbox"]):not([type="radio"]):not([type="button"])',
        );
      if (e.key === 'Escape' && selection) {
        e.preventDefault(); // clear the selection first; the next Escape closes the window
        e.stopPropagation();
        setSelection(null);
      } else if (
        e.key === 'Enter' &&
        selection &&
        !busy &&
        !(e.target instanceof HTMLButtonElement) &&
        (!typing || (e.target instanceof HTMLInputElement && e.target.type === 'text'))
      ) {
        e.preventDefault();
        if (!e.shiftKey || canPlay) void clip(e.shiftKey);
      } else if (!typing && e.key === 'End') {
        e.preventDefault();
        newest.jump();
      } else if (!typing && QUICK_KEYS[e.key]) {
        e.preventDefault();
        setFocusName(true);
        setSelection(lastSeconds(QUICK_KEYS[e.key], peek.windowMs));
      }
    };
  });
  useEffect(() => {
    const listener = (e: KeyboardEvent) => shortcut.current(e);
    document.addEventListener('keydown', listener, true);
    return () => document.removeEventListener('keydown', listener, true);
  }, []);

  const showHistory = allowed && historyMode && (active || recorded);
  const clips = allowed && (
    <aside className="mimic-page-clips" aria-label="Clips">
      <h2>Recent clips</h2>
      <MimicRecent state={state} host={host} canPlay={canPlay} onTempRequest={onTempRequest} />
      {host && supports(state.companionVersion, 'voiceReplayLibrary') && (
        <MimicLibrary connected={connected} canPlay={canPlay} />
      )}
    </aside>
  );

  return (
    <div id="mimic-page" className="mimic-panel mimic-page" data-tab={tab}>
      <header className="mimic-page-head">
        <Activity className="mimic-page-icon" size={32} aria-hidden="true" />
        <h1 id="mimic-title" tabIndex={-1}>
          Mimic
        </h1>
        <div className={active ? 'mimic-status live' : 'mimic-status'} role="status">
          <i />
          {active ? `Listening to ${mimic?.source ?? 'a source'}` : (mimic?.status ?? 'Off')}
        </div>
        {onFull && (
          <button type="button" className="btn" aria-pressed={!!full} onClick={onFull}>
            {full ? (
              <Shrink size={16} aria-hidden="true" />
            ) : (
              <Expand size={16} aria-hidden="true" />
            )}
            Fill window
          </button>
        )}
      </header>
      {allowed && (
        <div className="mimic-page-tabs" role="group" aria-label="Mimic view">
          <button type="button" aria-pressed={tab === 'live'} onClick={() => setTab('live')}>
            Live transcript
          </button>
          <button type="button" aria-pressed={tab === 'clips'} onClick={() => setTab('clips')}>
            Clips
          </button>
        </div>
      )}
      <div className="mimic-page-live">
        {demo && (
          <p className="mimic-intro">Mimic listens to a loop of Apollo 11 clips in this example.</p>
        )}
        {host && <MimicHostControls state={state} connected={connected} />}
        {!allowed ? (
          <p className="muted">The host has not enabled Mimic for guests.</p>
        ) : showHistory ? (
          <MimicHistory
            state={state}
            host={host}
            connected={connected}
            canPlay={canPlay}
            recentClips={clips}
          />
        ) : !active ? (
          <p className="muted">
            {host ? 'Choose a source and start listening.' : 'Waiting for the host to start Mimic.'}
          </p>
        ) : peek ? (
          <>
            <MimicWave peek={peek} selection={selection} onSelect={setSelection} />
            {mimic?.transcribing && (
              <div className="mimic-transcript-head">
                <span className="muted">
                  {newest.following
                    ? 'New words show as they are said'
                    : 'Newer words are further down'}
                </span>
                <button
                  type="button"
                  className={newest.following ? 'mimic-follow live' : 'mimic-follow primary'}
                  aria-pressed={newest.following}
                  title="Show the newest words and follow them (End)"
                  aria-keyshortcuts="End"
                  onClick={newest.jump}
                >
                  <ArrowDownToLine size={14} aria-hidden="true" />{' '}
                  {newest.following ? 'Following latest' : 'Jump to latest'}
                  <kbd className="mimic-follow-key" aria-hidden="true">
                    End
                  </kbd>
                </button>
              </div>
            )}
            {mimic?.transcribing && (
              <ol
                ref={newest.ref}
                onScroll={newest.onScroll}
                onPointerDown={newest.onPointerDown}
                onPointerUp={newest.onPointerUp}
                onPointerCancel={newest.onPointerUp}
                onPointerLeave={newest.onPointerUp}
                className="mimic-transcript"
                aria-label="Transcript"
              >
                {lines.length ? (
                  lines.map((line) => (
                    <li key={`${line.startMs}-${line.text}`}>
                      <button
                        aria-pressed={!!selection && overlaps(line, selection)}
                        onClick={() => {
                          setSelection(lineSelection(line, peek.windowMs));
                          setName('');
                        }}
                      >
                        <time>{ago(Math.max(0, line.startMs), peek.windowMs)}</time>
                        <span>{line.text}</span>
                      </button>
                    </li>
                  ))
                ) : (
                  <li className="muted">
                    Words will appear here a few seconds after they are said.
                  </li>
                )}
              </ol>
            )}
            <div className="mimic-quick" role="group" aria-label="Quick select">
              {QUICK_SECONDS.map((s) => (
                <button key={s} onClick={() => setSelection(lastSeconds(s, peek.windowMs))}>
                  Last {s} s
                </button>
              ))}
              {selection && (
                <button onClick={() => setSelection(null)}>
                  <X size={14} /> Clear
                </button>
              )}
            </div>
            {selection ? (
              <div className="mimic-actions">
                <span className="mimic-range">
                  {ago(selection.from, peek.windowMs)} → {ago(selection.to, peek.windowMs)} ·{' '}
                  {((selection.to - selection.from) / 1000).toFixed(1)} s
                </span>
                <input
                  ref={nameField}
                  aria-label="Clip name"
                  placeholder={suggestion || 'Name (optional)'}
                  maxLength={100}
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
                {host && supports(state.companionVersion, 'preview') && (
                  <button
                    title="Hear the selection on this PC only; nobody else hears it"
                    onClick={() => (previewing ? stopPreview() : playPreview())}
                  >
                    <Headphones size={15} /> {previewing ? 'Stop preview' : 'Preview'}
                  </button>
                )}
                <button aria-disabled={busy || undefined} onClick={() => !busy && void clip(false)}>
                  <Scissors size={15} /> Mimic it
                </button>
                <button
                  className="primary"
                  disabled={!canPlay}
                  aria-disabled={busy || held || undefined}
                  title={held ? HELD_BY_HOST : undefined}
                  onClick={() => !busy && !held && void clip(true)}
                >
                  <Play size={15} /> Mimic &amp; play
                </button>
              </div>
            ) : (
              <>
                {/* Keyboard help is shown where there is a mouse; touch screens get touch wording (mimic.css). */}
                <p className="muted mimic-help-keys">
                  Drag across the waveform, click a transcript line, or press <kbd>3</kbd>{' '}
                  <kbd>5</kbd> <kbd>0</kbd> for the last 3, 5 or 10 seconds. Then <kbd>Enter</kbd>{' '}
                  mimics, <kbd>Shift</kbd>+<kbd>Enter</kbd> mimics and plays, <kbd>Esc</kbd> clears.
                </p>
                <p className="muted mimic-help-touch">
                  Drag across the waveform, tap a transcript line, or tap Last 3 s, 5 s or 10 s.
                  Then Mimic it.
                </p>
              </>
            )}
          </>
        ) : (
          <p className="muted">Loading the live buffer…</p>
        )}
      </div>
      {!showHistory && clips}
    </div>
  );
}
