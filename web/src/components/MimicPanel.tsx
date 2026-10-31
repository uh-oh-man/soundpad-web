import { useCallback, useEffect, useRef, useState, type PointerEvent } from 'react';
import {
  Captions,
  Download,
  Keyboard,
  Mic2,
  Play,
  RefreshCw,
  Scissors,
  Square,
  X,
} from 'lucide-react';
import { z } from 'zod';
import { controller } from '../controller';
import type { Snapshot } from '../protocol';

const peekSchema = z.object({
  end: z.number().int().nonnegative(),
  rate: z.number().int().positive(),
  windowMs: z.number().int().positive(),
  levels: z.array(z.number().int().min(0).max(1000)).max(2000),
  active: z.boolean(),
  transcript: z
    .array(
      z.object({ startMs: z.number().int(), endMs: z.number().int(), text: z.string().max(2000) }),
    )
    .max(500)
    .nullish(),
});
const sourcesSchema = z.object({
  sources: z
    .array(z.object({ id: z.string().max(512), name: z.string().max(600), kind: z.string() }))
    .max(500),
});
export type MimicPeek = z.infer<typeof peekSchema>;
export type TranscriptLine = NonNullable<MimicPeek['transcript']>[number];
type Selection = { from: number; to: number };

/** Keyboard step for adjusting a selection edge: 100 ms, or 1 s with Ctrl. */
export const NUDGE_MS = 100;

/**
 * Arrow keys on the waveform: Left/Right move the end, Shift+Left/Right move the start.
 * With no selection, the first press selects the last second.
 */
export function nudgeSelection(
  selection: Selection | null,
  key: 'ArrowLeft' | 'ArrowRight',
  moveStart: boolean,
  windowMs: number,
  step = NUDGE_MS,
): Selection {
  if (!selection) return { from: windowMs - 1000, to: windowMs };
  const delta = key === 'ArrowLeft' ? -step : step;
  if (moveStart)
    return {
      ...selection,
      from: Math.min(Math.max(0, selection.from + delta), selection.to - MIN_MS),
    };
  return {
    ...selection,
    to: Math.max(Math.min(windowMs, selection.to + delta), selection.from + MIN_MS),
  };
}

/** Selection covering a transcript line, with a little padding so words aren't clipped. */
export function lineSelection(line: TranscriptLine, windowMs: number): Selection {
  return {
    from: Math.max(0, line.startMs - 150),
    to: Math.min(windowMs, Math.max(line.endMs + 150, line.startMs + MIN_MS)),
  };
}

/** Words spoken inside a selection, usable as the clip's default name. */
export function transcriptFor(lines: TranscriptLine[], selection: Selection) {
  const text = lines
    .filter((l) => Math.min(l.endMs, selection.to) - Math.max(l.startMs, selection.from) > 0)
    .map((l) => l.text.trim())
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > 100 ? text.slice(0, 99).trimEnd() + '…' : text;
}

const POLL_MS = 500;
const MIN_MS = 150;
/** How close (in CSS pixels) the pointer must be to a selection edge to grab its handle. */
const EDGE_PX = 8;
const QUICK_SECONDS = [3, 5, 10];
/** Keys that select the last N seconds, like Voice Replay Studio's F6 "save the last few seconds". */
export const QUICK_KEYS: Record<string, number> = { '3': 3, '5': 5, '0': 10 };

