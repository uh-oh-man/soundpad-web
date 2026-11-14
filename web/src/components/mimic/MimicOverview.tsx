import { useEffect, useRef, type KeyboardEvent, type PointerEvent } from 'react';
import { formatSpan, framesToMs, msToFrames, type FrameRange } from '../../mimic/history';

/**
 * The whole recording history as one small waveform (2.38): the part the main waveform shows is outlined; drag
 * or click to move it there. Recording never pauses while the view moves.
 */
export function MimicOverview({
  range,
  peaks,
  view,
  rate,
  gaps,
  shared,
  behindMs,
  onMove,
}: {
  /** The frames the overview covers: what this person may see, oldest to live. */
  range: FrameRange;
  peaks: number[];
  /** The frames the main waveform shows. */
  view: FrameRange;
  rate: number;
  gaps: number[];
  /** Parts the host shared with guests (drawn tinted). */
  shared: FrameRange[];
  behindMs: number;
  /** A new end for the main view (a frame); at or past live means "go live". */
  onMove: (end: number) => void;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const dragging = useRef<number | null>(null);
  const span = Math.max(1, range.endFrame - range.startFrame);
  useEffect(() => {
    const el = canvas.current;
    const ctx = el?.getContext('2d');
    if (!el || !ctx) return;
    const style = getComputedStyle(document.documentElement);
    const wave = style.getPropertyValue('--wave').trim() || '#7c9fca';
    const accent = style.getPropertyValue('--accent').trim() || '#7cb5ff';
    const muted = style.getPropertyValue('--text-dim').trim() || '#8a94a6';
    const ratio = window.devicePixelRatio || 1;
    const width = el.clientWidth * ratio,
      height = el.clientHeight * ratio;
    el.width = width;
    el.height = height;
    ctx.clearRect(0, 0, width, height);
    const x = (frame: number) => ((frame - range.startFrame) / span) * width;
    ctx.fillStyle = accent + '1f';
    for (const s of shared)
      ctx.fillRect(x(s.startFrame), 0, x(s.endFrame) - x(s.startFrame), height);
    const bar = width / Math.max(1, peaks.length);
    ctx.fillStyle = wave;
    peaks.forEach((level, i) => {
      const h = Math.max(ratio, (level / 1000) * height * 0.9);
      ctx.fillRect(i * bar, (height - h) / 2, Math.max(ratio, bar * 0.8), h);
    });
    ctx.fillStyle = muted;
    for (const g of gaps) ctx.fillRect(x(g) - ratio, 0, 2 * ratio, height);
    // The main view's part, outlined.
    ctx.strokeStyle = accent;
    ctx.lineWidth = 2 * ratio;
    const a = x(view.startFrame),
      b = x(view.endFrame);
    ctx.fillStyle = accent + '2e';
    ctx.fillRect(a, 0, Math.max(2 * ratio, b - a), height);
    ctx.strokeRect(a + ratio, ratio, Math.max(2 * ratio, b - a - 2 * ratio), height - 2 * ratio);
  }, [range.startFrame, span, peaks, view.startFrame, view.endFrame, gaps, shared]);
  const viewFrames = view.endFrame - view.startFrame;
  const at = (e: PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return (
      range.startFrame + (Math.min(Math.max(e.clientX - box.left, 0), box.width) / box.width) * span
    );
  };
  const key = (e: KeyboardEvent<HTMLCanvasElement>) => {
    const step = msToFrames(e.shiftKey ? 10000 : 1000, rate);
    const moves: Record<string, number> = {
      ArrowLeft: view.endFrame - step,
      ArrowRight: view.endFrame + step,
      Home: range.startFrame + viewFrames,
      End: range.endFrame,
    };
    if (!(e.key in moves)) return;
    e.preventDefault();
    onMove(moves[e.key]);
  };
  const total = framesToMs(span, rate);
  return (
    <div className="mimic-overview">
      <canvas
        ref={canvas}
        role="slider"
        tabIndex={0}
        aria-label={`Recording history overview, ${formatSpan(total)}. Drag or click to move the view; arrow keys move it by a second, Shift by ten.`}
        aria-valuemin={range.startFrame}
        aria-valuemax={range.endFrame}
        aria-valuenow={view.endFrame}
        aria-valuetext={
          behindMs < 250 ? 'Showing live' : `Showing ${formatSpan(behindMs)} before live`
        }
        onKeyDown={key}
        onPointerDown={(e) => {
          e.currentTarget.setPointerCapture(e.pointerId);
          // Grabbing inside the outlined part keeps the pointer where it is on it; elsewhere centres the view there.
          const t = at(e);
          dragging.current =
            t >= view.startFrame && t <= view.endFrame ? view.endFrame - t : viewFrames / 2;
          onMove(t + dragging.current);
        }}
        onPointerMove={(e) => {
          if (dragging.current !== null) onMove(at(e) + dragging.current);
        }}
        onPointerUp={() => (dragging.current = null)}
        onPointerCancel={() => (dragging.current = null)}
      />
    </div>
  );
}
