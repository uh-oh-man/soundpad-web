import { useEffect, useState, useRef } from 'react';
import {
  AudioLines,
  Play,
  Pause,
  Square,
  RotateCcw,
  ChevronUp,
  ChevronDown,
  OctagonX,
} from 'lucide-react';
import { hasSound } from '../protocol';
import type { Snapshot } from '../protocol';
import type { Workspace } from '../workspace';
import { Waveform, formatTime, usePlayhead } from './Waveform';
import { guestHeld, HELD_BY_HOST, intentKey, playback, usePendingIntent } from '../playback';
import { supports } from '../version';
/** Panic's accessible name; its title adds the shortcut. */
export const PANIC_LABEL = 'Panic: stop audio and pause guest playback';
export function Player({
  state,
  host,
  connected,
  disabled,
  canSeek,
  workspace,
  update,
  children,
}: {
  state: Snapshot;
  host: boolean;
  /** Connected to the companion (host) or the host (guest), whether or not Soundpad is available. */
  connected: boolean;
  disabled: boolean;
  canSeek: boolean;
  workspace: Workspace | null;
  update: (fn: (w: Workspace) => Workspace) => void;
  children: React.ReactNode;
}) {
  const root = useRef<HTMLElement>(null);
  useEffect(() => {
    const element = root.current;
    if (!element) return;
    const resize = new ResizeObserver(() =>
      document.documentElement.style.setProperty(
        '--player-height',
        `${element.getBoundingClientRect().height}px`,
      ),
    );
    resize.observe(element);
    return () => {
      resize.disconnect();
      document.documentElement.style.removeProperty('--player-height');
    };
  }, []);
  const [expanded, setExpanded] = useState(false);
  const [lastId, setLastId] = useState<string | null>(null);
  useEffect(() => {
    if (state.playback.soundId) setLastId(state.playback.soundId);
  }, [state.playback.soundId]);
  const current = state.playback,
    known = !!current.soundId,
    sound = state.library.sounds.find((s) => s.id === (current.soundId ?? lastId)),
    position = usePlayhead(current);
  // A click this page sent and the companion has not answered yet (2.35). "Playing" is the report.
  const pending = usePendingIntent();
  const held = guestHeld(state, host);
  const canPanic = host && supports(state.companionVersion, 'intents');
  function replay() {
    const cue = workspace?.lastCue;
    if (!cue) return;
    const target = state.library.sounds.find((s) => hasSound([cue.soundId], s));
    if (!target) return;
    void (cue.end
      ? playback.region(target.id, cue.at, cue.end, cue.repeat)
      : playback.startAt(target.id, cue.at));
  }
  return (
    <footer
      ref={root}
      className={`player player-v2 ${expanded ? 'expanded' : ''} ${pending ? 'intent-pending' : ''}`}
      data-intent={pending ? 'pending' : undefined}
    >
      <div className="player-track" aria-busy={!!pending}>
        <span className="playback-icon">
          {sound?.icon ? <img src={sound.icon} alt="" /> : <AudioLines size={22} />}
        </span>
        <div>
          <strong>
            {sound
              ? known
                ? sound.name
                : `Last selection: ${sound.name}`
              : current.status === 'PLAYING' || current.status === 'PAUSED'
                ? 'Soundpad playback · sound unknown'
                : current.status === 'STOPPED'
                  ? 'Ready to make some noise'
                  : 'Playback status unavailable'}
          </strong>
          <small>
            {sound && known
              ? `Requested by ${current.requester ?? 'Host'} · ${current.status.toLowerCase()}`
              : 'Direct Soundpad playback remains independent.'}
          </small>
        </div>
        <button
          className="mobile-expand icon-button"
          aria-label="Expand player waveform"
          aria-expanded={expanded}
          onClick={() => setExpanded((e) => !e)}
        >
          {expanded ? <ChevronDown size={18} /> : <ChevronUp size={18} />}
        </button>
      </div>
      <div className="player-wave">
        {sound ? (
          <Waveform
            compact
            sound={sound}
            state={state}
            disabled={disabled || !canSeek || !known || !current.epoch}
            held={held}
            workspace={workspace}
            update={update}
            choose={(at) => playback.seek(at)}
          />
        ) : (
          <div className="unknown-progress">
            {current.durationMs ? (
              <progress aria-label="Playback progress" max={current.durationMs} value={position} />
            ) : (
              <span>Play a sound to see its waveform</span>
            )}
            <small>
              {formatTime(current.positionMs)} / {formatTime(current.durationMs)}
            </small>
          </div>
        )}
      </div>
      <div className="transport">
        <button
          className="icon-button"
          aria-label={current.status === 'PAUSED' ? 'Resume playback' : 'Pause playback'}
          disabled={disabled || !canSeek || !sound || !current.epoch}
          // Held by the host's Panic: aria-disabled keeps focus, and the press is ignored.
          aria-disabled={held || undefined}
          title={held ? HELD_BY_HOST : undefined}
          onClick={() => !held && void playback.pause()}
        >
          {current.status === 'PAUSED' ? <Play size={18} /> : <Pause size={18} />}
        </button>
        <button
          className="stop"
          disabled={disabled}
          aria-disabled={held || undefined}
          title={held ? HELD_BY_HOST : undefined}
          onClick={() => !held && void playback.stop()}
        >
          <Square size={15} fill="currentColor" /> Stop
        </button>
        {canPanic && (
          // Apart from Stop on purpose (2.35): Stop ends one sound, Panic also holds guest playback.
          <button
            className="panic"
            aria-label={PANIC_LABEL}
            title={`${PANIC_LABEL} (Shift+Escape)`}
            disabled={!connected}
            aria-disabled={pending === intentKey.panic || undefined}
            onClick={() => pending !== intentKey.panic && void playback.panic()}
          >
            <OctagonX size={15} aria-hidden="true" /> <span>Panic</span>
          </button>
        )}
        <button
          className="icon-button"
          aria-label="Replay last cue"
          title={held ? HELD_BY_HOST : 'Replay last cue'}
          aria-disabled={held || undefined}
          disabled={
            disabled ||
            !canSeek ||
            !workspace?.lastCue ||
            !state.library.sounds.some((s) => hasSound([workspace.lastCue!.soundId], s))
          }
          onClick={() => !held && replay()}
        >
          <RotateCcw size={17} />
        </button>
      </div>
      {children}
    </footer>
  );
}