/** Seconds before "now" for a position in the window, e.g. "−4.2 s". */
export const ago = (ms: number, windowMs: number) =>
  windowMs - ms < 50 ? 'now' : `−${((windowMs - ms) / 1000).toFixed(1)} s`;

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
  const nameField = useRef<HTMLInputElement>(null);
  const [focusName, setFocusName] = useState(false);
  useEffect(() => {
    if (focusName && selection) {
      nameField.current?.focus();
      setFocusName(false);
    }
  }, [focusName, selection]);
  // The checkbox shows the requested state until the companion's snapshot confirms it.
  const [wantTranscript, setWantTranscript] = useState<boolean | null>(null);
  const [wantHotkey, setWantHotkey] = useState<boolean | null>(null);
  useEffect(() => {
    if (wantHotkey !== null && wantHotkey === !!mimic?.hotkey) setWantHotkey(null);
  }, [wantHotkey, mimic?.hotkey]);
  const transcribing = wantTranscript ?? !!mimic?.transcribing;
  useEffect(() => {
    if (wantTranscript !== null && wantTranscript === !!mimic?.transcribing)
      setWantTranscript(null);
  }, [wantTranscript, mimic?.transcribing]);
  const allowed = host || state.permissions.guestMimic === true;
  const lines = (peek?.transcript ?? []).filter((l) => l.endMs > 0);
  const suggestion = peek && selection ? transcriptFor(lines, selection) : '';

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
        ...((name.trim() || suggestion) && { name: name.trim() || suggestion }),
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

  // Shortcuts work anywhere while the window is open: focus often leaves it when buttons swap
  // (Start/Stop listening). Capture phase runs before the dialog's own Escape handler.
  const shortcut = useRef<(e: KeyboardEvent) => void>(() => {});
  useEffect(() => {
    shortcut.current = (e: KeyboardEvent) => {
      if (!peek || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
      const typing = e.target instanceof HTMLElement && e.target.matches('input, select, textarea');
      if (e.key === 'Escape' && selection) {
        e.preventDefault(); // clear the selection first; the next Escape closes the window
        e.stopPropagation();
        setSelection(null);
      } else if (
        e.key === 'Enter' &&
        selection &&
        !busy &&
        !(e.target instanceof HTMLButtonElement) &&
        (!typing || (e.target instanceof HTMLInputElement && e.target.type === 'text'))
      ) {
        e.preventDefault();
        if (!e.shiftKey || canPlay) void clip(e.shiftKey);
      } else if (!typing && QUICK_KEYS[e.key]) {
        e.preventDefault();
        setFocusName(true);
        setSelection({
          from: Math.max(0, peek.windowMs - QUICK_KEYS[e.key] * 1000),
          to: peek.windowMs,
        });
      }
    };
  });
  useEffect(() => {
    const listener = (e: KeyboardEvent) => shortcut.current(e);
    document.addEventListener('keydown', listener, true);
    return () => document.removeEventListener('keydown', listener, true);
  }, []);

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
      {host && (
        <div className="mimic-transcribe">
          <Captions size={16} />
          {mimic?.transcriptModel ? (
            <label>
              <input
                type="checkbox"
                aria-label="Transcribe Mimic audio"
                checked={transcribing}
                disabled={busy}
                onChange={async (e) => {
                  const on = e.target.checked;
                  setWantTranscript(on);
                  setBusy(true);
                  await controller
                    .run('mimicTranscribe', { action: on ? 'enable' : 'disable' })
                    .catch(() => setWantTranscript(null));
                  setBusy(false);
                }}
              />
              Transcript
            </label>
          ) : (
            <button
              disabled={busy || mimic?.transcriptStatus?.startsWith('Downloading')}
              onClick={async () => {
                setBusy(true);
                await controller.run('mimicTranscribe', { action: 'download' }).catch(() => {});
                setBusy(false);
              }}
            >
              <Download size={15} /> Get transcripts (148 MB, once)
            </button>
          )}
          <small>
            {mimic?.transcriptStatus && mimic.transcriptStatus !== 'Off'
              ? mimic.transcriptStatus
              : 'Speech is recognized on this PC; only the text is shared.'}
          </small>
        </div>
      )}
      {host && (
        <div className="mimic-transcribe">
          <Keyboard size={16} />
          <label>
            <input
              type="checkbox"
              aria-label="F6 saves the last 10 seconds"
              checked={wantHotkey ?? !!mimic?.hotkey}
              disabled={busy}
              onChange={async (e) => {
                const on = e.target.checked;
                setWantHotkey(on);
                setBusy(true);
                await controller
                  .run('mimicHotkey', { action: on ? 'enable' : 'disable' })
                  .catch(() => setWantHotkey(null));
                setBusy(false);
              }}
            />
            F6 hotkey
          </label>
          <small>
            {mimic?.hotkeyStatus && mimic.hotkeyStatus !== 'Off'
              ? mimic.hotkeyStatus
              : 'Press F6 in any app on this PC to save the last 10 seconds, like Voice Replay Studio.'}
          </small>
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
          {transcribing && (
            <ol className="mimic-transcript" aria-label="Transcript">
              {lines.length ? (
                lines.map((line) => {
                  const chosen =
                    !!selection &&
                    Math.min(line.endMs, selection.to) - Math.max(line.startMs, selection.from) > 0;
                  return (
                    <li key={`${line.startMs}-${line.text}`}>
                      <button
                        aria-pressed={chosen}
                        onClick={() => {
                          setSelection(lineSelection(line, peek.windowMs));
                          setName('');
                        }}
                      >
                        <time>{ago(Math.max(0, line.startMs), peek.windowMs)}</time>
                        <span>{line.text}</span>
                      </button>
                    </li>
                  );
                })
              ) : (
                <li className="muted">Words will appear here a few seconds after they are said.</li>
              )}
            </ol>
          )}
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
                ref={nameField}
                aria-label="Clip name"
                placeholder={suggestion || 'Name (optional)'}
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
            <p className="muted">
              Drag across the waveform, click a transcript line, or press <kbd>3</kbd> <kbd>5</kbd>{' '}
              <kbd>0</kbd> for the last 3, 5 or 10 seconds. Then <kbd>Enter</kbd> mimics,{' '}
              <kbd>Shift</kbd>+<kbd>Enter</kbd> mimics and plays, <kbd>Esc</kbd> clears.
            </p>
          )}
        </>
      ) : (
        <p className="muted">Loading the live buffer…</p>
      )}
    </div>
  );
}
