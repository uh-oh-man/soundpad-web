import { useCallback, useEffect, useRef, useState, type PointerEvent } from 'react';
import { Mic2, Play, RefreshCw, Scissors, Square, X } from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';

const peekSchema = z.object({
  end: z.number().int().nonnegative(),
  rate: z.number().int().positive(),
  windowMs: z.number().int().positive(),
  levels: z.array(z.number().int().min(0).max(1000)).max(2000),
  active: z.boolean(),
});
const sourcesSchema = z.object({
  sources: z
    .array(z.object({ id: z.string().max(512), name: z.string().max(600), kind: z.string() }))
    .max(500),
});
export type MimicPeek = z.infer<typeof peekSchema>;
type Selection = { from: number; to: number };

const POLL_MS = 500;
const MIN_MS = 150;
const QUICK_SECONDS = [3, 5, 10];

/** Seconds before "now" for a position in the window, e.g. "−4.2 s". */
export const ago = (ms: number, windowMs: number) => `−${((windowMs - ms) / 1000).toFixed(1)} s`;

/** Clamp and order a dragged range so it stays inside the window and meets the minimum length. */
export function normalizeSelection(a: number, b: number, windowMs: number): Selection | null {
  const from = Math.max(0, Math.min(a, b));
  const to = Math.min(windowMs, Math.max(a, b));
  return to - from >= MIN_MS ? { from: Math.round(from), to: Math.round(to) } : null;
}

function useCanvasColors() {
  const style = getComputedStyle(document.documentElement);
  return {
    wave: style.getPropertyValue('--wave').trim() || '#7c9fca',
    accent: style.getPropertyValue('--accent').trim() || '#7cb5ff',
    line: style.getPropertyValue('--line').trim() || '#303b4a',
  };
}

