import { useState } from 'react';
import { AudioLines, Play, Pause, Square, RotateCcw, ChevronUp, ChevronDown } from 'lucide-react';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';
import type { Workspace } from '../workspace';
import { Waveform, formatTime, usePlayhead } from './Waveform';
export function Player({
  state,
  disabled,
  canSeek,
  workspace,
  update,
  children,
}: {
  state: Snapshot;
  disabled: boolean;
  canSeek: boolean;
  workspace: Workspace | null;
  update: (fn: (w: Workspace) => Workspace) => void;
  children: React.ReactNode;
}) {
  const [expanded, setExpanded] = useState(false);
  const playback = state.playback,
    sound = state.library.sounds.find((s) => s.id === playback.soundId),
    position = usePlayhead(playback);
  async function action(op: 'pause' | 'seek' | 'stop', at?: number) {
    try {
      await controller.run(op, {
        soundId: playback.soundId ?? undefined,
        epoch: playback.epoch ?? undefined,
        positionMs: at,
      });
    } catch {
      /* Controller reports failure. */
    }
  }
  async function replay() {
    const cue = workspace?.lastCue;
    if (!cue) return;
    try {
      await controller.run(cue.end ? 'region' : 'startAt', {
        soundId: cue.soundId,
        libraryVersion: state.library.version,
        positionMs: cue.at,
        endMs: cue.end,
        repeat: cue.repeat,
      });
    } catch {
      /* The controller reports command errors; browser storage is optional for display names. */
    }
  }
  return (
    <footer className={`player player-v2 ${expanded ? 'expanded' : ''}`}>
      <div className="player-track">
        <span className="playback-icon">
          {sound?.icon ? <img src={sound.icon} alt="" /> : <AudioLines size={22} />}
        </span>
        <div>
          <strong>
            {sound
              ? sound.name
              : playback.status === 'PLAYING' || playback.status === 'PAUSED'
                ? 'Soundpad playback · sound unknown'
                : playback.status === 'STOPPED'
                  ? 'Ready to make some noise'
                  : 'Playback status unavailable'}
          </strong>
          <small>
            {sound
              ? `Requested by ${playback.requester ?? 'Host'} · ${playback.status.toLowerCase()}`
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
            disabled={disabled || !canSeek}
            workspace={workspace}
            update={update}
            choose={(at) => action('seek', at)}
          />
        ) : (
          <div className="unknown-progress">
            {playback.durationMs ? (
              <progress aria-label="Playback progress" max={playback.durationMs} value={position} />
            ) : (
              <span>Play a sound to see its waveform</span>
            )}
            <small>
              {formatTime(playback.positionMs)} / {formatTime(playback.durationMs)}
            </small>
          </div>
        )}
      </div>
      <div className="transport">
        <button
          className="icon-button"
          aria-label={playback.status === 'PAUSED' ? 'Resume playback' : 'Pause playback'}
          disabled={disabled || !canSeek || !sound || !playback.epoch}
          onClick={() => void action('pause')}
        >
          {playback.status === 'PAUSED' ? <Play size={18} /> : <Pause size={18} />}
        </button>
        <button className="stop" disabled={disabled} onClick={() => void action('stop')}>
          <Square size={15} fill="currentColor" /> Stop
        </button>
        <button
          className="icon-button"
          aria-label="Replay last cue"
          title="Replay last cue"
          disabled={
            disabled ||
            !canSeek ||
            !workspace?.lastCue ||
            !state.library.sounds.some((s) => s.id === workspace.lastCue?.soundId)
          }
          onClick={() => void replay()}
        >
          <RotateCcw size={17} />
        </button>
      </div>
      {children}
    </footer>
  );
}
