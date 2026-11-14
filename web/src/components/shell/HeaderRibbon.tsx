import { useEffect, useLayoutEffect, useRef } from 'react';
import type { Snapshot } from '../../protocol';
import { usePref } from '../../prefs';
import { useReducedMotion } from '../../motion';
import { peekWaveform, requestWaveform, subscribeWaveforms } from '../../waveform-cache';
import { isComposed } from '../../ribbon/envelope';
import { drawRibbon, type RibbonPalette } from '../../ribbon/draw';
import { RibbonMachine, STATE_ALPHA, STILL_LEVEL, WAITING_LEVEL } from '../../ribbon/machine';

/**
 * What the header ribbon is given (Stage G spec 6.3). Frozen in Phase 0: SectionHeader (WP-A) renders
 * the ribbon with these props, and WP-B implements it.
 */
export type HeaderRibbonProps = {
  /** The shared playback state; the ribbon follows only a web play it can identify (spec 6.3). */
  playback: Snapshot['playback'];
  /** Whose library the playing sound belongs to (the waveform cache's key). */
  hostId: string | null | undefined;
  /** Guest playback held by the host's Panic, or playback suspended: the ribbon settles. */
  held: boolean;
  /** False while this section is not the current view: nothing is drawn. */
  active: boolean;
};

export type RibbonState = 'off' | 'idle' | 'waiting' | 'live' | 'settling' | 'still';

/** At most 30 frames a second; 20 after the ribbon proved slow on this machine. */
const FRAME_MS = 33;
const SLOW_FRAME_MS = 50;
/** A rolling mean of draw time above this (over 30 frames) switches to 8 strands at 20 fps. */
const SLOW_DRAW_MS = 4;
const PHONE = '(max-width: 700px)';
const FORCED = '(forced-colors: active)';

/** `level`: the level of the last frame drawn (the shots wait for a lifted one). */
type Seam = { frames: number; state: RibbonState; key: string; level?: number };
declare global {
  interface Window {
    /** DEV only: what the ribbon drew, for the e2e tests (spec 6.6). */
    __soundpadRibbon?: Seam;
  }
}

/** The page's slow-draw switch: once one ribbon was slow, every ribbon on the page stays light. */
let degraded = false;

function matches(query: string) {
  try {
    return typeof matchMedia === 'function' && matchMedia(query).matches;
  } catch {
    return false;
  }
}

function palette(element: Element): RibbonPalette {
  const style = getComputedStyle(element);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    deep: read('--ribbon-deep', '#12306b'),
    strand: read('--ribbon-strand', '#3d8bff'),
    crest: read('--ribbon-crest', '#d6f0ff'),
  };
}

/**
 * The audio-reactive header ribbon (Stage G spec 6). It follows the cached peaks of the clip that is
 * actually playing, identified by host, sound and epoch, so a replaced play never animates it. It is
 * decorative (aria-hidden, no pointer events), draws at most 30 frames a second, stops completely when
 * nothing moves, the page is hidden, the header is scrolled away or its section is not shown, and
 * gives a still picture under reduced motion. Playback props go into refs: no React state per frame.
 */
