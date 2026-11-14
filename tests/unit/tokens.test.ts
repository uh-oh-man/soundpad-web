import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

/**
 * The token contrast table (Stage G spec 2.2), read from web/src/tokens.css itself so a changed value
 * cannot slip past: text needs 4.5:1, control boundaries and focus 3:1, on every surface of every
 * theme, and text stays readable over the header ribbon at its 0.20 alpha cap.
 */
const css = readFileSync(new URL('../../web/src/tokens.css', import.meta.url), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

/** `--name: #hex` pairs of the first top-level rule whose selector list includes `selector`. */
function block(selector: string): Record<string, string> {
  const rule = /(?<=^|\})\s*([^{}@]*?)\{([^{}]*)\}/g;
  for (const m of css.matchAll(rule)) {
    const selectors = m[1].split(',').map((s) => s.trim().replace(/"/g, "'"));
    if (!selectors.includes(selector)) continue;
    const out: Record<string, string> = {};
    for (const d of m[2].matchAll(/(--[\w-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;/g)) out[d[1]] = d[2];
    return out;
  }
  throw Error(`No ${selector} block in tokens.css`);
}
// Theme blocks inherit what they leave out from the default (:root) and the shared :root block.
const shared = (() => {
  const out: Record<string, string> = {};
  for (const m of css.matchAll(/(?<=^|\})\s*:root\s*\{([^{}]*)\}/g))
    for (const d of m[1].matchAll(/(--[\w-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;/g)) out[d[1]] = d[2];
  return out;
})();
const blue = { ...shared, ...block(':root') };
const themes: Record<string, Record<string, string>> = {
  blue,
  purple: { ...blue, ...block(":root[data-theme='purple']") },
  ocean: { ...blue, ...block(":root[data-theme='ocean']") },
};

// The helpers of the spec's contrast script (scratchpad stage-g/contrast-spec.js), inline.
const hex = (h: string) =>
  [0, 2, 4].map((i) => parseInt(h.replace('#', '').slice(i, i + 2), 16) / 255);
const lin = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const lum = (h: string) => {
  const [r, g, b] = hex(h).map(lin);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const contrast = (a: string, b: string) => {
  const x = lum(a),
    y = lum(b);
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
};
const mix = (a: string, b: string, t: number) => {
  const A = hex(a),
    B = hex(b);
  return (
    '#' +
    A.map((v, i) =>
      Math.round((v * (1 - t) + B[i] * t) * 255)
        .toString(16)
        .padStart(2, '0'),
    ).join('')
  );
};

const SURFACES = ['--surface-0', '--surface-1', '--surface-2', '--surface-3', '--surface-hover'];
const RIBBON_CAP = 0.2;

describe.each(Object.entries(themes))('%s theme tokens', (_name, t) => {
  const v = (name: string) => {
    expect(t[name], `${name} is defined as a hex colour`).toMatch(/^#[0-9a-fA-F]{6}$/);
    return t[name];
  };
  it.each(['--text-primary', '--text-muted', '--text-dim', '--accent', '--accent-text'])(
    '%s reads at 4.5:1 on every surface',
    (token) => {
      for (const s of SURFACES)
        expect(contrast(v(token), v(s)), `${token} on ${s}`).toBeGreaterThanOrEqual(4.5);
    },
  );
  it('control boundaries reach 3:1 on every surface, and focus on every surface it sits on', () => {
    for (const s of SURFACES)
      expect(contrast(v('--line-strong'), v(s)), `--line-strong on ${s}`).toBeGreaterThanOrEqual(3);
    for (const s of SURFACES.slice(0, 4))
      expect(contrast(v('--focus'), v(s)), `--focus on ${s}`).toBeGreaterThanOrEqual(3);
  });
  it('white reads on the accent fill and its hover, and accent text on the soft accent', () => {
    expect(contrast('#ffffff', v('--accent-fill'))).toBeGreaterThanOrEqual(4.5);
    expect(contrast('#ffffff', v('--accent-fill-hover'))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(v('--accent-text'), v('--accent-soft'))).toBeGreaterThanOrEqual(4.5);
  });
  it('text stays readable over the ribbon at its 0.20 alpha cap', () => {
    const behind = mix(v('--surface-1'), v('--ribbon-crest'), RIBBON_CAP);
    for (const token of ['--text-primary', '--text-muted', '--accent-text'])
      expect(contrast(v(token), behind), `${token} over the ribbon`).toBeGreaterThanOrEqual(4.5);
  });
  it('voice colours stand out on tiles and cards', () => {
    for (let i = 1; i <= 8; i++)
      expect(contrast(v(`--voice-${i}`), v('--surface-2')), `--voice-${i}`).toBeGreaterThanOrEqual(
        5.8,
      );
  });
});

describe('shared status colours', () => {
  it('Stop and Panic fills keep their text readable', () => {
    expect(contrast(blue['--text-on-accent'], blue['--danger-fill'])).toBeGreaterThanOrEqual(4.5);
    expect(contrast(blue['--text-on-warning'], blue['--warning'])).toBeGreaterThanOrEqual(4.5);
  });
  it('danger, warning and success read as text on the page and panels in every theme', () => {
    for (const t of Object.values(themes))
      for (const token of ['--danger', '--warning', '--success'])
        for (const s of SURFACES.slice(0, 3))
          expect(contrast(t[token], t[s]), `${token} on ${s}`).toBeGreaterThanOrEqual(4.5);
  });
});
