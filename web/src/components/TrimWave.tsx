import { useRef } from 'react';
import type { Snapshot } from '../protocol';
import { useWaveformData } from '../waveform-cache';
import { trimDrag, trimGrab, type TrimGrab } from '../trim';
import { wavePath } from './Waveform';

const WIDTH = 800;

/**
 * The source's waveform above Edit copy's Start and End boxes (2.19.0), like Voice Replay Studio's
 * clip editor: drag across it to choose the part to copy, or drag an edge to adjust it. The number
 * boxes stay the exact, keyboard-friendly controls, so this strip is a pointer aid only.
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
  const path = data ? wavePath(data, WIDTH) : '';
  const secondsAt = (e: React.PointerEvent<SVGSVGElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return ((e.clientX - box.left) / Math.max(1, box.width)) * length;
  };
  const x = (s: number) => (Math.min(Math.max(0, s), length) / length) * WIDTH;
  const left = x(Math.min(from, to)),
    right = x(Math.max(from, to));
  return (
    <div className="trim-wave" aria-hidden="true">
      <svg
        viewBox={`0 0 ${WIDTH} 64`}
        preserveAspectRatio="none"
        data-testid="trim-wave"
        onPointerDown={(e) => {
          if (e.button !== 0) return;
          const t = secondsAt(e);
          const box = e.currentTarget.getBoundingClientRect();
          // An edge is "near" within 10 px, whatever the sound's length.
          const tolerance = (10 / Math.max(1, box.width)) * length;
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
      {!path && (
        <small>
          {data?.status === 'unavailable' ? 'No waveform for this sound.' : 'Loading waveform…'}
        </small>
      )}
    </div>
  );
}
