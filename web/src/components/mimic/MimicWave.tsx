import { useEffect, useRef, useState, type PointerEvent } from 'react';
import {
  ago,
  nudgeSelection,
  normalizeSelection,
  NUDGE_MS,
  VIEW_SECONDS,
  visibleRange,
  type Selection,
} from '../../mimic/selection';

const VIEW_KEY = 'soundpad-web:mimic-view';

function rememberedView() {
  try {
    const seconds = Number(localStorage.getItem(VIEW_KEY));
    return VIEW_SECONDS.includes(seconds) ? seconds : VIEW_SECONDS[0];
  } catch {
    return VIEW_SECONDS[0]; // storage can be unavailable; the full window still works
  }
}

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
  const [viewSeconds, setViewSeconds] = useState(rememberedView);
  // The range a drag started in: kept until the pointer is released, so the view never jumps under it.
  const [dragRange, setDragRange] = useState<{ start: number; end: number } | null>(null);
  const shown = draft ?? selection;
  const { start, end } = dragRange ?? visibleRange(peek.windowMs, viewSeconds * 1000, selection);
  const span = end - start;
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
    const x = (t: number) => ((t - start) / span) * width;
    if (shown) {
      ctx.fillStyle = colors.accent + '33';
      ctx.fillRect(x(shown.from), 0, x(shown.to) - x(shown.from), height);
    }
    const edges = shown ? [shown.from, shown.to].map(x) : [];
    const step = peek.windowMs / peek.levels.length;
    const bar = (step / span) * width;
    peek.levels.forEach((level, i) => {
      const t = (i + 0.5) * step;
      if (t < start) return;
      const inside = shown && t >= shown.from && t <= shown.to;
      ctx.fillStyle = inside ? colors.accent : colors.wave;
      const h = Math.max(ratio, (level / 1000) * height * 0.92);
      ctx.fillRect(x(i * step) + bar * 0.15, (height - h) / 2, Math.max(ratio, bar * 0.7), h);
    });
    // Ticks every 5 s, or every second when zoomed in.
    ctx.fillStyle = colors.line;
    const tick = span > 10000 ? 5000 : 1000;
    for (let s = end - tick; s > start; s -= tick)
      ctx.fillRect(x(s), height - 6 * ratio, ratio, 6 * ratio);
    // Edge handles, like Voice Replay Studio's: drag one to adjust that end of the selection.
    ctx.fillStyle = colors.accent;
    for (const x of edges) {
      ctx.fillRect(x - ratio, 0, 2 * ratio, height);
      ctx.fillRect(x - 4 * ratio, height / 2 - 12 * ratio, 8 * ratio, 24 * ratio);
    }
  }, [peek, shown, start, end, span, colors.accent, colors.wave, colors.line]);
  const at = (e: PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return start + (Math.min(Math.max(e.clientX - box.left, 0), box.width) / box.width) * span;
  };
  /** The opposite end of the selection when the pointer is on one of its edge handles. */
  const anchorFor = (e: PointerEvent<HTMLCanvasElement>) => {
    if (!selection) return null;
    const msPerPixel = span / e.currentTarget.getBoundingClientRect().width;
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
        aria-label={`Mimic buffer, showing the last ${span / 1000} seconds. Drag to select; drag an edge to adjust it. Arrow keys move the end, Shift+arrows the start, Ctrl for 1 second steps.`}
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
          setDragRange({ start, end });
          setDraft(null);
        }}
        onPointerMove={(e) => {
          if (drag.current === null)
            e.currentTarget.style.cursor = anchorFor(e) === null ? '' : 'ew-resize';
          else setDraft({ from: Math.min(drag.current, at(e)), to: Math.max(drag.current, at(e)) });
        }}
        onPointerCancel={() => {
          drag.current = null;
          setDragRange(null);
          setDraft(null);
        }}
        onPointerUp={(e) => {
          if (drag.current === null) return;
          const next = normalizeSelection(drag.current, at(e), peek.windowMs);
          drag.current = null;
          setDragRange(null);
          setDraft(null);
          onSelect(next);
        }}
      />
      <div className="mimic-axis">
        <span aria-hidden="true">−{span / 1000} s</span>
        <span className="mimic-zoom" role="group" aria-label="Waveform zoom">
          {VIEW_SECONDS.map((s) => (
            <button
              key={s}
              type="button"
              aria-pressed={viewSeconds === s}
              title={`Show the last ${s} seconds`}
              onClick={() => {
                setViewSeconds(s);
                try {
                  localStorage.setItem(VIEW_KEY, String(s));
                } catch {
                  // not remembered; the choice still applies now
                }
              }}
            >
              {s} s
            </button>
          ))}
        </span>
        <span aria-hidden="true">now</span>
      </div>
    </div>
  );
}
