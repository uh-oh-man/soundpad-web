import { useEffect, useRef, useState } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  Headphones,
  Play,
  Radio,
  Scissors,
  Share2,
  X,
} from 'lucide-react';
import { controller } from '../../controller';
import type { Snapshot } from '../../protocol';
import { supports } from '../../version';
import { usePreviewAudio } from '../../preview-audio';
import { guestHeld, HELD_BY_HOST, playback } from '../../playback';
import { ago, lastSeconds, QUICK_KEYS, QUICK_SECONDS, type Selection } from '../../mimic/selection';
import {
  behindLiveMs,
  clampViewEnd,
  DEFAULT_HISTORY_VIEW,
  formatClock,
  formatSpan,
  framesToMs,
  gapMarks,
  hiddenParts,
  jump,
  levelsSchema,
  oldestFrame,
  selectionFrames,
  selectionMs,
  sentences,
  timelineSchema,
  toPeaks,
  transcriptSchema,
  wallOf,
  WINDOW_MS,
  windowStart,
  wordSelected,
  wordSelection,
  wordsText,
  type FrameRange,
  type Timeline,
  type Word,
} from '../../mimic/history';
import { MimicWave } from './MimicWave';
import { MimicOverview } from './MimicOverview';

// Guests refresh half as often: their requests share the host page's and the companion's request budgets.
const POLL_MS = 500,
  GUEST_POLL_MS = 1000;
/** Bins of the main 30 s window (50 ms each) and of the overview. */
const MAIN_BINS = 600;
const OVERVIEW_BINS = 600;
const JUMP_SECONDS = 10;
const VIEW_KEY = 'soundpad-web:mimic-history-view';

/**
 * The Mimic window on a 2.38 companion: the rolling recording history. The main waveform shows the newest 10 s
 * (zoom 5/10/30 s), the overview the whole history; Back/Forward, dragging the overview and Go live move the view
 * while recording carries on. The transcript shows sentences of clickable words (click selects a word, Shift-click
 * a range to it). Guests see the history from when they joined, plus what the host shared.
 */
