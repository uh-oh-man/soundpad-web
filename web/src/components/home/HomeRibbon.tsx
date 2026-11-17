import { useEffect, useRef } from 'react';
import { drawRibbon, type RibbonPalette } from '../../ribbon/draw';
import { follow, freshFollow, RIPPLE_GAP_MS, RIPPLE_LIFE_MS } from '../../ribbon/envelope';
import { useReducedMotion } from '../../motion';
import { usePref } from '../../prefs';
import { onPulse } from './pulse';

/**
 * The app's header ribbon, borrowed for the start page (homepage): the same strands, drawn by
 * ribbon/draw.ts, flow across the hero out of the pretend soundboard's Now playing bar, and a pad press
 * lifts them and sends a ripple along. Decorative (aria-hidden, no pointer events). It draws only while
 * on screen and the page is visible, at most 30 frames a second (an idle drift, cheap: 7 strands of 48
 * points). Under reduced motion or Header animation: Still it draws one still frame per change, and Off
 * hides it.
 */
const CREST_AT = 0.62;
const FRAME_MS = 1000 / 30;

function palette(element: Element): RibbonPalette {
  const style = getComputedStyle(element);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    deep: read('--brand-violet', '#a66bff'),
    strand: read('--brand-cyan', '#3ff0ff'),
    crest: read('--ribbon-crest', '#d6f0ff'),
  };
}

/**
 * `anchor` is the element the crest sits on; with `at="start"` the crest sits just outside its left
 * edge instead, so the strands seem to flow out of it (the soundboard's Now playing bar).
 */
export function HomeRibbon({
  anchor,
  at = 'center',
  clear,
}: {
  anchor: string;
  at?: 'center' | 'start';
  /** Text the strands must stay clear of (hidden left of its right edge). */
  clear?: string;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const reduced = useReducedMotion();
  const [animation] = usePref('headerAnimation');
  const off = animation === 'off';
  const still = reduced || animation === 'still';

  useEffect(() => {
    const element = canvas.current;
    const host = element?.parentElement;
    if (!element || !host || off) return;
    const ctx = element.getContext('2d');
    if (!ctx) return;
    const state = freshFollow();
    let target = 0;
    let width = 0;
    let height = 0;
    let colors = palette(element);
    let raf = 0;
    let last = 0;
    let visible = true;
    let stillLift: number | undefined;
    const started = performance.now();

    // Sized so the bell's crest (62 % of the width) sits on the hub, and centred on it vertically.
    const place = () => {
      const hub = document.querySelector(anchor);
      const box = host.getBoundingClientRect();
      if (!hub || !box.width) return;
      const r = hub.getBoundingClientRect();
      const cx = (at === 'start' ? r.left - 24 : r.left + r.width / 2) - box.left;
      const cy = r.top + r.height / 2 - box.top;
      width = Math.min(box.width, Math.max(cx / CREST_AT, box.width * 0.6));
      height = Math.min(320, Math.max(180, r.height * 1.4));
      const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      element.width = Math.round(width * dpr);
      element.height = Math.round(height * dpr);
      element.style.width = `${width}px`;
      element.style.height = `${height}px`;
      element.style.top = `${cy - height / 2}px`;
      // Never behind words: the strands appear only to the right of the text column, when it is beside.
      const copy = clear ? document.querySelector(clear)?.getBoundingClientRect() : undefined;
      const from = copy && copy.right < r.left ? copy.right - box.left + 8 : 0;
      element.style.maskImage = from
        ? `linear-gradient(90deg, transparent ${from}px, var(--surface-0) ${from + 70}px)`
        : '';
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      colors = palette(element);
    };

    const draw = (now: number, level: number) => {
      const t = (now - started) / 1000;
      drawRibbon(
        ctx,
        { width, height, strands: 7 },
        {
          t: still ? 1.7 : t,
          level,
          ripples: still
            ? []
            : state.ripples.map((r) => ({ t0: (r.t0 - started) / 1000, amp: r.amp })),
          alpha: still ? 1.2 : 0.75 + 0.5 * level,
          palette: colors,
        },
      );
    };

    const frame = (now: number) => {
      raf = 0;
      if (!visible || document.hidden) return;
      if (now - last >= FRAME_MS) {
        const dt = last ? now - last : FRAME_MS;
        last = now;
        follow(state, target, dt);
        state.ripples = state.ripples.filter((r) => now - r.t0 < RIPPLE_LIFE_MS);
        draw(now, 0.3 + 0.7 * state.level);
      }
      raf = requestAnimationFrame(frame);
    };
    const run = () => {
      if (still) draw(performance.now(), stillLift === undefined ? 0.25 : 0.7);
      else if (!raf && visible && !document.hidden) raf = requestAnimationFrame(frame);
    };

    place();
    run();
    const resize = new ResizeObserver(() => {
      place();
      if (still) run();
    });
    resize.observe(host);
    const seen = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting;
      run();
    });
    seen.observe(host);
    const shown = () => run();
    document.addEventListener('visibilitychange', shown);

    let release: number | undefined;
    const stop = onPulse((strength) => {
      const now = performance.now();
      if (still) {
        // A still picture: the strands stand taller while a sound plays, then settle. No movement.
        window.clearTimeout(stillLift);
        stillLift = window.setTimeout(() => {
          stillLift = undefined;
          run();
        }, 900);
        run();
        return;
      }
      target = Math.min(1, 0.55 + 0.45 * strength);
      if (now - state.lastRipple >= RIPPLE_GAP_MS) {
        state.ripples.push({ t0: now, amp: 0.55 * strength });
        state.lastRipple = now;
      }
      window.clearTimeout(release);
      release = window.setTimeout(() => (target = 0), 320);
      run();
    });

    return () => {
      cancelAnimationFrame(raf);
      window.clearTimeout(release);
      window.clearTimeout(stillLift);
      resize.disconnect();
      seen.disconnect();
      document.removeEventListener('visibilitychange', shown);
      stop();
    };
  }, [anchor, at, clear, off, still]);

  if (off) return null;
  return <canvas ref={canvas} className="home-ribbon" aria-hidden="true" />;
}
