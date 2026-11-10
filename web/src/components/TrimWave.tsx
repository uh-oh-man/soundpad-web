import { useRef, useState } from 'react';
import type { Snapshot } from '../protocol';
import { useWaveformData } from '../waveform-cache';
import { trimDrag, trimGrab, zoomView, type TrimGrab } from '../trim';
import { wavePath } from './Waveform';
import { NUDGE_MS, nudgeSelection } from '../mimic/selection';

const WIDTH = 800;

/**
 * The source's waveform above Edit copy's Start and End boxes (2.19.0), like Voice Replay Studio's
 * clip editor: drag across it to choose the part to copy, or drag an edge to adjust it. The number
 * boxes stay the exact controls. Like Mimic's waveform, it also takes the keyboard (2.31): Left/Right move
 * the end, Shift+Left/Right the start, Ctrl for 1 second steps.
 * "Zoom to selection" (2.22.0) shows just the chosen part, so edges on a long sound can be set finely.
 */
export function TrimWave({
  state,
  soundId,
  lengthMs,
  from,
  to,
  onChange,
}: {
  state: Snapshot;
  soundId: string;
  lengthMs: number;
  from: number;
  to: number;
  onChange: (range: { from: number; to: number }) => void;
}) {
  const { data } = useWaveformData(state.hostId, soundId, true);
  const drag = useRef<{ grab: TrimGrab; anchor: number; range: { from: number; to: number } }>(
    null,
  );
  const length = lengthMs / 1000;
  // The zoomed view is fixed when zooming in, so dragging an edge doesn't move the view under the pointer.
  const [zoom, setZoom] = useState<{ start: number; end: number } | null>(null);
  const view = zoom ?? { start: 0, end: length };
  const span = Math.max(0.001, view.end - view.start);
  const path =
    data?.durationMs && length
      ? wavePath(data, WIDTH, length / span, (view.start / length) * data.durationMs)
      : '';
  const secondsAt = (e: React.PointerEvent<SVGSVGElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return view.start + ((e.clientX - box.left) / Math.max(1, box.width)) * span;
  };
  const x = (s: number) => Math.min(Math.max(0, (s - view.start) / span), 1) * WIDTH;
  const left = x(Math.min(from, to)),
    right = x(Math.max(from, to));
  return (
    <div className="trim-wave">
      <svg
        role="slider"
        tabIndex={0}
        aria-label="Part to copy. Drag to choose; drag an edge to adjust it. Arrow keys move the end, Shift+arrows the start, Ctrl for 1 second steps."
        aria-valuemin={0}
        aria-valuemax={Number(length.toFixed(2))}
        aria-valuenow={Number(Math.max(from, to).toFixed(2))}
        aria-valuetext={`From ${Math.min(from, to).toFixed(2)} s to ${Math.max(from, to).toFixed(2)} s of ${length.toFixed(2)} s`}
        onKeyDown={(e) => {
          if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
          e.preventDefault();
          // Mimic's own nudge rules, in milliseconds.
          const next = nudgeSelection(
            { from: Math.min(from, to) * 1000, to: Math.max(from, to) * 1000 },
            e.key,
            e.shiftKey,
            lengthMs,
            e.ctrlKey ? 1000 : NUDGE_MS,
          );
          onChange({
            from: Math.round(next.from / 10) / 100,
            to: Math.round(next.to / 10) / 100,
          });
        }}
        viewBox={`0 0 ${WIDTH} 64`}
        preserveAspectRatio="none"
        data-testid="trim-wave"
        onPointerDown={(e) => {
          if (e.button !== 0) return;
          // No text selection: a selection nearby would turn the next drag into the browser's own
          // drag-and-drop, which cancels this one.
          e.preventDefault();
          const t = secondsAt(e);
          const box = e.currentTarget.getBoundingClientRect();
          // An edge is "near" within 10 px, whatever the sound's length or the zoom.
          const tolerance = (10 / Math.max(1, box.width)) * span;
          const grab = trimGrab(t, from, to, tolerance);
          drag.current = { grab, anchor: t, range: { from, to } };
          e.currentTarget.setPointerCapture(e.pointerId);
          if (grab === 'new') onChange(trimDrag('new', t, { from, to }, t, length));
        }}
        onPointerMove={(e) => {
          const d = drag.current;
          if (d) onChange(trimDrag(d.grab, secondsAt(e), d.range, d.anchor, length));
        }}
        onPointerUp={() => (drag.current = null)}
        onPointerCancel={() => (drag.current = null)}
      >
        <path className="trim-wave-path" d={path} vectorEffect="non-scaling-stroke" />
        <rect className="trim-wave-shade" x={0} y={0} width={left} height={64} />
        <rect className="trim-wave-shade" x={right} y={0} width={WIDTH - right} height={64} />
        <rect
          className="trim-wave-range"
          x={left}
          y={0}
          width={Math.max(0, right - left)}
          height={64}
        />
        <line className="trim-wave-edge" x1={left} x2={left} y1={0} y2={64} />
        <line className="trim-wave-edge" x1={right} x2={right} y1={0} y2={64} />
      </svg>
      <div className="trim-wave-bar">
        <small>
          {!path
            ? data?.status === 'unavailable'
              ? 'No waveform for this sound.'
              : 'Loading waveform…'
            : zoom
              ? `Showing ${view.start.toFixed(2)}–${view.end.toFixed(2)} s of ${length.toFixed(2)} s`
              : 'Drag to choose the part to copy, or drag an edge.'}
        </small>
        {/* Worth it only when the part is a small share of the sound, or to zoom back out. */}
        {(zoom || (length > 1 && Math.abs(to - from) < length * 0.8)) && (
          <button
            type="button"
            aria-pressed={!!zoom}
            onClick={() => setZoom(zoom ? null : zoomView(from, to, length))}
          >
            {zoom ? 'Show whole sound' : 'Zoom to selection'}
          </button>
        )}
      </div>
    </div>
  );
}
