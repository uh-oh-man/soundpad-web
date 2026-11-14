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

function rememberedView(key: string, fallback: number) {
  try {
    const seconds = Number(localStorage.getItem(key));
    return VIEW_SECONDS.includes(seconds) ? seconds : fallback;
  } catch {
    return fallback; // storage can be unavailable; the full window still works
  }
}

/** How close (in CSS pixels) the pointer must be to a selection edge to grab its handle. */
const EDGE_PX = 8;

export type WavePeek = { windowMs: number; levels: number[] };

/** Extras the recording history view draws (2.38); the live 30 s view of older companions has none. */
export type WaveExtras = {
  /** Where the zoom choice is remembered, and the zoom to start with. */
  storageKey?: string;
  defaultView?: number;
  /** Replaces "the last N seconds" when the view is not at live, e.g. "ending 42 s before live". */
  where?: string;
  /** Text at the right end of the axis ("now" by default). */
  endLabel?: string;
  /** Gaps in the recording (they take no time on the waveform): a line and a label. */
  marks?: { ms: number; label: string }[];
  /** Parts this person cannot see (not recorded, expired, before they joined): drawn hatched, never as silence. */
  hidden?: Selection[];
  /** A word under the pointer in the transcript. */
  highlight?: Selection | null;
  /** Called when a drag starts, so a view that follows live can hold still under the pointer. */
  onGrab?: () => void;
};

function useCanvasColors() {
  const style = getComputedStyle(document.documentElement);
  return {
    wave: style.getPropertyValue('--wave').trim() || '#7c9fca',
    accent: style.getPropertyValue('--accent').trim() || '#7cb5ff',
    line: style.getPropertyValue('--line').trim() || '#303b4a',
    muted: style.getPropertyValue('--text-dim').trim() || '#8a94a6',
  };
}

/** The buffer as a waveform: drag to select, drag an edge handle to adjust, arrow keys to nudge. */
export function MimicWave({
  peek,
  selection,
  onSelect,
  extras = {},
}: {
  peek: WavePeek;
  selection: Selection | null;
  onSelect: (selection: Selection | null) => void;
  extras?: WaveExtras;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const drag = useRef<number | null>(null);
  const [draft, setDraft] = useState<Selection | null>(null);
  const colors = useCanvasColors();
  const storageKey = extras.storageKey ?? VIEW_KEY;
  const [viewSeconds, setViewSeconds] = useState(() =>
    rememberedView(storageKey, extras.defaultView ?? VIEW_SECONDS[0]),
  );
  // The range a drag started in: kept until the pointer is released, so the view never jumps under it.
  const [dragRange, setDragRange] = useState<{ start: number; end: number } | null>(null);
  const shown = draft ?? selection;
  const { start, end } = dragRange ?? visibleRange(peek.windowMs, viewSeconds * 1000, selection);
  const span = end - start;
  const { marks, hidden, highlight } = extras;
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
    // What this person cannot see is hatched, so it is never mistaken for silence.
    for (const part of hidden ?? []) {
      const a = Math.max(0, x(part.from)),
        b = Math.min(width, x(part.to));
      if (b <= a) continue;
      ctx.save();
      ctx.beginPath();
      ctx.rect(a, 0, b - a, height);
      ctx.clip();
      ctx.beginPath();
      ctx.strokeStyle = colors.line;
      ctx.lineWidth = ratio;
      for (let h = a - height; h < b; h += 8 * ratio) {
        ctx.moveTo(h, height);
        ctx.lineTo(h + height, 0);
      }
      ctx.stroke();
      ctx.restore();
    }
    if (highlight) {
      ctx.fillStyle = colors.accent + '26';
      ctx.fillRect(x(highlight.from), 0, x(highlight.to) - x(highlight.from), height);
    }
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
    // Gaps: a dashed line where recording stopped and resumed (no time passes on the waveform there).
    if (marks?.length) {
      ctx.save();
      ctx.strokeStyle = colors.muted;
      ctx.fillStyle = colors.muted;
      ctx.setLineDash([4 * ratio, 3 * ratio]);
      ctx.lineWidth = 1.5 * ratio;
      ctx.font = `${11 * ratio}px system-ui, sans-serif`;
      for (const mark of marks) {
        const at = x(mark.ms);
        if (at < 0 || at > width) continue;
        ctx.beginPath();
        ctx.moveTo(at, 0);
        ctx.lineTo(at, height);
        ctx.stroke();
        const text = mark.label;
        const textWidth = ctx.measureText(text).width;
        ctx.fillText(text, Math.min(at + 4 * ratio, width - textWidth - 2 * ratio), 12 * ratio);
      }
      ctx.restore();
    }
    // Edge handles, like Voice Replay Studio's: drag one to adjust that end of the selection.
    ctx.fillStyle = colors.accent;
    for (const x of edges) {
      ctx.fillRect(x - ratio, 0, 2 * ratio, height);
      ctx.fillRect(x - 4 * ratio, height / 2 - 12 * ratio, 8 * ratio, 24 * ratio);
    }
  }, [
    peek,
    shown,
    start,
    end,
    span,
    colors.accent,
    colors.wave,
    colors.line,
    colors.muted,
    marks,
    hidden,
    highlight,
  ]);
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
        aria-label={`Mimic buffer, showing ${extras.where ? `${span / 1000} seconds ${extras.where}` : `the last ${span / 1000} seconds`}. Drag to select; drag an edge to adjust it. Arrow keys move the end, Shift+arrows the start, Ctrl for 1 second steps.`}
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
          extras.onGrab?.();
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
                  localStorage.setItem(storageKey, String(s));
                } catch {
                  // not remembered; the choice still applies now
                }
              }}
            >
              {s} s
            </button>
          ))}
        </span>
        <span aria-hidden="true">{extras.endLabel ?? 'now'}</span>
      </div>
    </div>
  );
}