function MimicWave({
  peek,
  selection,
  onSelect,
}: {
  peek: MimicPeek;
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
  }, [peek, shown, colors.accent, colors.wave, colors.line]);
  const at = (e: PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return (Math.min(Math.max(e.clientX - box.left, 0), box.width) / box.width) * peek.windowMs;
  };
  return (
    <div className="mimic-wave">
      <canvas
        ref={canvas}
        role="img"
        aria-label={`Mimic buffer, last ${peek.windowMs / 1000} seconds. Drag to select.`}
        onPointerDown={(e) => {
          e.currentTarget.setPointerCapture(e.pointerId);
          drag.current = at(e);
          setDraft(null);
        }}
        onPointerMove={(e) => {
          if (drag.current !== null)
            setDraft({ from: Math.min(drag.current, at(e)), to: Math.max(drag.current, at(e)) });
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

export function MimicPanel({
  state,
  host,
  connected,
  canPlay,
}: {
  state: Snapshot;
  host: boolean;
  connected: boolean;
  canPlay: boolean;
}) {
  const mimic = state.mimic;
  const active = !!mimic?.active;
  const [sources, setSources] = useState<z.infer<typeof sourcesSchema>['sources']>([]);
  const [source, setSource] = useState('');
  const [peek, setPeek] = useState<MimicPeek | null>(null);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const allowed = host || state.permissions.guestMimic === true;

  const loadSources = useCallback(async () => {
    try {
      const parsed = sourcesSchema.parse(await controller.run('mimicSources'));
      setSources(parsed.sources);
      setSource((current) =>
        parsed.sources.some((s) => s.id === current) ? current : (parsed.sources[0]?.id ?? ''),
      );
    } catch {
      /* Controller shows the error. */
    }
  }, []);
  useEffect(() => {
    if (host && connected) void loadSources();
  }, [host, connected, loadSources]);

  // Live view: refresh while listening and nothing is selected. A selection freezes the view so the
  // chosen moment stays put; the companion keeps extra history so it is still there on "Mimic it".
  const frozen = !!selection;
  useEffect(() => {
    if (!active || !allowed || !connected || frozen) return;
    let live = true;
    let timer: ReturnType<typeof setTimeout>;
    const tick = async () => {
      try {
        const next = peekSchema.parse(await controller.peekMimic());
        if (live) setPeek(next);
      } catch {
        /* Keep the last view; the next tick retries. */
      }
      if (live) timer = setTimeout(tick, POLL_MS);
    };
    void tick();
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [active, allowed, connected, frozen]);
  useEffect(() => {
    if (!active) {
      setPeek(null);
      setSelection(null);
    }
  }, [active]);

  async function clip(play: boolean) {
    if (!peek || !selection) return;
    setBusy(true);
    try {
      const result = (await controller.run('mimicClip', {
        mimicEnd: peek.end,
        positionMs: selection.from,
        endMs: selection.to,
        ...(name.trim() ? { name: name.trim() } : {}),
      })) as { assetId?: string } | undefined;
      controller.notify('Mimicked! The clip is in Temp for this session.', 'success');
      setSelection(null);
      setName('');
      if (play && result?.assetId)
        await controller.run('audioPlay', { assetId: result.assetId, orientation: 'normal' });
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mimic-panel">
      <p className="mimic-intro">
        Mimic keeps the last 30 seconds of one app or output device on the host's PC, in memory
        only. Select what someone just said and turn it into a sound. It never records a microphone,
        and everyone in the session can see when it is listening.
      </p>
      <div className={`mimic-status ${active ? 'live' : ''}`} role="status">
        <i />
        {active ? `Listening to ${mimic?.source ?? 'a source'}` : (mimic?.status ?? 'Off')}
      </div>
      {host && (
        <div className="mimic-source">
          <label>
            Listen to
            <select
              aria-label="Mimic source"
              value={source}
              disabled={busy || !sources.length}
              onChange={(e) => setSource(e.target.value)}
            >
              {sources.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </label>
          <button
            className="icon-button"
            aria-label="Refresh Mimic sources"
            title="Refresh sources"
            disabled={busy}
            onClick={() => void loadSources()}
          >
            <RefreshCw size={16} />
          </button>
          {active ? (
            <button
              disabled={busy}
              onClick={async () => {
                setBusy(true);
                await controller.run('mimicStop').catch(() => {});
                setBusy(false);
              }}
            >
              <Square size={15} /> Stop listening
            </button>
          ) : (
            <button
              className="primary"
              disabled={busy || !source || !connected}
              onClick={async () => {
                setBusy(true);
                await controller.run('mimicStart', { sourceId: source }).catch(() => {});
                setBusy(false);
              }}
            >
              <Mic2 size={15} /> Start listening
            </button>
          )}
        </div>
      )}
      {!allowed ? (
        <p className="muted">The host has not enabled Mimic for guests.</p>
      ) : !active ? (
        <p className="muted">
          {host ? 'Choose a source and start listening.' : 'Waiting for the host to start Mimic.'}
        </p>
      ) : peek ? (
        <>
          <MimicWave peek={peek} selection={selection} onSelect={setSelection} />
          <div className="mimic-quick" role="group" aria-label="Quick select">
            {QUICK_SECONDS.map((s) => (
              <button
                key={s}
                onClick={() =>
                  setSelection({ from: Math.max(0, peek.windowMs - s * 1000), to: peek.windowMs })
                }
              >
                Last {s} s
              </button>
            ))}
            {selection && (
              <button onClick={() => setSelection(null)}>
                <X size={14} /> Clear
              </button>
            )}
          </div>
          {selection ? (
            <div className="mimic-actions">
              <span className="mimic-range">
                {ago(selection.from, peek.windowMs)} → {ago(selection.to, peek.windowMs)} ·{' '}
                {((selection.to - selection.from) / 1000).toFixed(1)} s
              </span>
              <input
                aria-label="Clip name"
                placeholder="Name (optional)"
                maxLength={100}
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
              <button disabled={busy} onClick={() => void clip(false)}>
                <Scissors size={15} /> Mimic it
              </button>
              <button
                className="primary"
                disabled={busy || !canPlay}
                onClick={() => void clip(true)}
              >
                <Play size={15} /> Mimic &amp; play
              </button>
            </div>
          ) : (
            <p className="muted">Drag across the waveform, or use a quick select.</p>
          )}
        </>
      ) : (
        <p className="muted">Loading the live buffer…</p>
      )}
    </div>
  );
}
