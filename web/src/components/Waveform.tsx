import { useEffect, useMemo, useRef, useState } from 'react';
import { Plus, RotateCcw, Flag, Scissors, Play, Trash2, HelpCircle } from 'lucide-react';
import { useWaveformData, clearWaveforms } from '../waveform-cache';
import { controller } from '../controller';
import type { Snapshot, Sound, Waveform as WaveData } from '../protocol';
import { useTimeline } from '../timeline';
import type { Workspace } from '../workspace';
export const formatTime = (ms: number | null | undefined, precision = 1) =>
  ms == null
    ? '—'
    : `${Math.floor(ms / 60000)}:${((ms / 1000) % 60).toFixed(precision).padStart(precision + 3, '0')}`;
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
  data: Pick<WaveData, 'status' | 'durationMs' | 'analyzedMs'> & {
    levels?: { bins: number; peaks: ArrayLike<number> }[];
  },
  width = 800,
  zoom = 1,
  offset = 0,
) {
  if (!['ready', 'loading'].includes(data.status) || !data.levels || !data.durationMs) return '';
  const wanted = width * zoom;
  const level = data.levels.find((l) => l.bins >= wanted) ?? data.levels[data.levels.length - 1];
  const span = data.durationMs / zoom;
  const analyzed =
    data.status === 'loading' ? (data.analyzedMs ?? data.durationMs) : data.durationMs;
  let path = '';
  for (let x = 0; x < width; x++) {
    let min = 32767,
      max = -32767;
    const a = Math.floor(((offset + (x / width) * span) / analyzed) * level.bins),
      b = Math.ceil(((offset + ((x + 1) / width) * span) / analyzed) * level.bins);
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
    [tools, setTools] = useState(false),
    [cueName, setCueName] = useState('Cue'),
    [regionName, setRegionName] = useState('Region'),
    [start, setStart] = useState(0),
    [end, setEnd] = useState(1000),
    [repeat, setRepeat] = useState(false),
    [selectRegion, setSelectRegion] = useState(false);
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
  const metadataId = sound.workspaceId ?? sound.id;
  const duration = data?.durationMs ?? sound.durationMs ?? 0;
  const timeline = useTimeline({
    identity: sound.id,
    duration,
    initialZoom: workspace?.zoom[metadataId] ?? 1,
    disabled,
    commit: (at) => {
      void pick(at);
    },
    range: selectRegion
      ? (a, b) => {
          setStart(a);
          setEnd(b);
          setTools(true);
        }
      : undefined,
    persistZoom: (zoom) => {
      if (workspace && workspace.zoom[metadataId] !== zoom)
        update((w) => ({ ...w, zoom: { ...w.zoom, [metadataId]: zoom } }));
    },
  });
  const { zoom, offset: viewOffset, span, selected, setSelected, preview, timeAt } = timeline;
  const path = useMemo(
    () => (data ? wavePath(data, 800, zoom, viewOffset) : ''),
    [data, zoom, viewOffset],
  );
  const cues = workspace?.cues[metadataId] ?? [],
    regions = workspace?.regions[metadataId] ?? [];
  async function pick(at: number) {
    setSelected(at);
    if (workspace) update((w) => ({ ...w, lastCue: { soundId: metadataId, at } }));
    try {
      await choose(at);
    } catch {
      /* Controller reports failure. */
    }
  }
  const percentage = (at: number) => ((at - viewOffset) / Math.max(1, span)) * 100;
  return (
    <div className={`wave-block ${compact ? 'compact' : ''}`} ref={root}>
      {data?.levels && data.durationMs ? (
        <>
          <div
            className="wave-surface"
            role="slider"
            tabIndex={disabled ? -1 : 0}
            aria-label={`Seek ${sound.name}`}
            aria-valuemin={0}
            aria-valuemax={duration}
            aria-valuenow={Math.round(preview ?? (active ? position : selected))}
            aria-valuetext={formatTime(preview ?? (active ? position : selected), 3)}
            aria-disabled={disabled}
            ref={timeline.surface}
            {...timeline.interactions}
            title="Scroll to zoom · Shift-drag or middle-drag to pan · Pinch on touch · +/- zoom, Alt+arrows pan, 0 fit"
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
                      timeline.setPreview(timeAt(e.clientX));
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
                          [metadataId]: (w.cues[metadataId] ?? []).map((x) =>
                            x.id === c.id ? { ...x, at } : x,
                          ),
                        },
                      }));
                      timeline.setPreview(null);
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
            {data.status === 'loading' && <small>Analyzing on host…</small>}
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
          <div className="wave-navigation">
            <span className="wave-gesture-hint">
              <HelpCircle size={12} />
              <span>Scroll to zoom · Shift-drag to pan</span>
            </span>
            {zoom > 1 && (
              <button
                className="wave-fit"
                aria-label={`Fit waveform ${sound.name}`}
                onClick={timeline.fit}
              >
                <RotateCcw size={12} /> Fit <small>{zoom.toFixed(1)}×</small>
              </button>
            )}
            <button
              className="wave-advanced"
              onClick={() => setTools((t) => !t)}
              aria-expanded={tools}
            >
              <Flag size={12} /> {compact ? 'Edit' : 'Cues & regions'}
            </button>
          </div>
          {tools && (
            <div className="wave-tools">
              <div className="wave-edit-actions">
                <button
                  aria-pressed={selectRegion}
                  onClick={() => setSelectRegion((value) => !value)}
                >
                  Select region by dragging
                </button>
                <p className="muted">
                  Arrow keys select 10 ms; Shift selects 1 s. Enter seeks. + / − zoom, Alt + arrows
                  pan, 0 fits. Pinch to zoom on touch.
                </p>
              </div>
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
                    step="0.001"
                    value={selected / 1000}
                    onChange={(e) => setSelected(Math.round(Math.max(0, +e.target.value * 1000)))}
                  />
                </label>
                <button
                  disabled={disabled}
                  aria-label={`Seek selected position ${sound.name}`}
                  onClick={() => void pick(selected)}
                >
                  Seek here
                </button>
                <button
                  disabled={!workspace || cues.length >= 64}
                  onClick={() =>
                    update((w) => ({
                      ...w,
                      cues: {
                        ...w.cues,
                        [metadataId]: [
                          ...(w.cues[metadataId] ?? []),
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
                          [metadataId]: (w.cues[metadataId] ?? []).map((x) =>
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
                          [metadataId]: (w.cues[metadataId] ?? []).map((x) =>
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
                          [metadataId]: (w.cues[metadataId] ?? []).filter((x) => x.id !== c.id),
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
                        [metadataId]: [
                          ...(w.regions[metadataId] ?? []),
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
                        lastCue: { soundId: metadataId, at: r.start, end: r.end, repeat: r.repeat },
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
                          [metadataId]: (w.regions[metadataId] ?? []).map((x) =>
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
                          [metadataId]: (w.regions[metadataId] ?? []).map((x) =>
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
                          [metadataId]: (w.regions[metadataId] ?? []).map((x) =>
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
                            [metadataId]: (w.regions[metadataId] ?? []).map((x) =>
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
                          [metadataId]: (w.regions[metadataId] ?? []).filter((x) => x.id !== r.id),
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
