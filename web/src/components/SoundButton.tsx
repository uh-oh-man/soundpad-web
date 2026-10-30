import { useEffect, useMemo, useRef, useState } from 'react';
import { Music2 } from 'lucide-react';
import type { Sound, Snapshot } from '../protocol';
import { useWaveformData } from '../waveform-cache';
import { formatTime, wavePath } from './Waveform';

export function SoundButton({
  sound,
  state,
  canPlay,
  canSeek,
  index,
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
  const root = useRef<HTMLButtonElement>(null);
  const [visible, setVisible] = useState(false),
    [at, setAt] = useState<number | null>(null);
  const { data } = useWaveformData(state.hostId, sound.id, visible && !sound.icon);
  const duration = data?.durationMs ?? sound.durationMs ?? 0;
  const path = useMemo(() => (data ? wavePath(data, 800) : ''), [data]);
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
  const scrubbable = !sound.icon && !!path && canSeek;
  function position(clientX: number) {
    const rect = root.current!.getBoundingClientRect();
    return Math.round(
      Math.max(0, Math.min(duration - 1, ((clientX - rect.left) / rect.width) * duration)),
    );
  }
  return (
    <button
      ref={root}
      className={`play-sound ${sound.icon ? 'has-art' : 'no-art'} ${path && !sound.icon ? 'full-wave-button' : ''}`}
      disabled={!canPlay}
      aria-label={`Play ${sound.name}`}
      title={
        scrubbable
          ? `Choose where to play · ${formatTime(at ?? 0)} / ${formatTime(duration)}`
          : sound.name
      }
      onPointerMove={(e) => {
        if (scrubbable) setAt(position(e.clientX));
      }}
      onPointerLeave={() => setAt(null)}
      onKeyDown={(e) => {
        if (scrubbable && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
          e.preventDefault();
          const delta = e.shiftKey ? 1000 : 10;
          setAt(
            e.key === 'Home'
              ? 0
              : e.key === 'End'
                ? duration - 1
                : Math.max(
                    0,
                    Math.min(duration - 1, (at ?? 0) + (e.key === 'ArrowRight' ? delta : -delta)),
                  ),
          );
        }
      }}
      onClick={(e) =>
        void (scrubbable ? choose(e.detail === 0 ? (at ?? 0) : position(e.clientX)) : play()).catch(
          () => {},
        )
      }
    >
      <span className={`sound-icon tone-${index % 5}`} aria-hidden="true">
        {sound.icon ? (
          <img src={sound.icon} alt="" />
        ) : path ? (
          <svg viewBox="0 0 800 64" preserveAspectRatio="none">
            <path d={path} />
          </svg>
        ) : (
          <Music2 size={22} />
        )}
      </span>
      {scrubbable && at !== null && (
        <span className="tile-playhead" style={{ left: `${(at / duration) * 100}%` }} />
      )}
      <span className="sound-label">
        <span className="sound-name">{sound.name}</span>
        <span className="sound-hint">
          {pending
            ? 'PREPARING…'
            : at !== null && scrubbable
              ? `${formatTime(at)} · PLAY HERE`
              : active
                ? state.playback.status === 'PAUSED'
                  ? 'PAUSED'
                  : 'NOW PLAYING'
                : 'PLAY SOUND'}
        </span>
      </span>
    </button>
  );
}
