import { useEffect, useMemo, useRef, useState } from 'react';
import { Plus, Minus, RotateCcw, Flag, Scissors, Play, Trash2 } from 'lucide-react';
import { useWaveformData, clearWaveforms } from '../waveform-cache';
import { controller } from '../controller';
import type { Snapshot, Sound, Waveform as WaveData } from '../protocol';
import type { Workspace } from '../workspace';
export const formatTime = (ms: number | null | undefined) =>
  ms == null ? '—' : `${Math.floor(ms / 60000)}:${((ms / 1000) % 60).toFixed(1).padStart(4, '0')}`;
export function usePlayhead(playback: Snapshot['playback']) {
  const [position, setPosition] = useState(playback.positionMs ?? 0);
  const base = useRef({
    position: playback.positionMs ?? 0,
    at: performance.now(),
    epoch: playback.epoch,
    status: playback.status,
    positionRevision: playback.positionRevision,
  });
  useEffect(() => {
    const now = performance.now(),
      previous = base.current;
    const projected = previous.position + (previous.status === 'PLAYING' ? now - previous.at : 0),
      sample = playback.positionMs ?? 0;
    const next =
      previous.epoch === playback.epoch &&
      previous.positionRevision === playback.positionRevision &&
      playback.status === 'PLAYING' &&
      Math.abs(sample - projected) < 350
        ? Math.max(sample, projected)
        : sample;
    base.current = {
      position: next,
      at: now,
      epoch: playback.epoch,
      status: playback.status,
      positionRevision: playback.positionRevision,
    };
    let frame = 0,
      timer: ReturnType<typeof setTimeout>;
    function update() {
      const value = Math.min(
        playback.durationMs ?? Infinity,
        base.current.position +
          (playback.status === 'PLAYING' && !document.hidden
            ? performance.now() - base.current.at
            : 0),
      );
      setPosition(Math.max(0, value));
      if (playback.status === 'PLAYING') {
        if (matchMedia('(prefers-reduced-motion: reduce)').matches) timer = setTimeout(update, 200);
        else frame = requestAnimationFrame(update);
      }
    }
    update();
    return () => {
      cancelAnimationFrame(frame);
      clearTimeout(timer);
    };
  }, [
    playback.positionMs,
    playback.sampledAt,
    playback.status,
    playback.epoch,
    playback.durationMs,
    playback.positionRevision,
  ]);
  return position;
}
export function wavePath(
  data: Pick<WaveData, 'status' | 'durationMs'> & {
    levels?: { bins: number; peaks: ArrayLike<number> }[];
  },
  width = 800,
  zoom = 1,
  offset = 0,
) {
  if (data.status !== 'ready' || !data.levels || !data.durationMs) return '';
  const wanted = width * zoom;
  const level = data.levels.find((l) => l.bins >= wanted) ?? data.levels[data.levels.length - 1];
  const span = data.durationMs / zoom;
  let path = '';
  for (let x = 0; x < width; x++) {
    let min = 32767,
      max = -32767;
    const a = Math.floor(((offset + (x / width) * span) / data.durationMs) * level.bins),
      b = Math.ceil(((offset + ((x + 1) / width) * span) / data.durationMs) * level.bins);
    for (let i = Math.max(0, a); i < Math.min(level.bins, b); i++) {
      min = Math.min(min, level.peaks[i * 2]);
      max = Math.max(max, level.peaks[i * 2 + 1]);
    }
    if (max >= min) path += `M${x},${32 - (max / 32767) * 29}V${32 - (min / 32767) * 29}`;
  }
  return path;
}
type Props = {
  sound: Sound;
  state: Snapshot;
  disabled: boolean;
  workspace: Workspace | null;
  update: (fn: (w: Workspace) => Workspace) => void;
  choose: (at: number) => Promise<void>;
  compact?: boolean;
};
export function Waveform({
  sound,
  state,
  disabled,
  workspace,
  update,
  choose,
  compact = false,
}: Props) {
  const root = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(compact),
    [selected, setSelected] = useState(0),
    [preview, setPreview] = useState<number | null>(null),
    [offset, setOffset] = useState(0),
    [tools, setTools] = useState(false),
    [cueName, setCueName] = useState('Cue'),
    [regionName, setRegionName] = useState('Region'),
    [start, setStart] = useState(0),
    [end, setEnd] = useState(1000),
    [repeat, setRepeat] = useState(false),
    [selectRegion, setSelectRegion] = useState(false);
  const zoom = workspace?.zoom[sound.id] ?? 1;
  const active = state.playback.soundId === sound.id && state.playback.identitySource === 'web';
  const position = usePlayhead(
    active ? state.playback : { status: 'STOPPED', positionMs: 0, durationMs: null },
  );
  useEffect(() => {
    const node = root.current;
    if (!node || compact || visible) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: '180px' },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [compact, visible]);
  const { data, error } = useWaveformData(
    state.hostId,
    sound.id,
    visible && (state.protocol ?? 1) >= 2,
  );
  const duration = data?.durationMs ?? sound.durationMs ?? 0,
    span = duration / zoom,
    maxOffset = Math.max(0, duration - span),
    viewOffset = Math.min(offset, maxOffset);
  const path = useMemo(
    () => (data ? wavePath(data, 800, zoom, viewOffset) : ''),
    [data, zoom, viewOffset],
  );
  const cues = workspace?.cues[sound.id] ?? [],
    regions = workspace?.regions[sound.id] ?? [];
  const timeAt = (x: number) => {
    const rect = root.current!.querySelector('.wave-surface')!.getBoundingClientRect();
    return Math.round(
      Math.max(0, Math.min(duration, viewOffset + ((x - rect.left) / rect.width) * span)),
    );
  };
  async function pick(at: number) {
    setSelected(at);
    if (workspace) update((w) => ({ ...w, lastCue: { soundId: sound.id, at } }));
    try {
      await choose(at);
    } catch {
      /* Controller reports failure. */
    }
  }
  const percentage = (at: number) => ((at - viewOffset) / Math.max(1, span)) * 100;
  return (
    <div className={`wave-block ${compact ? 'compact' : ''}`} ref={root}>
      {data?.status === 'ready' ? (
        <>
          <div
            className="wave-surface"
            role="slider"
            tabIndex={disabled ? -1 : 0}
            aria-label={`Seek ${sound.name}`}
            aria-valuemin={0}
            aria-valuemax={duration}
            aria-valuenow={Math.round(preview ?? (active ? position : selected))}
            aria-valuetext={formatTime(preview ?? (active ? position : selected))}
            aria-disabled={disabled}
            onPointerDown={(e) => {
              if (disabled) return;
              e.currentTarget.setPointerCapture(e.pointerId);
              if (selectRegion) setStart(timeAt(e.clientX));
              setPreview(timeAt(e.clientX));
            }}
            onPointerMove={(e) => {
              if (e.currentTarget.hasPointerCapture(e.pointerId)) setPreview(timeAt(e.clientX));
            }}
            onPointerUp={(e) => {
              if (disabled || !e.currentTarget.hasPointerCapture(e.pointerId)) return;
              e.currentTarget.releasePointerCapture(e.pointerId);
              const at = timeAt(e.clientX);
              setPreview(null);
              if (selectRegion) {
                setStart(Math.min(start, at));
                setEnd(Math.max(start, at));
                setTools(true);
                return;
              }
              void pick(at);
            }}
            onPointerCancel={() => setPreview(null)}
            onKeyDown={(e) => {
              if (disabled) return;
              let at = selected;
              if (e.key === 'ArrowRight') at += e.shiftKey ? 1000 : 100;
              else if (e.key === 'ArrowLeft') at -= e.shiftKey ? 1000 : 100;
              else if (e.key === 'Home') at = 0;
              else if (e.key === 'End') at = duration - 1;
              else if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                void pick(selected);
                return;
              } else return;
              e.preventDefault();
              setSelected(Math.round(Math.max(0, Math.min(duration - 1, at))));
            }}
          >
            <svg viewBox="0 0 800 64" preserveAspectRatio="none" aria-hidden="true">
              <path d={path} />
            </svg>
            {regions
              .filter((r) => r.end > viewOffset && r.start < viewOffset + span)
              .map((r) => (
                <span
                  key={r.id}
                  className="wave-region"
                  title={r.name}
                  style={{
                    left: `${Math.max(0, percentage(r.start))}%`,
                    width: `${Math.min(100, percentage(r.end)) - Math.max(0, percentage(r.start))}%`,
                  }}
                />
              ))}
            {active && percentage(position) >= 0 && percentage(position) <= 100 && (
              <span className="wave-playhead" style={{ left: `${percentage(position)}%` }} />
            )}
            {(preview !== null || selected > 0) && (
              <span
                className="wave-selection"
                style={{ left: `${Math.max(0, Math.min(100, percentage(preview ?? selected)))}%` }}
              />
            )}
            {cues
              .filter((c) => percentage(c.at) >= 0 && percentage(c.at) <= 100)
              .map((c) => (
                <button
                  key={c.id}
                  className="cue-marker"
                  title={`${c.name} · ${formatTime(c.at)}`}
                  aria-label={`Cue ${c.name}`}
                  style={{ left: `${percentage(c.at)}%` }}
                  onPointerDown={(e) => {
                    e.stopPropagation();
                    e.currentTarget.setPointerCapture(e.pointerId);
                  }}
                  onPointerMove={(e) => {
                    if (e.currentTarget.hasPointerCapture(e.pointerId)) {
                      e.stopPropagation();
                      setPreview(timeAt(e.clientX));
                    }
                  }}
                  onPointerUp={(e) => {
                    e.stopPropagation();
                    if (preview !== null) {
                      const at = timeAt(e.clientX);
                      update((w) => ({
                        ...w,
                        cues: {
                          ...w.cues,
                          [sound.id]: (w.cues[sound.id] ?? []).map((x) =>
                            x.id === c.id ? { ...x, at } : x,
                          ),
                        },
                      }));
                      setPreview(null);
                    } else void pick(c.at);
                    e.currentTarget.releasePointerCapture(e.pointerId);
                  }}
                  onClick={(e) => e.stopPropagation()}
                  onKeyDown={(e) => {
                    e.stopPropagation();
                    if (e.key === 'Enter') void pick(c.at);
                  }}
                >
                  <Flag size={11} />
                </button>
              ))}
          </div>
          <div className="wave-caption">
            <span>{formatTime(viewOffset)}</span>
            <span>
              {preview !== null
                ? formatTime(preview)
                : active
                  ? `${formatTime(position)} / ${formatTime(duration)}`
                  : formatTime(selected)}
              {!compact && ' · Click or drag to seek'}
            </span>
            <span>{formatTime(viewOffset + span)}</span>
          </div>
          {!compact && (
            <div className="wave-controls">
              <button
                aria-label={`Zoom out ${sound.name}`}
                disabled={zoom <= 1}
                onClick={() =>
                  update((w) => ({ ...w, zoom: { ...w.zoom, [sound.id]: Math.max(1, zoom / 2) } }))
                }
              >
                <Minus size={14} />
              </button>
              <span>{zoom}×</span>
              <button
                aria-label={`Zoom in ${sound.name}`}
                disabled={zoom >= 32}
                onClick={() =>
                  update((w) => ({ ...w, zoom: { ...w.zoom, [sound.id]: Math.min(32, zoom * 2) } }))
                }
              >
                <Plus size={14} />
              </button>
              <button
                onClick={() => {
                  setOffset(0);
                  update((w) => ({ ...w, zoom: { ...w.zoom, [sound.id]: 1 } }));
                }}
              >
                <RotateCcw size={13} /> Fit
              </button>
              <button onClick={() => setTools((t) => !t)} aria-expanded={tools}>
                <Flag size={13} /> Cues & regions
              </button>
              <button
                aria-pressed={selectRegion}
                onClick={() => setSelectRegion((value) => !value)}
              >
                Select region
              </button>
              {zoom > 1 && (
                <input
                  type="range"
                  aria-label={`Waveform offset ${sound.name}`}
                  min={0}
                  max={maxOffset}
                  value={viewOffset}
                  onChange={(e) => setOffset(+e.target.value)}
                />
              )}
            </div>
          )}
          {tools && !compact && (
            <div className="wave-tools">
              <div className="cue-create">
                <input
                  aria-label="New cue name"
                  value={cueName}
                  maxLength={80}
                  onChange={(e) => setCueName(e.target.value)}
                />
                <label>
                  Time (s)
                  <input
                    type="number"
                    aria-label="Cue time seconds"
                    min={0}
                    max={duration / 1000}
                    step="0.1"
                    value={selected / 1000}
                    onChange={(e) => setSelected(Math.round(Math.max(0, +e.target.value * 1000)))}
                  />
                </label>
                <button
                  disabled={!workspace || cues.length >= 64}
                  onClick={() =>
                    update((w) => ({
                      ...w,
                      cues: {
                        ...w.cues,
                        [sound.id]: [
                          ...(w.cues[sound.id] ?? []),
                          {
                            id: crypto.randomUUID(),
                            name: cueName.trim() || 'Cue',
                            at: Math.min(selected, duration - 1),
                          },
                        ],
                      },
                    }))
                  }
                >
                  <Plus size={14} /> Add cue
                </button>
              </div>
              {cues.map((c) => (
                <div className="cue-edit" key={c.id}>
                  <button
                    disabled={disabled}
                    onClick={() => void pick(c.at)}
                    aria-label={`Play cue ${c.name}`}
                  >
                    <Play size={13} />
                  </button>
                  <input
                    aria-label={`Rename cue ${c.name}`}
                    defaultValue={c.name}
                    maxLength={80}
                    onBlur={(e) =>
                      update((w) => ({
                        ...w,
                        cues: {
                          ...w.cues,
                          [sound.id]: (w.cues[sound.id] ?? []).map((x) =>
                            x.id === c.id ? { ...x, name: e.target.value.trim() || 'Cue' } : x,
                          ),
                        },
                      }))
                    }
                  />
                  <input
                    type="number"
                    aria-label={`Move cue ${c.name} seconds`}
                    min={0}
                    max={duration / 1000}
                    step="0.1"
                    value={c.at / 1000}
                    onChange={(e) => {
                      const at = Math.round(
                        Math.max(0, Math.min(duration - 1, +e.target.value * 1000)),
                      );
                      update((w) => ({
                        ...w,
                        cues: {
                          ...w.cues,
                          [sound.id]: (w.cues[sound.id] ?? []).map((x) =>
                            x.id === c.id ? { ...x, at } : x,
                          ),
                        },
                      }));
                    }}
                  />
                  <button
                    aria-label={`Delete cue ${c.name}`}
                    onClick={() =>
                      update((w) => ({
                        ...w,
                        cues: {
                          ...w.cues,
                          [sound.id]: (w.cues[sound.id] ?? []).filter((x) => x.id !== c.id),
                        },
                      }))
                    }
                  >
                    <Trash2 size={13} />
                  </button>
                </div>
              ))}
              <div className="region-create">
                <input
                  aria-label="Region name"
                  value={regionName}
                  maxLength={80}
                  onChange={(e) => setRegionName(e.target.value)}
                />
                <label>
                  Start (s)
                  <input
                    type="number"
                    aria-label="Region start seconds"
                    min={0}
                    max={duration / 1000}
                    step="0.1"
                    value={start / 1000}
                    onChange={(e) => setStart(Math.max(0, Math.round(+e.target.value * 1000)))}
                  />
                </label>
                <button onClick={() => setStart(selected)}>Use selection as start</button>
                <label>
                  End (s)
                  <input
                    type="number"
                    aria-label="Region end seconds"
                    min={0}
                    max={duration / 1000}
                    step="0.1"
                    value={end / 1000}
                    onChange={(e) => setEnd(Math.round(+e.target.value * 1000))}
                  />
                </label>
                <button onClick={() => setEnd(selected)}>Use selection as end</button>
                <label>
                  <input
                    type="checkbox"
                    checked={repeat}
                    onChange={(e) => setRepeat(e.target.checked)}
                  />{' '}
                  Repeat
                </label>
                <button
                  disabled={!workspace || end <= start || end > duration || regions.length >= 64}
                  onClick={() =>
                    update((w) => ({
                      ...w,
                      regions: {
                        ...w.regions,
                        [sound.id]: [
                          ...(w.regions[sound.id] ?? []),
                          {
                            id: crypto.randomUUID(),
                            name: regionName.trim() || 'Region',
                            start,
                            end,
                            repeat,
                          },
                        ],
                      },
                    }))
                  }
                >
                  <Scissors size={13} /> Save region
                </button>
              </div>
              {regions.map((r) => (
                <div className="region-edit" key={r.id}>
                  <button
                    disabled={disabled}
                    aria-label={`Play region ${r.name}`}
                    onClick={() => {
                      update((w) => ({
                        ...w,
                        lastCue: { soundId: sound.id, at: r.start, end: r.end, repeat: r.repeat },
                      }));
                      void controller
                        .run('region', {
                          soundId: sound.id,
                          libraryVersion: state.library.version,
                          positionMs: r.start,
                          endMs: r.end,
                          repeat: r.repeat,
                        })
                        .catch(() => {});
                    }}
                  >
                    <Play size={13} />
                  </button>
                  <input
                    aria-label={`Rename region ${r.name}`}
                    defaultValue={r.name}
                    maxLength={80}
                    onBlur={(e) =>
                      update((w) => ({
                        ...w,
                        regions: {
                          ...w.regions,
                          [sound.id]: (w.regions[sound.id] ?? []).map((x) =>
                            x.id === r.id ? { ...x, name: e.target.value.trim() || 'Region' } : x,
                          ),
                        },
                      }))
                    }
                  />
                  <input
                    type="number"
                    aria-label={`Region ${r.name} start seconds`}
                    min={0}
                    max={r.end / 1000}
                    step="0.1"
                    value={r.start / 1000}
                    onChange={(e) => {
                      const value = Math.min(
                        r.end - 1,
                        Math.max(0, Math.round(+e.target.value * 1000)),
                      );
                      update((w) => ({
                        ...w,
                        regions: {
                          ...w.regions,
                          [sound.id]: (w.regions[sound.id] ?? []).map((x) =>
                            x.id === r.id ? { ...x, start: value } : x,
                          ),
                        },
                      }));
                    }}
                  />
                  <input
                    type="number"
                    aria-label={`Region ${r.name} end seconds`}
                    min={r.start / 1000}
                    max={duration / 1000}
                    step="0.1"
                    value={r.end / 1000}
                    onChange={(e) => {
                      const value = Math.min(
                        duration,
                        Math.max(r.start + 1, Math.round(+e.target.value * 1000)),
                      );
                      update((w) => ({
                        ...w,
                        regions: {
                          ...w.regions,
                          [sound.id]: (w.regions[sound.id] ?? []).map((x) =>
                            x.id === r.id ? { ...x, end: value } : x,
                          ),
                        },
                      }));
                    }}
                  />
                  <label>
                    <input
                      type="checkbox"
                      aria-label={`Repeat region ${r.name}`}
                      checked={r.repeat}
                      onChange={(e) => {
                        const repeat = e.target.checked;
                        update((w) => ({
                          ...w,
                          regions: {
                            ...w.regions,
                            [sound.id]: (w.regions[sound.id] ?? []).map((x) =>
                              x.id === r.id ? { ...x, repeat } : x,
                            ),
                          },
                        }));
                      }}
                    />
                    Repeat
                  </label>
                  <button
                    aria-label={`Delete region ${r.name}`}
                    onClick={() =>
                      update((w) => ({
                        ...w,
                        regions: {
                          ...w.regions,
                          [sound.id]: (w.regions[sound.id] ?? []).filter((x) => x.id !== r.id),
                        },
                      }))
                    }
                  >
                    <Trash2 size={13} />
                  </button>
                </div>
              ))}
              <p className="muted">
                Cues and regions stay in your browser. Soundpad starts use play then seek; opening
                fragments and region boundaries are not sample-accurate.
              </p>
            </div>
          )}
        </>
      ) : (
        <div className="wave-unavailable">
          <span>
            {error ||
              data?.error ||
              ((state.protocol ?? 1) < 2
                ? 'Waveforms require the V2 companion.'
                : visible
                  ? 'Analyzing audio…'
                  : 'Waveform loads when visible.')}
          </span>
          {(error || data?.status === 'unavailable') && (
            <button
              onClick={() => {
                clearWaveforms(state.hostId ?? undefined, sound.id);
              }}
            >
              Retry waveform
            </button>
          )}
        </div>
      )}
    </div>
  );
}