export function MimicHistory({
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
  const history = mimic?.history;
  const [timeline, setTimeline] = useState<Timeline | null>(null);
  const [follow, setFollow] = useState(true);
  const [viewEnd, setViewEnd] = useState(0);
  const [main, setMain] = useState<{ end: number; peaks: number[] } | null>(null);
  const [overview, setOverview] = useState<{ range: FrameRange; peaks: number[] } | null>(null);
  const [words, setWords] = useState<Word[]>([]);
  const [pending, setPending] = useState<FrameRange[]>([]);
  const [selection, setSelection] = useState<FrameRange | null>(null);
  const frozenBySelection = useRef(false);
  const [anchor, setAnchor] = useState<number | null>(null);
  const [hover, setHover] = useState<Word | null>(null);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const held = guestHeld(state, host);
  const live = timeline?.liveFrame ?? 0;
  const rate = timeline?.rate ?? 48000;
  const end = follow ? live : viewEnd;
  // The window the waveform on screen was fetched for: selections map to frames through it, so they match what was seen.
  const displayEnd = main?.end ?? end;
  const start = windowStart(displayEnd, rate);
  const sessionId = timeline?.sessionId ?? null;

  // Poll: the timeline every half second; the main levels with it (when live or the view moved), the transcript
  // every second and the overview every two. Recording never waits for any of it.
  const viewRef = useRef({ follow, viewEnd });
  viewRef.current = { follow, viewEnd };
  const [kick, setKick] = useState(0);
  useEffect(() => {
    if (!connected) return;
    let alive = true;
    let timer: ReturnType<typeof setTimeout>;
    let tick = 0;
    let shownEnd = -1;
    const run = async () => {
      try {
        const t = timelineSchema.parse(
          await controller.run('historyTimeline', {}, { quiet: true }),
        );
        if (!alive) return;
        setTimeline(t);
        if (t.sessionId) {
          const v = viewRef.current;
          const e = v.follow ? t.liveFrame : clampViewEnd(t, v.viewEnd);
          const s = windowStart(e, t.rate);
          const from = Math.max(0, s);
          if (e > from && (e !== shownEnd || tick % 4 === 0)) {
            const windowFrames = e - s;
            const lead = Math.round((MAIN_BINS * (from - s)) / windowFrames);
            const reply = levelsSchema.parse(
              await controller.run(
                'historyLevels',
                {
                  startFrame: from,
                  endFrame: e,
                  bins: Math.max(1, MAIN_BINS - lead),
                  sourceId: t.sessionId,
                },
                { quiet: true },
              ),
            );
            if (!alive) return;
            setMain({ end: e, peaks: [...new Array(lead).fill(0), ...toPeaks(reply.levels)] });
            shownEnd = e;
          }
          if (tick % 2 === 0 && e > from) {
            const reply = transcriptSchema.parse(
              await controller.run(
                'historyTranscript',
                { startFrame: from, endFrame: e, sourceId: t.sessionId },
                { quiet: true },
              ),
            );
            if (!alive) return;
            setWords(reply.words);
            setPending(reply.pending);
          }
          const oldest = oldestFrame(t);
          if (tick % 4 === 0 && t.liveFrame > oldest) {
            const reply = levelsSchema.parse(
              await controller.run(
                'historyLevels',
                {
                  startFrame: oldest,
                  endFrame: t.liveFrame,
                  bins: OVERVIEW_BINS,
                  sourceId: t.sessionId,
                },
                { quiet: true },
              ),
            );
            if (!alive) return;
            setOverview({
              range: { startFrame: oldest, endFrame: t.liveFrame },
              peaks: toPeaks(reply.levels),
            });
          }
        }
      } catch {
        /* Keep the last view; the next tick retries. */
      }
      tick++;
      if (alive) timer = setTimeout(run, host ? POLL_MS : GUEST_POLL_MS);
    };
    void run();
    return () => {
      alive = false;
      clearTimeout(timer);
    };
  }, [connected, kick, host]);

  /** Moves the view so it ends at `next` (a frame); at or past live it follows live again. */
  function moveTo(next: number) {
    if (!timeline) return;
    const clamped = clampViewEnd(timeline, next);
    frozenBySelection.current = false;
    if (clamped >= timeline.liveFrame) setFollow(true);
    else {
      setFollow(false);
      setViewEnd(clamped);
    }
    setKick((k) => k + 1);
  }
  function step(seconds: number) {
    if (!timeline) return;
    const next = jump(timeline, end, seconds);
    moveTo(next.live ? timeline.liveFrame : next.end);
  }
  function goLive() {
    frozenBySelection.current = false;
    setFollow(true);
    setKick((k) => k + 1);
  }

  /** A new selection (frames). Selecting while live holds the view still, so the selection stays where it was made. */
  function select(next: FrameRange | null) {
    setSelection(next);
    setName('');
    if (next && follow) {
      setFollow(false);
      setViewEnd(displayEnd);
      frozenBySelection.current = true;
    } else if (!next && frozenBySelection.current) {
      frozenBySelection.current = false;
      setFollow(true);
    }
    if (next && timeline && (next.startFrame < start || next.endFrame > displayEnd)) {
      // A word outside the waveform: bring it into view (a second of context after it).
      setFollow(false);
      setViewEnd(clampViewEnd(timeline, next.endFrame + rate));
      frozenBySelection.current = false;
    }
  }
  const onWaveSelect = (s: Selection | null) => select(s ? selectionFrames(s, start, rate) : null);

  const selectedMs = selection ? selectionMs(selection, start, rate) : null;
  const suggestion = wordsText(words, selection);
  const fields = () =>
    selection && sessionId
      ? { startFrame: selection.startFrame, endFrame: selection.endFrame, sourceId: sessionId }
      : null;

  // Host-only private preview: heard on this PC only, never through Soundpad or by guests.
  const { previewing, play: preview, stop: stopPreview } = usePreviewAudio();
  async function clip(play: boolean) {
    const chosen = fields();
    if (!chosen || busy) return;
    setBusy(true);
    try {
      const named = {
        ...chosen,
        ...((name.trim() || suggestion) && { name: name.trim() || suggestion }),
      };
      if (play) await playback.make('mimicClip', named, 'make:mimic');
      else await controller.run('mimicClip', named);
      select(null);
      setName('');
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  async function share() {
    const chosen = fields();
    if (!chosen) return;
    try {
      await controller.run('historyShare', {
        startFrame: chosen.startFrame,
        endFrame: chosen.endFrame,
      });
      controller.notify('Guests can now see that part of the history.', 'success');
      setKick((k) => k + 1);
    } catch {
      /* Controller shows the error. */
    }
  }
  async function unshare() {
    try {
      await controller.run('historyUnshare');
      setKick((k) => k + 1);
    } catch {
      /* Controller shows the error. */
    }
  }

  // Shortcuts, as in the 30 s view: 3/5/0 select the last seconds, Enter mimics (Shift plays), Escape clears.
  const nameField = useRef<HTMLInputElement>(null);
  const [focusName, setFocusName] = useState(false);
  useEffect(() => {
    if (focusName && selection) {
      nameField.current?.focus();
      setFocusName(false);
    }
  }, [focusName, selection]);
  const shortcut = useRef<(e: KeyboardEvent) => void>(() => {});
  useEffect(() => {
    shortcut.current = (e: KeyboardEvent) => {
      if (!timeline?.sessionId || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
      const typing =
        e.target instanceof HTMLElement &&
        e.target.matches(
          'select, textarea, input:not([type="checkbox"]):not([type="radio"]):not([type="button"])',
        );
      if (e.key === 'Escape' && selection) {
        e.preventDefault();
        e.stopPropagation();
        select(null);
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
        select(
          selectionFrames(
            lastSeconds(QUICK_KEYS[e.key], WINDOW_MS),
            windowStart(displayEnd, rate),
            rate,
          ),
        );
      }
    };
  });
  useEffect(() => {
    const listener = (e: KeyboardEvent) => shortcut.current(e);
    document.addEventListener('keydown', listener, true);
    return () => document.removeEventListener('keydown', listener, true);
  }, []);

  if (!timeline) return <p className="muted">Loading the recording history…</p>;
  if (!timeline.sessionId)
    return (
      <p className="muted">
        {host ? 'Choose a source and start listening.' : 'Waiting for the host to start Mimic.'}
      </p>
    );

  const behind = behindLiveMs(timeline, end);
  const atLive = follow || end >= timeline.liveFrame;
  // Held still only because something is selected (as the 30 s view did): it still reads as "the last N seconds".
  const moved = !atLive && !frozenBySelection.current;
  const viewClock = wallOf(timeline, end);
  const marks = gapMarks(timeline, start);
  const hidden = hiddenParts(timeline, start);
  const shown = sentences(words);
  const highlight = hover
    ? selectionMs({ startFrame: hover.start, endFrame: hover.end }, start, rate)
    : null;
  const waiting =
    timeline.transcribing && pending.some((p) => p.endFrame - p.startFrame > rate / 2);
  const selectionClock = selection ? wallOf(timeline, selection.startFrame) : null;
  return (
    <div className="mimic-history">
      {!host && (
        <p className="muted mimic-guest-note">
          You see what was recorded since you joined
          {timeline.shared.length ? ', and the parts the host shared with guests' : ''}.
        </p>
      )}
      <div className="mimic-nav" role="group" aria-label="Move through the history">
        <button
          type="button"
          onClick={() => step(-JUMP_SECONDS)}
          disabled={end <= oldestFrame(timeline) + rate}
        >
          <ChevronLeft size={15} /> Back {JUMP_SECONDS} s
        </button>
        <button type="button" onClick={() => step(JUMP_SECONDS)} disabled={atLive}>
          Forward {JUMP_SECONDS} s <ChevronRight size={15} />
        </button>
        <button
          type="button"
          className={atLive ? 'live' : 'primary'}
          aria-pressed={atLive}
          onClick={goLive}
        >
          <Radio size={15} /> {atLive ? 'Live' : 'Go live'}
        </button>
        <span className="mimic-when">
          {viewClock !== null && (
            <time dateTime={new Date(viewClock).toISOString()}>{formatClock(viewClock)}</time>
          )}
          {atLive
            ? timeline.recording
              ? ' · live'
              : ' · recording stopped'
            : ` · ${formatSpan(behind)} behind live`}
        </span>
      </div>
      {overview && (
        <MimicOverview
          range={overview.range}
          peaks={overview.peaks}
          view={{ startFrame: Math.max(overview.range.startFrame, start), endFrame: end }}
          rate={rate}
          gaps={timeline.gaps.map((g) => g.atFrame)}
          shared={host ? timeline.shared : []}
          behindMs={behind}
          onMove={moveTo}
        />
      )}
      {main && (
        <MimicWave
          peek={{ windowMs: WINDOW_MS, levels: main.peaks }}
          selection={selectedMs}
          onSelect={onWaveSelect}
          extras={{
            storageKey: VIEW_KEY,
            defaultView: DEFAULT_HISTORY_VIEW,
            where: moved ? `ending ${formatSpan(behind)} before live` : undefined,
            endLabel: moved ? `−${formatSpan(behind)}` : 'now',
            marks,
            hidden,
            highlight,
            onGrab: () => {
              // Hold the view still under the pointer; letting go without a selection follows live again.
              if (!follow) return;
              setFollow(false);
              setViewEnd(displayEnd);
              frozenBySelection.current = true;
            },
          }}
        />
      )}
      {marks.length > 0 && (
        <ul className="mimic-gaps" aria-label="Gaps in the recording">
          {marks.map((m) => (
            <li key={m.ms}>{m.label}</li>
          ))}
        </ul>
      )}
      {(mimic?.transcribing || words.length > 0) && (
        <ol className="mimic-transcript mimic-words" aria-label="Transcript">
          {shown.length ? (
            shown.map((sentence) => (
              <li key={sentence[0].id}>
                <time>
                  {ago(Math.max(0, framesToMs(sentence[0].start - start, rate)), WINDOW_MS)}
                </time>
                <span className="mimic-sentence">
                  {sentence.map((w) => (
                    <button
                      key={w.id}
                      type="button"
                      className={`mimic-word${w.status === 'uncertain' ? ' uncertain' : ''}${hover?.id === w.id ? ' hover' : ''}`}
                      aria-pressed={wordSelected(w, selection)}
                      title={
                        w.status === 'uncertain'
                          ? 'Uncertain: check this word and its edges before using it'
                          : undefined
                      }
                      onMouseEnter={() => setHover(w)}
                      onMouseLeave={() => setHover((h) => (h?.id === w.id ? null : h))}
                      onFocus={() => setHover(w)}
                      onBlur={() => setHover((h) => (h?.id === w.id ? null : h))}
                      onClick={(e) => {
                        select(wordSelection(words, anchor, w.id, e.shiftKey, rate));
                        if (!e.shiftKey || anchor === null) setAnchor(w.id);
                      }}
                    >
                      {w.text}
                    </button>
                  ))}
                </span>
              </li>
            ))
          ) : (
            <li className="muted">Words will appear here a few seconds after they are said.</li>
          )}
          {waiting && <li className="muted mimic-pending">Recognising more of this part…</li>}
        </ol>
      )}
      <div className="mimic-quick" role="group" aria-label="Quick select">
        {QUICK_SECONDS.map((s) => (
          <button
            key={s}
            onClick={() =>
              select(
                selectionFrames(lastSeconds(s, WINDOW_MS), windowStart(displayEnd, rate), rate),
              )
            }
          >
            Last {s} s
          </button>
        ))}
        {selection && (
          <button onClick={() => select(null)}>
            <X size={14} /> Clear
          </button>
        )}
      </div>
      {host && timeline.shared.length > 0 && (
        <p className="muted mimic-shared">
          {timeline.shared.length === 1
            ? 'One earlier part is'
            : `${timeline.shared.length} earlier parts are`}{' '}
          shared with guests.{' '}
          <button type="button" className="link" onClick={() => void unshare()}>
            Stop sharing
          </button>
        </p>
      )}
      {selection && selectedMs ? (
        <div className="mimic-actions">
          <span className="mimic-range">
            {ago(selectedMs.from, WINDOW_MS)} → {ago(selectedMs.to, WINDOW_MS)} ·{' '}
            {(framesToMs(selection.endFrame - selection.startFrame, rate) / 1000).toFixed(1)} s
            {selectionClock !== null && <small> at {formatClock(selectionClock)}</small>}
          </span>
          <input
            ref={nameField}
            aria-label="Clip name"
            placeholder={suggestion || 'Name (optional)'}
            maxLength={100}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          {host && supports(state.companionVersion, 'preview') && (
            <button
              title="Hear the selection on this PC only; nobody else hears it"
              onClick={() => {
                const chosen = fields();
                if (previewing) stopPreview();
                else if (chosen) void preview('mimicPreview', chosen);
              }}
            >
              <Headphones size={15} /> {previewing ? 'Stop preview' : 'Preview'}
            </button>
          )}
          {host && timeline.fromFrame === null && (
            <button
              title="Let guests see and clip this part, although it is from before they joined"
              onClick={() => void share()}
            >
              <Share2 size={15} /> Share with guests
            </button>
          )}
          <button aria-disabled={busy || undefined} onClick={() => !busy && void clip(false)}>
            <Scissors size={15} /> Mimic it
          </button>
          <button
            className="primary"
            disabled={!canPlay}
            aria-disabled={busy || held || undefined}
            title={held ? HELD_BY_HOST : undefined}
            onClick={() => !busy && !held && void clip(true)}
          >
            <Play size={15} /> Mimic &amp; play
          </button>
        </div>
      ) : (
        <>
          <p className="muted mimic-help-keys">
            Drag across the waveform, click a word (Shift-click another to select everything
            between), or press <kbd>3</kbd> <kbd>5</kbd> <kbd>0</kbd> for the last 3, 5 or 10
            seconds. Then <kbd>Enter</kbd> mimics, <kbd>Shift</kbd>+<kbd>Enter</kbd> mimics and
            plays, <kbd>Esc</kbd> clears. Back, Forward and the overview move through the last{' '}
            {formatSpan((history?.seconds ?? timeline.windowSeconds) * 1000)}; recording carries on.
          </p>
          <p className="muted mimic-help-touch">
            Drag across the waveform, tap a transcript line's word, or tap Last 3 s, 5 s or 10 s.
            Then Mimic it. Drag the overview to go back in time.
          </p>
        </>
      )}
    </div>
  );
}
