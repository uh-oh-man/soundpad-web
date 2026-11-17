import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type MutableRefObject,
  type PointerEvent,
  type ReactNode,
} from 'react';
import {
  ago,
  nudgeSelection,
  normalizeSelection,
  NUDGE_MS,
  VIEW_SECONDS,
  visibleRange,
  type Selection,
} from '../../mimic/selection';
import { isClick } from './playMode';
import { glideData, glideStart, glideStep, type Glide } from '../../mimic/glide';

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

/** Extras the recording history view draws (2.39); the live 30 s view of older companions has none. */
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
  /** Other people's selections (2.40): coloured, labelled bands ("Alex's selection"). */
  bands?: (Selection & { color: string; label: string })[];
  /** Called when a drag starts, so a view that follows live can hold still under the pointer. */
  onGrab?: () => void;
  onAbandon?: () => void;
  /**
   * While following live (2.47): the end of the levels shown (ms of the recording) and how often new ones
   * come. The view then glides on between polls instead of jumping once per poll. Absent: it stands still.
   */
  live?: { endMs: number; intervalMs: number };
  /**
   * Where the levels' window ends (ms of the recording). A held view keeps the glide's lag while levels of
   * the same window come again, and drops it when another window is shown.
   */
  viewEndMs?: number;
  /**
   * Written with how far (ms) the view drawn now ends before the end of the levels: the glide's lag. It is
   * kept after the glide stops until other levels arrive, so a view held still shows what was on screen.
   */
  shiftRef?: MutableRefObject<number>;
  /**
   * A click (press and release without a drag, not on an edge handle) plays from that point (2.45): it gets the
   * position in ms of the window and the selection stays as it was. Without it a click clears the selection, as
   * the 30 s view always did. Releasing a drag only ever selects.
   */
  onPlayAt?: (ms: number) => void;
  /** Where the audio playing from this view is (ms of the window): a thin line; null or absent for none. */
  playhead?: number | null;
  ruler?: boolean;
  renderWords?: (start: number, end: number, width: number) => ReactNode;
};

