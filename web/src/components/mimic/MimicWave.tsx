import { useEffect, useRef, useState, type PointerEvent } from 'react';
import {
  ago,
  nudgeSelection,
  normalizeSelection,
  NUDGE_MS,
  type Selection,
} from '../../mimic/selection';

/** How close (in CSS pixels) the pointer must be to a selection edge to grab its handle. */
const EDGE_PX = 8;

export type WavePeek = { windowMs: number; levels: number[] };

function useCanvasColors() {
  const style = getComputedStyle(document.documentElement);
  return {
    wave: style.getPropertyValue('--wave').trim() || '#7c9fca',
    accent: style.getPropertyValue('--accent').trim() || '#7cb5ff',
    line: style.getPropertyValue('--line').trim() || '#303b4a',
  };
}

/** The live buffer as a waveform: drag to select, drag an edge handle to adjust, arrow keys to nudge. */
export function MimicWave({
  peek,
  selection,
  onSelect,
}: {
  peek: WavePeek;
  selection: Selection | null;
  onSelect: (selection: Selection | null) => void;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const drag = useRef<number | null>(null);
  const [draft, setDraft] = useState<Selection | null>(null);
  const colors = useCanvasColors();
  const shown = draft ?? selection;
  useEffect(() => {
    const el = canvas.current;
    const ctx = el?.getContext('2d');
    if (!el || !ctx) return;
    const ratio = window.devicePixelRatio || 1;
    const width = el.clientWidth * ratio,
      height = el.clientHeight * ratio;
    el.width = width;
    el.height = height;
    ctx.clearRect(0, 0, width, height);
    if (shown) {
      ctx.fillStyle = colors.accent + '33';
      const x0 = (shown.from / peek.windowMs) * width,
        x1 = (shown.to / peek.windowMs) * width;
      ctx.fillRect(x0, 0, x1 - x0, height);
    }
    const edges = shown ? [shown.from, shown.to].map((t) => (t / peek.windowMs) * width) : [];
    const bar = width / peek.levels.length;
    peek.levels.forEach((level, i) => {
      const t = ((i + 0.5) / peek.levels.length) * peek.windowMs;
      const inside = shown && t >= shown.from && t <= shown.to;
      ctx.fillStyle = inside ? colors.accent : colors.wave;
      const h = Math.max(ratio, (level / 1000) * height * 0.92);
      ctx.fillRect(i * bar + bar * 0.15, (height - h) / 2, Math.max(ratio, bar * 0.7), h);
    });
    ctx.fillStyle = colors.line;
    for (let s = 5000; s < peek.windowMs; s += 5000)
      ctx.fillRect((s / peek.windowMs) * width, height - 6 * ratio, ratio, 6 * ratio);
    // Edge handles, like Voice Replay Studio's: drag one to adjust that end of the selection.
    ctx.fillStyle = colors.accent;
    for (const x of edges) {
      ctx.fillRect(x - ratio, 0, 2 * ratio, height);
      ctx.fillRect(x - 4 * ratio, height / 2 - 12 * ratio, 8 * ratio, 24 * ratio);
    }
  }, [peek, shown, colors.accent, colors.wave, colors.line]);
  const at = (e: PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return (Math.min(Math.max(e.clientX - box.left, 0), box.width) / box.width) * peek.windowMs;
  };
  /** The opposite end of the selection when the pointer is on one of its edge handles. */
  const anchorFor = (e: PointerEvent<HTMLCanvasElement>) => {
    if (!selection) return null;
    const msPerPixel = peek.windowMs / e.currentTarget.getBoundingClientRect().width;
    const t = at(e);
    if (Math.abs(t - selection.from) <= EDGE_PX * msPerPixel) return selection.to;
    if (Math.abs(t - selection.to) <= EDGE_PX * msPerPixel) return selection.from;
    return null;
  };
  return (
    <div className="mimic-wave">
      <canvas
        ref={canvas}
        role="slider"
        tabIndex={0}
        aria-label={`Mimic buffer, last ${peek.windowMs / 1000} seconds. Drag to select; drag an edge to adjust it. Arrow keys move the end, Shift+arrows the start, Ctrl for 1 second steps.`}
        aria-valuemin={0}
        aria-valuemax={peek.windowMs}
        aria-valuenow={selection?.to ?? peek.windowMs}
        aria-valuetext={
          selection
            ? `Selected ${ago(selection.from, peek.windowMs)} to ${ago(selection.to, peek.windowMs)}`
            : 'Nothing selected'
        }
        onKeyDown={(e) => {
          if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
          e.preventDefault();
          onSelect(
            nudgeSelection(
              selection,
              e.key,
              e.shiftKey,
              peek.windowMs,
              e.ctrlKey ? 1000 : NUDGE_MS,
            ),
          );
        }}
        onPointerDown={(e) => {
          e.currentTarget.setPointerCapture(e.pointerId);
          drag.current = anchorFor(e) ?? at(e);
          setDraft(null);
        }}
        onPointerMove={(e) => {
          if (drag.current === null)
            e.currentTarget.style.cursor = anchorFor(e) === null ? '' : 'ew-resize';
          else setDraft({ from: Math.min(drag.current, at(e)), to: Math.max(drag.current, at(e)) });
        }}
        onPointerUp={(e) => {
          if (drag.current === null) return;
          const next = normalizeSelection(drag.current, at(e), peek.windowMs);
          drag.current = null;
          setDraft(null);
          onSelect(next);
        }}
      />
      <div className="mimic-axis" aria-hidden="true">
        <span>−{peek.windowMs / 1000} s</span>
        <span>now</span>
      </div>
    </div>
  );
}