export function HeaderRibbon({ playback, hostId, held, active }: HeaderRibbonProps) {
  const [animation] = usePref('headerAnimation');
  const reduced = useReducedMotion();
  const wrapper = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const machine = useRef(new RibbonMachine());
  const props = useRef({ playback, hostId, held, active });
  // Everything the loop reads, in one place it can see without re-rendering.
  const loop = useRef({
    frame: 0,
    lastDraw: 0,
    lastTick: 0,
    drawn: '',
    state: 'idle' as RibbonState,
    visible: true,
    inView: true,
    width: 0,
    height: 0,
    still: false,
    palette: null as RibbonPalette | null,
    drawTimes: [] as number[],
    requested: new Set<string>(),
    frames: 0,
    schedule: () => {},
  });
  const off = animation === 'off' || matches(FORCED);
  const still = reduced || animation === 'still';
  loop.current.still = still;

  // A new report: the machine takes it at once (the loop reads it on its next frame).
  useLayoutEffect(() => {
    props.current = { playback, hostId, held, active };
    machine.current.report(playback, hostId, held, performance.now());
    loop.current.schedule();
  }, [playback, hostId, held, active]);

  useEffect(() => {
    if (off) {
      const element = wrapper.current;
      if (element) {
        element.dataset.ribbonState = 'off';
        element.dataset.ribbonKey = '';
      }
      if (import.meta.env.DEV)
        window.__soundpadRibbon = { frames: loop.current.frames, state: 'off', key: '' };
      return;
    }
    const element = wrapper.current,
      surface = canvas.current;
    if (!element || !surface) return;
    const context = surface.getContext('2d');
    const l = loop.current;
    l.palette = palette(element);
    l.drawn = '';

    function setState(state: RibbonState, key: string) {
      if (element!.dataset.ribbonState !== state) element!.dataset.ribbonState = state;
      if (element!.dataset.ribbonKey !== key) element!.dataset.ribbonKey = key;
      l.state = state;
      if (import.meta.env.DEV) window.__soundpadRibbon = { frames: l.frames, state, key };
    }
    function paint(
      level: number,
      t: number,
      ripples: { t0: number; amp: number }[],
      alpha: number,
    ) {
      if (!context || !l.palette || !l.width || !l.height) return;
      const started = performance.now();
      const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      context.setTransform(dpr, 0, 0, dpr, 0, 0);
      const strands = degraded || matches(PHONE) ? 8 : 14;
      drawRibbon(
        context,
        { width: l.width, height: l.height, strands },
        {
          t,
          level,
          ripples: ripples.map((r) => ({ t0: r.t0 / 1000, amp: r.amp })),
          alpha,
          palette: l.palette,
        },
      );
      const spent = performance.now() - started;
      l.frames++;
      if (import.meta.env.DEV) {
        window.__soundpadRibbon = {
          frames: l.frames,
          state: l.state,
          key: element!.dataset.ribbonKey ?? '',
          level,
        };
        try {
          performance.measure('ribbon-draw', { start: started, end: started + spent });
          if (l.frames % 600 === 0) performance.clearMeasures('ribbon-draw');
        } catch {
          // Measures are a DEV aid only.
        }
      }
      l.drawTimes.push(spent);
      if (l.drawTimes.length > 30) l.drawTimes.shift();
      if (!degraded && l.drawTimes.length === 30) {
        const mean = l.drawTimes.reduce((a, b) => a + b, 0) / 30;
        if (mean > SLOW_DRAW_MS) degraded = true;
      }
    }
    /** Asks for the playing sound's peaks once, if nobody has (the player usually has). */
    function want() {
      const { playback: p, hostId: host } = props.current;
      const key = machine.current.key;
      if (!key || !host || !p.soundId || isComposed(p.soundId)) return;
      const id = host + ':' + p.soundId;
      if (l.requested.has(id) || peekWaveform(host, p.soundId)) return;
      l.requested.add(id);
      void requestWaveform(host, p.soundId).catch(() => {});
    }
    function tick(now: number) {
      l.frame = 0;
      const { playback: p, hostId: host, active: shown } = props.current;
      if (!shown || !l.visible || !l.inView) return; // nothing drawn; restarts when shown again
      want();
      const entry = p.soundId ? peekWaveform(host, p.soundId) : undefined;
      const dt = l.lastTick ? Math.min(250, now - l.lastTick) : 0;
      l.lastTick = now;
      const result = machine.current.frame(entry, now, dt);
      const key = result.key ?? '';
      if (l.still) {
        // One frame per state change: idle flat, anything playing frozen at a lifted level.
        const playing = result.mode !== 'idle';
        setState('still', key);
        const signature = `still:${playing}:${l.width}x${l.height}`;
        if (l.drawn !== signature) {
          l.drawn = signature;
          paint(playing ? STILL_LEVEL : 0, 0, [], STATE_ALPHA.still);
        }
        l.lastTick = 0;
        return;
      }
      if (result.mode === 'idle' || result.mode === 'waiting') {
        setState(result.mode, key);
        const signature = `${result.mode}:${l.width}x${l.height}`;
        if (l.drawn !== signature) {
          l.drawn = signature;
          paint(result.mode === 'waiting' ? WAITING_LEVEL : 0, 0, [], STATE_ALPHA[result.mode]);
        }
        l.lastTick = 0;
        return;
      }
      // live or settling: animated, at most 30 (or 20) frames a second.
      setState(result.mode, key);
      const interval = degraded ? SLOW_FRAME_MS : FRAME_MS;
      if (now - l.lastDraw >= interval) {
        l.lastDraw = now;
        l.drawn = '';
        paint(result.level, now / 1000, result.ripples, STATE_ALPHA[result.mode]);
      }
      l.frame = requestAnimationFrame(tick);
    }
    l.schedule = () => {
      if (!l.frame) l.frame = requestAnimationFrame(tick);
    };

    // The canvas follows its box (never resized per frame), at most 1.5 device pixels per CSS pixel.
    const size = new ResizeObserver(() => {
      const box = element.getBoundingClientRect();
      const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      l.width = box.width;
      l.height = box.height;
      surface.width = Math.max(1, Math.round(box.width * dpr));
      surface.height = Math.max(1, Math.round(box.height * dpr));
      l.drawn = '';
      l.schedule();
      clear();
    });
    size.observe(element);
    // The mask keeps the ribbon faint behind the title and the buttons (spec 6.2).
    const header = element.parentElement;
    function clear() {
      if (!header) return;
      // On a phone the ribbon has its own band under the title: nothing to keep clear.
      if (matches(PHONE)) {
        element!.style.setProperty('--ribbon-clear-l', '0%');
        element!.style.setProperty('--ribbon-clear-r', '100%');
        return;
      }
      const box = header.getBoundingClientRect();
      if (!box.width) return;
      const title = element!.nextElementSibling as HTMLElement | null;
      const actions = header.querySelector<HTMLElement>(':scope > .board-actions');
      const left = title ? title.getBoundingClientRect() : null;
      const right = actions?.getBoundingClientRect();
      const percent = (x: number) =>
        `${Math.max(0, Math.min(100, ((x - box.left) / box.width) * 100)).toFixed(2)}%`;
      element!.style.setProperty('--ribbon-clear-l', left ? percent(left.left + left.width) : '0%');
      element!.style.setProperty(
        '--ribbon-clear-r',
        right && right.width ? percent(right.left) : '100%',
      );
    }
    const layout = new ResizeObserver(clear);
    if (header) {
      layout.observe(header);
      for (const child of Array.from(header.children)) if (child !== element) layout.observe(child);
    }
    const view = new IntersectionObserver((entries) => {
      l.inView = entries.some((e) => e.isIntersecting);
      l.lastTick = 0;
      l.schedule();
    });
    view.observe(element);
    const visibility = () => {
      l.visible = !document.hidden;
      l.lastTick = 0;
      l.schedule();
    };
    document.addEventListener('visibilitychange', visibility);
    visibility();
    // Peaks that arrive (or finish analysing) can turn "waiting" into "live".
    const stopPeaks = subscribeWaveforms(() => l.schedule());
    // A theme or contrast change repaints in the new colours.
    const theme = new MutationObserver(() => {
      l.palette = palette(element);
      l.drawn = '';
      l.schedule();
    });
    theme.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme', 'data-contrast'],
    });
    l.schedule();
    return () => {
      cancelAnimationFrame(l.frame);
      l.frame = 0;
      l.schedule = () => {};
      size.disconnect();
      layout.disconnect();
      view.disconnect();
      theme.disconnect();
      stopPeaks();
      document.removeEventListener('visibilitychange', visibility);
    };
  }, [off]);

  // A change of still/animated or of the shown section repaints once.
  useEffect(() => {
    loop.current.drawn = '';
    loop.current.lastTick = 0;
    loop.current.schedule();
  }, [still, active]);

  return (
    <div
      ref={wrapper}
      className="header-ribbon"
      aria-hidden="true"
      data-ribbon-state={off ? 'off' : still ? 'still' : 'idle'}
      data-ribbon-key=""
    >
      {!off && <canvas ref={canvas} />}
    </div>
  );
}