function canvasColors() {
  const style = getComputedStyle(document.documentElement);
  return {
    wave: style.getPropertyValue('--wave').trim() || '#7c9fca',
    accent: style.getPropertyValue('--accent').trim() || '#7cb5ff',
    line: style.getPropertyValue('--line').trim() || '#303b4a',
    muted: style.getPropertyValue('--text-dim').trim() || '#8a94a6',
    text: style.getPropertyValue('--text-primary').trim() || '#eaf1ff',
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
  const drag = useRef<{
    pointer: number;
    anchor: number;
    x: number;
    y: number;
    edge: boolean;
    at: (x: number) => number;
    windowMs: number;
    select: typeof onSelect;
    playAt: WaveExtras['onPlayAt'];
    abandon: WaveExtras['onAbandon'];
  } | null>(null);
  const [width, setWidth] = useState(0);
  const hasWords = !!extras.renderWords;
  useEffect(() => {
    const element = canvas.current;
    if (!element || !hasWords) return;
    let frame = 0;
    const resize = new ResizeObserver(() => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => setWidth(element.clientWidth));
    });
    resize.observe(element);
    return () => {
      resize.disconnect();
      cancelAnimationFrame(frame);
    };
  }, [hasWords]);
  const [draft, setDraft] = useState<Selection | null>(null);
  const colors = useRef(canvasColors());
  colors.current = canvasColors();
  const storageKey = extras.storageKey ?? VIEW_KEY;
  const [viewSeconds, setViewSeconds] = useState(() =>
    rememberedView(storageKey, extras.defaultView ?? VIEW_SECONDS[0]),
  );
  // The range a drag started in: kept until the pointer is released, so the view never jumps under it.
  const [dragRange, setDragRange] = useState<{ start: number; end: number } | null>(null);
  const shown = draft ?? selection;
  const { marks, hidden, highlight, bands, playhead } = extras;
  // The glide (2.47): the drawn view ends `shift` ms before the levels do. While following live it changes
  // every animation frame without a React render: the canvas is redrawn and the word lane moved directly.
  const glide = useRef<{ g: Glide | null; endMs: number | undefined; shift: number }>({
    g: null,
    endMs: undefined,
    shift: 0,
  });
  const lane = useRef<HTMLDivElement>(null);
  const live = extras.live;
  const shiftRef = extras.shiftRef;
  /**
   * The glide holds while the pointer is over the waveform or its words, or the keyboard is in them: what is
   * aimed at stands still (2.47). It catches up, a little faster than time passes, once let go.
   */
  const aiming = useRef(false);
  // New levels while following live: the glide learns where they end (here, so the words below are laid out
  // at the lag of this render). With the glide off, the lag stays until other levels arrive (a held view is
  // fetched to end where the glide was).
  {
    const state = glide.current;
    if (live) {
      state.g = state.g
        ? glideData(state.g, live.endMs, aiming.current)
        : glideStart(live.endMs, live.intervalMs);
      state.shift = state.g.dataEnd - state.g.visEnd;
      state.endMs = extras.viewEndMs ?? live.endMs;
    } else {
      state.g = null;
      if (extras.viewEndMs === undefined || extras.viewEndMs !== state.endMs) {
        state.shift = 0;
        state.endMs = extras.viewEndMs;
      }
    }
  }
  // A selection that starts before the zoomed view as drawn (with the glide's lag) widens it.
  const lag = glide.current.shift;
  const { start, end } =
    dragRange ??
    visibleRange(
      peek.windowMs,
      viewSeconds * 1000,
      selection && { from: selection.from + lag, to: selection.to + lag },
    );
  const span = end - start;
  /** The lag the words were laid out at in this render; the lane moves by the change since. */
  const wordsShift = dragRange ? 0 : glide.current.shift;
  const laidOut = useRef(0);
  laidOut.current = wordsShift;
  /** The time range drawn now (ms of the window). */
  const view = () =>
    dragRange ?? { start: start - glide.current.shift, end: end - glide.current.shift };
  const draw = useRef<() => void>(() => {});
  draw.current = () => {
    const el = canvas.current;
    const ctx = el?.getContext('2d');
    if (!el || !ctx) return;
    const shift = dragRange ? 0 : glide.current.shift;
    const c = colors.current;
    // The lag the view is held at, also while a drag shows its own range.
    if (shiftRef) shiftRef.current = glide.current.shift;
    if (lane.current)
      lane.current.style.transform =
        shift !== laidOut.current
          ? `translateX(${(((shift - laidOut.current) / span) * 100).toFixed(3)}%)`
          : '';
    const ratio = window.devicePixelRatio || 1;
    const width = Math.round(el.clientWidth * ratio),
      height = Math.round(el.clientHeight * ratio);
    // Resizing clears the canvas and costs a new buffer: only when the size really changed.
    if (el.width !== width || el.height !== height) {
      el.width = width;
      el.height = height;
    }
    ctx.clearRect(0, 0, width, height);
    const from = start - shift;
    const x = (t: number) => ((t - from) / span) * width;
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
      ctx.strokeStyle = c.line;
      ctx.lineWidth = ratio;
      for (let h = a - height; h < b; h += 8 * ratio) {
        ctx.moveTo(h, height);
        ctx.lineTo(h + height, 0);
      }
      ctx.stroke();
      ctx.restore();
    }
    // Other people's selections sit under one's own, each in its own colour with its label.
    if (bands?.length) {
      ctx.save();
      ctx.font = `${10 * ratio}px system-ui, sans-serif`;
      for (const band of bands) {
        const a = Math.max(0, x(band.from)),
          b = Math.min(width, x(band.to));
        if (b <= a) continue;
        ctx.fillStyle = band.color + '2e';
        ctx.fillRect(a, 0, b - a, height);
        ctx.fillStyle = band.color;
        ctx.fillRect(a, height - 3 * ratio, b - a, 3 * ratio);
        ctx.fillText(band.label, a + 3 * ratio, height - 6 * ratio, Math.max(0, b - a - 4 * ratio));
      }
      ctx.restore();
    }
    if (highlight) {
      ctx.fillStyle = c.accent + '26';
      ctx.fillRect(x(highlight.from), 0, x(highlight.to) - x(highlight.from), height);
    }
    if (shown) {
      ctx.fillStyle = c.accent + '33';
      ctx.fillRect(x(shown.from), 0, x(shown.to) - x(shown.from), height);
    }
    const edges = shown ? [shown.from, shown.to].map(x) : [];
    const step = peek.windowMs / peek.levels.length;
    const bar = (step / span) * width;
    for (let i = 0; i < peek.levels.length; i++) {
      const t = (i + 0.5) * step;
      if (t < from - step || t > from + span + step) continue;
      const inside = shown && t >= shown.from && t <= shown.to;
      ctx.fillStyle = inside ? c.accent : c.wave;
      const h = Math.max(ratio, (peek.levels[i] / 1000) * height * 0.92);
      ctx.fillRect(x(i * step) + bar * 0.15, (height - h) / 2, Math.max(ratio, bar * 0.7), h);
    }
    // Ticks every 5 s, or every second when zoomed in: fixed to the view, like the ruler above it.
    ctx.fillStyle = c.line;
    const tick = span > 10000 ? 5000 : 1000;
    for (let s = span - tick; s > 0; s -= tick)
      ctx.fillRect((s / span) * width, height - 6 * ratio, ratio, 6 * ratio);
    // Gaps: a dashed line where recording stopped and resumed (no time passes on the waveform there).
    if (marks?.length) {
      ctx.save();
      ctx.strokeStyle = c.muted;
      ctx.fillStyle = c.muted;
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
    ctx.fillStyle = c.accent;
    for (const x of edges) {
      ctx.fillRect(x - ratio, 0, 2 * ratio, height);
      ctx.fillRect(x - 4 * ratio, height / 2 - 12 * ratio, 8 * ratio, 24 * ratio);
    }
    // What is playing from this view (2.45): a thin line in the text colour, apart from the accent edges.
    if (playhead != null && playhead >= from && playhead <= from + span) {
      ctx.fillStyle = c.text;
      ctx.fillRect(x(playhead) - ratio / 2, 0, ratio, height);
    }
  };
  // Every render draws once.
  useLayoutEffect(() => {
    draw.current();
  });
  const gliding = !!live;
  useEffect(() => {
    if (!gliding) return;
    let frame = requestAnimationFrame(function tick(now) {
      const state = glide.current;
      if (state.g && aiming.current) state.g.last = now;
      else if (state.g) {
        const shift = glideStep(state.g, now);
        // Under a tenth of a pixel moved: nothing to redraw.
        const px = canvas.current?.clientWidth ?? 0;
        if (Math.abs(shift - state.shift) * (px / span) >= 0.1) {
          state.shift = shift;
          draw.current();
        }
      }
      frame = requestAnimationFrame(tick);
    });
    return () => cancelAnimationFrame(frame);
  }, [gliding, span]);
  // The theme changed (light or dark, another colour set): redraw now, not at the next poll.
  useEffect(() => {
    const redraw = () =>
      requestAnimationFrame(() => {
        colors.current = canvasColors();
        draw.current();
      });
    const observer = new MutationObserver(redraw);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme', 'data-palette', 'class', 'style'],
    });
    const scheme = window.matchMedia?.('(prefers-color-scheme: dark)');
    scheme?.addEventListener?.('change', redraw);
    return () => {
      observer.disconnect();
      scheme?.removeEventListener?.('change', redraw);
    };
  }, []);
  const pointerTime = (box: DOMRect) => {
    const from = view().start;
    return (x: number) =>
      from + (Math.min(Math.max(x - box.left, 0), box.width) / box.width) * span;
  };
  /** The opposite end of the selection when the pointer is on one of its edge handles. */
  const anchorFor = (time: number, width: number) => {
    if (!selection) return null;
    const edgeMs = EDGE_PX * (span / width);
    if (Math.abs(time - selection.from) <= edgeMs) return selection.to;
    if (Math.abs(time - selection.to) <= edgeMs) return selection.from;
    return null;
  };
  const finishDrag = () => {
    drag.current = null;
    setDragRange(null);
    setDraft(null);
  };
  const cancelDrag = (e: PointerEvent<HTMLCanvasElement>) => {
    const gesture = drag.current;
    if (gesture?.pointer !== e.pointerId) return;
    finishDrag();
    gesture.abandon?.();
  };
  return (
    <div
      className="mimic-wave"
      onPointerEnter={() => (aiming.current = true)}
      onPointerLeave={() => (aiming.current = false)}
      onFocus={() => (aiming.current = true)}
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) aiming.current = false;
      }}
    >
      {extras.ruler && (
        <div className="mimic-ruler" aria-hidden="true">
          {Array.from({ length: 6 }, (_, index) => {
            const offset = (start + (span * index) / 5 - end) / 1000;
            return (
              <span key={index}>
                {index === 5
                  ? (extras.endLabel ?? 'now')
                  : `${offset.toFixed(1).replace(/\.0$/, '')} s`}
              </span>
            );
          })}
        </div>
      )}
      <canvas
        ref={canvas}
        role="slider"
        tabIndex={0}
        aria-label={`Mimic buffer, showing ${extras.where ? `${span / 1000} seconds ${extras.where}` : `the last ${span / 1000} seconds`}. ${extras.onPlayAt ? 'Click to play from a point; drag' : 'Drag'} to select; drag an edge to adjust it. Arrow keys move the end, Shift+arrows the start, Ctrl for 1 second steps.`}
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
          if (e.button !== 0 || drag.current) return;
          const box = e.currentTarget.getBoundingClientRect();
          const at = pointerTime(box);
          const time = at(e.clientX);
          const edge = anchorFor(time, box.width);
          drag.current = {
            pointer: e.pointerId,
            anchor: edge ?? time,
            edge: edge !== null,
            x: e.clientX,
            y: e.clientY,
            at,
            windowMs: peek.windowMs,
            select: onSelect,
            playAt: extras.onPlayAt,
            abandon: extras.onAbandon,
          };
          e.currentTarget.setPointerCapture(e.pointerId);
          // The view as drawn (with the glide's lag) holds still until the pointer is released.
          setDragRange(view());
          extras.onGrab?.();
        }}
        onPointerMove={(e) => {
          const gesture = drag.current;
          if (gesture?.pointer === e.pointerId) {
            const at = gesture.at(e.clientX);
            setDraft({ from: Math.min(gesture.anchor, at), to: Math.max(gesture.anchor, at) });
          } else if (!gesture) {
            const box = e.currentTarget.getBoundingClientRect();
            const edge = anchorFor(pointerTime(box)(e.clientX), box.width);
            e.currentTarget.style.cursor = edge === null ? '' : 'ew-resize';
          }
        }}
        onPointerCancel={cancelDrag}
        onLostPointerCapture={cancelDrag}
        onPointerUp={(e) => {
          const gesture = drag.current;
          if (gesture?.pointer !== e.pointerId) return;
          const at = gesture.at(e.clientX);
          const clicked = isClick(e.clientX - gesture.x, e.clientY - gesture.y, gesture.edge);
          const next = normalizeSelection(gesture.anchor, at, gesture.windowMs);
          finishDrag();
          if (clicked && gesture.playAt) gesture.playAt(at);
          else if (next || (clicked && !gesture.edge)) gesture.select(next);
          else gesture.abandon?.();
        }}
      />
      {extras.renderWords && (
        <div className="mimic-word-track" role="group" aria-label="Words on the waveform">
          {/* The lane moves with the glide (2.47); the words keep their places in it. */}
          <div className="mimic-word-lane" ref={lane}>
            {width > 0 && extras.renderWords(start - wordsShift, end - wordsShift, width)}
          </div>
        </div>
      )}
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
