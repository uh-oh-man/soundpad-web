import { useEffect, useMemo, useRef, useState } from 'react';
import { Music2, Play, LoaderCircle } from 'lucide-react';
import type { Sound, Snapshot } from '../protocol';
import { useWaveformData } from '../waveform-cache';
import { formatTime, wavePath, usePlayhead } from './Waveform';
import { useTimeline } from '../timeline';
export function SoundButton({
  sound,
  state,
  canPlay,
  canSeek,
  pending,
  active,
  play,
  choose,
}: {
  sound: Sound;
  state: Snapshot;
  canPlay: boolean;
  canSeek: boolean;
  index: number;
  pending: boolean;
  active: boolean;
  play: () => Promise<void>;
  choose: (at: number) => Promise<void>;
}) {
  const root = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const { data } = useWaveformData(state.hostId, sound.id, visible && !sound.icon);
  const duration = data?.durationMs ?? sound.durationMs ?? 0;
  const timeline = useTimeline({
    identity: sound.id,
    duration,
    disabled: !canPlay || !canSeek,
    commit: (at) => {
      void choose(at).catch(() => {});
    },
  });
  const path = useMemo(
    () => (data ? wavePath(data, 800, timeline.zoom, timeline.offset) : ''),
    [data, timeline.zoom, timeline.offset],
  );
  const position = usePlayhead(
    active ? state.playback : { status: 'STOPPED', positionMs: 0, durationMs: null },
  );
  const percentage = (value: number) =>
    ((value - timeline.offset) / Math.max(1, timeline.span)) * 100;
  useEffect(() => {
    if (!root.current || visible || sound.icon) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: '180px' },
    );
    observer.observe(root.current);
    return () => observer.disconnect();
  }, [visible, sound.icon]);
  const content = (
    <>
      <span className="tile-visual" aria-hidden="true">
        {sound.icon ? (
          <img src={sound.icon} draggable={false} alt="" />
        ) : path ? (
          <svg viewBox="0 0 800 64" preserveAspectRatio="none">
            <path d={path} />
          </svg>
        ) : (
          <Music2 size={25} />
        )}
      </span>
      <span className="tile-shade" aria-hidden="true" />
      <span className="tile-label">
        <span className="sound-name">{sound.name}</span>
        <span className="sound-hint">
          {pending
            ? 'Preparing audio…'
            : timeline.preview !== null
              ? formatTime(timeline.preview, 3)
              : active
                ? state.playback.status === 'PAUSED'
                  ? 'Paused'
                  : 'Now playing'
                : sound.icon
                  ? 'Soundpad audio'
                  : data?.status === 'loading'
                    ? 'Analyzing on host…'
                    : path
                      ? 'Click waveform to cue'
                      : 'Loading waveform…'}
        </span>
      </span>
    </>
  );
  return (
    <div
      ref={root}
      className={`sound-button ${sound.icon ? 'has-art' : 'no-art'} ${active ? 'is-playing' : ''}`}
      aria-busy={pending}
    >
      {sound.icon ? (
        <button
          className="tile-art-button"
          disabled={!canPlay}
          aria-disabled={pending || undefined}
          aria-label={`Play artwork ${sound.name}`}
          onClick={() => !pending && void play().catch(() => {})}
        >
          {content}
        </button>
      ) : (
        <div
          ref={timeline.surface}
          className={`tile-wave-surface ${path ? 'has-wave' : ''}`}
          role="slider"
          tabIndex={canPlay && canSeek && path ? 0 : -1}
          aria-label={`Cue ${sound.name} waveform`}
          aria-disabled={!canPlay || !canSeek || !path}
          aria-valuemin={0}
          aria-valuemax={duration}
          aria-valuenow={Math.round(timeline.preview ?? timeline.selected)}
          aria-valuetext={formatTime(timeline.preview ?? timeline.selected, 3)}
          title="Click or drag to cue · Scroll to zoom · Shift-drag to pan · 0 to fit"
          {...(path ? timeline.interactions : {})}
        >
          {content}
          {active && percentage(position) >= 0 && percentage(position) <= 100 && (
            <span className="tile-playhead" style={{ left: `${percentage(position)}%` }} />
          )}
          {(timeline.preview !== null || timeline.selected > 0) && (
            <span
              className="tile-cue"
              style={{
                left: `${Math.max(0, Math.min(100, percentage(timeline.preview ?? timeline.selected)))}%`,
              }}
            />
          )}
          {timeline.zoom > 1 && (
            <span className="tile-zoom">{timeline.zoom.toFixed(1)}× · 0 to fit</span>
          )}
        </div>
      )}
      <button
        className="tile-play-strip"
        // The name must contain the visible text so voice control ("click play from start") works.
        aria-label={`Play from start: ${sound.name}`}
        // Busy is aria-disabled, not disabled: Chrome drops focus from a button that becomes disabled.
        disabled={!canPlay}
        aria-disabled={pending || undefined}
        onClick={() => !pending && void play().catch(() => {})}
      >
        {pending ? (
          <LoaderCircle size={13} className="spinning" />
        ) : (
          <Play size={13} fill="currentColor" />
        )}
        <span>PLAY FROM START</span>
        {active && <span className="playing-dot" aria-label="Currently playing" />}
      </button>
    </div>
  );
}
