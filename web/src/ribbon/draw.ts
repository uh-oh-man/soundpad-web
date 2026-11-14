/**
 * Draws the header ribbon (Stage G spec 6.5): layered strands with a bright crest at about 62 % of the
 * width, lifted by the level and nudged by travelling ripples. Pure apart from the 2D context it is
 * given: no shadows or filters, and every point stays within 0.42 of the height from the centre line.
 */

export type RibbonPalette = { deep: string; strand: string; crest: string };
export type RibbonGeom = { width: number; height: number; strands: number };
export type RibbonFrame = {
  /** Seconds. */
  t: number;
  /** 0..1. */
  level: number;
  /** Ripple start times in seconds, and their size. */
  ripples: { t0: number; amp: number }[];
  /** The state's brightness: idle 0.35, waiting 0.5, live 1, still 1.6. */
  alpha: number;
  palette: RibbonPalette;
};

/** The 2D context calls the ribbon makes (a stub in the unit tests). */
export type RibbonContext = Pick<
  CanvasRenderingContext2D,
  | 'clearRect'
  | 'beginPath'
  | 'moveTo'
  | 'lineTo'
  | 'quadraticCurveTo'
  | 'stroke'
  | 'createLinearGradient'
  | 'save'
  | 'restore'
> & {
  lineWidth: number;
  strokeStyle: string | CanvasGradient | CanvasPattern;
  globalAlpha: number;
  globalCompositeOperation: GlobalCompositeOperation;
  lineCap: CanvasLineCap;
  lineJoin: CanvasLineJoin;
};

const POINTS = 48;
const CREST_AT = 0.62;
const BELL_WIDTH = 0.22;
const MAX_LIFT = 0.42;
const TAU = Math.PI * 2;

export const bell = (x: number, width: number) =>
  Math.exp(-((x / width - CREST_AT) ** 2) / (2 * BELL_WIDTH ** 2));

/** One strand's points, left to right, in CSS pixels. */
export function strandPoints(geom: RibbonGeom, frame: RibbonFrame, i: number) {
  const { width: W, height: H, strands: N } = geom;
  const L = Math.max(0, Math.min(1, frame.level));
  const cy = 0.55 * H;
  const phase = i * 0.37;
  const spread = (i - (N - 1) / 2) * (H * 0.018) * (0.35 + L);
  const amp = (0.1 + 0.32 * L) * H;
  const speed = W / 0.9;
  const points: { x: number; y: number }[] = [];
  for (let k = 0; k < POINTS; k++) {
    const x = (k / (POINTS - 1)) * W;
    const t = frame.t;
    const w =
      Math.sin(TAU * (1.3 * (x / W) + 0.11 * t) + phase) *
      (0.6 + 0.4 * Math.sin(TAU * (0.7 * (x / W) - 0.07 * t) + 1.7 * phase));
    let r = 0;
    for (const ripple of frame.ripples) {
      const age = t - ripple.t0;
      if (age < 0) continue;
      r +=
        ripple.amp * Math.exp(-((x - speed * age) ** 2) / (0.08 * W) ** 2) * Math.exp(-age / 0.4);
    }
    let y = cy + bell(x, W) * (amp * w + r * H * 0.25) + spread;
    y = Math.max(cy - MAX_LIFT * H, Math.min(cy + MAX_LIFT * H, y));
    points.push({ x, y });
  }
  return points;
}

/** #rgb or #rrggbb to [r, g, b]; anything else is the default strand blue. */
export function parseColor(value: string): [number, number, number] {
  const hex = value.trim().replace(/^#/, '');
  const full =
    hex.length === 3
      ? hex
          .split('')
          .map((c) => c + c)
          .join('')
      : hex.slice(0, 6);
  if (!/^[0-9a-f]{6}$/i.test(full)) return [61, 139, 255];
  return [0, 2, 4].map((at) => parseInt(full.slice(at, at + 2), 16)) as [number, number, number];
}
const rgba = ([r, g, b]: [number, number, number], a: number) =>
  `rgba(${r}, ${g}, ${b}, ${Math.max(0, Math.min(1, a)).toFixed(3)})`;
const mix = (a: [number, number, number], b: [number, number, number], f: number) =>
  a.map((v, i) => Math.round(v + (b[i] - v) * f)) as [number, number, number];

function path(ctx: RibbonContext, points: { x: number; y: number }[]) {
  ctx.beginPath();
  ctx.moveTo(points[0].x, points[0].y);
  for (let k = 1; k < points.length - 1; k++) {
    const mx = (points[k].x + points[k + 1].x) / 2,
      my = (points[k].y + points[k + 1].y) / 2;
    ctx.quadraticCurveTo(points[k].x, points[k].y, mx, my);
  }
  const last = points[points.length - 1];
  ctx.lineTo(last.x, last.y);
}

/** Clears and draws one frame. The context's transform maps CSS pixels to the canvas already. */
export function drawRibbon(ctx: RibbonContext, geom: RibbonGeom, frame: RibbonFrame) {
  const { width: W, height: H, strands: N } = geom;
  ctx.clearRect(0, 0, W, H);
  if (W <= 0 || H <= 0 || N <= 0) return;
  const L = Math.max(0, Math.min(1, frame.level));
  const deep = parseColor(frame.palette.deep),
    strand = parseColor(frame.palette.strand),
    crest = parseColor(frame.palette.crest);
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  // Deep at the edges to strand blue at the crest, brighter where the bell lifts the strands.
  const gradient = ctx.createLinearGradient(0, 0, W, 0);
  for (let s = 0; s <= 6; s++) {
    const f = s / 6,
      b = bell(f * W, W);
    gradient.addColorStop(f, rgba(mix(deep, strand, b), (0.22 + 0.4 * L * b) * frame.alpha));
  }
  const crestIndex = Math.floor(N / 2);
  for (let i = 0; i < N; i++) {
    if (i === crestIndex) continue;
    ctx.globalAlpha = 1;
    ctx.strokeStyle = gradient;
    ctx.lineWidth = 1 + 0.5 * L;
    path(ctx, strandPoints(geom, frame, i));
    ctx.stroke();
  }
  // The crest: a soft 6 px glow, then the bright 1.5 px line over it.
  const points = strandPoints(geom, frame, crestIndex);
  ctx.strokeStyle = rgba(strand, 1);
  ctx.globalAlpha = Math.min(0.9, 0.15 * frame.alpha);
  ctx.lineWidth = 6;
  path(ctx, points);
  ctx.stroke();
  ctx.strokeStyle = rgba(crest, 1);
  ctx.globalAlpha = Math.min(0.9, (0.45 + 0.5 * L) * frame.alpha);
  ctx.lineWidth = 1.5;
  path(ctx, points);
  ctx.stroke();
  ctx.restore();
}
