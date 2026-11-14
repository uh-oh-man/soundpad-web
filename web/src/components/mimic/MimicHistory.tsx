import { useEffect, useRef, useState } from 'react';
import {
  BookmarkPlus,
  ChevronLeft,
  ChevronRight,
  Copy,
  Crosshair,
  Headphones,
  MessageSquareQuote,
  Play,
  Radio,
  Scissors,
  Share2,
  Trash2,
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
import {
  bandColor,
  bandLabel,
  bandSelection,
  draftClipSchema,
  draftLabel,
  draftSchema,
  draftState,
  sameRange,
  type Draft,
} from '../../mimic/drafts';
import { refreshDrafts, useDraftStore } from '../../mimic/draft-store';
import { recall, remember } from '../../sections';
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
/** A draft's edges are saved this long after the last change (keyboard nudges come in bursts). */
const SAVE_EDGES_MS = 250;

/**
 * The Mimic window on a 2.39 companion: the rolling recording history. The main waveform shows the newest 10 s
 * (zoom 5/10/30 s), the overview the whole history; Back/Forward, dragging the overview and Go live move the view
 * while recording carries on. The transcript shows sentences of clickable words (click selects a word, Shift-click
 * a range to it). Guests see the history from when they joined, plus what the host shared.
 *
 * With a 2.40 companion, Capture opens a draft for the person who captured it instead of making a clip at once:
 * one draft is shown at a time (the others are in the draft history), its edges can be dragged into the
 * surrounding history, and Keep in Temp makes the clip. Other people's drafts show as labelled bands that can be
 * copied into one's own. What was open and where the view was are remembered when the window closes.
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
  // Drafts need the 2.40 companion, and for a guest a host page that relays their commands.
  const draftsOn =
    supports(state.companionVersion, 'drafts') && (host || controller.relaysDrafts());
  const remembered = useRef(recall('mimic'));
  const sameSession = remembered.current.sessionId === (history?.sessionId ?? null);
  const [timeline, setTimeline] = useState<Timeline | null>(null);
  const [follow, setFollow] = useState(sameSession ? remembered.current.follow !== false : true);
  const [viewEnd, setViewEnd] = useState(sameSession ? (remembered.current.viewEnd ?? 0) : 0);
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
  const [openId, setOpenId] = useState<string | null>(
    draftsOn && sameSession ? (remembered.current.draftId ?? null) : null,
  );
  const held = guestHeld(state, host);
  const live = timeline?.liveFrame ?? 0;
  const rate = timeline?.rate ?? 48000;
  const end = follow ? live : viewEnd;
  // The window the waveform on screen was fetched for: selections map to frames through it, so they match what was seen.
  const displayEnd = main?.end ?? end;
  const start = windowStart(displayEnd, rate);
  const sessionId = timeline?.sessionId ?? null;

  // Drafts: this person's (most recent first) and the other people's selections on this history.
  const { drafts: list, budget } = useDraftStore();
  const { mine, others, open, gone } = draftState(
    list,
    sessionId ?? history?.sessionId ?? null,
    openId,
    draftsOn,
  );
  const waiting = !host && budget?.guest?.own.paused ? budget.guest.own.waiting : null;

  // Remember what this section shows, so closing the window (or a reload) comes back to it.
  useEffect(() => {
    if (!sessionId) return;
    remember('mimic', { sessionId, draftId: openId, follow, viewEnd });
  }, [sessionId, openId, follow, viewEnd]);
  // A draft that went (kept and closed, discarded, from another tab of the same person) closes here too.
  useEffect(() => {
    if (gone) setOpenId(null);
  }, [gone]);
  // The open draft's range is what is selected; it follows the companion when it changes there.
  useEffect(() => {
    if (!open) return;
    setSelection({ startFrame: open.startFrame, endFrame: open.endFrame });
    // Only when another draft is opened or its range changed elsewhere, never while typing its name.
  }, [open?.id, open?.startFrame, open?.endFrame]);
  useEffect(() => {
    setName(open?.name ?? '');
  }, [open?.id]);

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
  // The list is fetched by the page when drafts change; opening the window asks once more.
  useEffect(() => {
    if (draftsOn && connected && history?.sessionId) void refreshDrafts();
  }, [draftsOn, connected, history?.sessionId]);

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
  /** Brings a range into view (a second of context after it) unless it is already there. */
  function reveal(range: FrameRange) {
    if (!timeline) return;
    if (range.startFrame >= start && range.endFrame <= displayEnd) {
      if (follow) {
        setFollow(false);
        setViewEnd(displayEnd);
        frozenBySelection.current = true;
      }
      return;
    }
    setFollow(false);
    setViewEnd(clampViewEnd(timeline, range.endFrame + rate));
    frozenBySelection.current = false;
    setKick((k) => k + 1);
  }

  // A draft's new edges go to the companion shortly after the last change; a refusal puts the old ones back.
  const saveTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(saveTimer.current), []);
  function saveEdges(draft: Draft, next: FrameRange) {
    clearTimeout(saveTimer.current);
    saveTimer.current = setTimeout(() => {
      void controller
        .run('draftUpdate', {
          draftId: draft.id,
          startFrame: Math.round(next.startFrame),
          endFrame: Math.round(next.endFrame),
          ...(sessionId && { sourceId: sessionId }),
        })
        .then(() => refreshDrafts())
        .catch(() => setSelection({ startFrame: draft.startFrame, endFrame: draft.endFrame }));
    }, SAVE_EDGES_MS);
  }

  /** A new selection (frames). Selecting while live holds the view still, so the selection stays where it was made. */
  function select(next: FrameRange | null) {
    if (open) {
      // Editing the open draft: dragging an edge or picking words moves its edges. Clearing closes it (it stays in the history).
      if (!next) {
        closeDraft();
        return;
      }
      setSelection(next);
      saveEdges(open, next);
      return;
    }
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

  function openDraft(draft: Draft | null) {
    clearTimeout(saveTimer.current);
    if (!draft) {
      closeDraft();
      return;
    }
    setOpenId(draft.id);
    setSelection({ startFrame: draft.startFrame, endFrame: draft.endFrame });
    setName(draft.name ?? '');
    reveal({ startFrame: draft.startFrame, endFrame: draft.endFrame });
  }
  function closeDraft() {
    clearTimeout(saveTimer.current);
    setOpenId(null);
    setSelection(null);
    setName('');
    if (frozenBySelection.current) {
      frozenBySelection.current = false;
      setFollow(true);
    }
  }

  const selectedMs = selection ? selectionMs(selection, start, rate) : null;
  const suggestion = wordsText(words, selection);
  const fields = () =>
    selection && sessionId
      ? { startFrame: selection.startFrame, endFrame: selection.endFrame, sourceId: sessionId }
      : null;

  // Private preview: heard in this browser only, never through Soundpad. Guests have it with a 2.40 companion.
  const { previewing, play: preview, stop: stopPreview } = usePreviewAudio();
  const canPreview = (host && supports(state.companionVersion, 'preview')) || draftsOn;
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
  /** Capture (2.40): the selection becomes a draft for this person, shown at once. Nothing is imported or played. */
  async function capture(quick?: 'phrase' | 'recent') {
    if (busy) return;
    const chosen = quick ? null : fields();
    if (!quick && !chosen) return;
    setBusy(true);
    try {
      // Only a typed name: otherwise the companion names the draft from the words this person may see, and
      // shows others a name only from words they may see (2.40).
      const named = name.trim();
      const draft = draftSchema.parse(
        await controller.run('draftCreate', {
          ...(quick ? { action: quick } : chosen),
          ...(!quick && named && { name: named }),
          ...(sessionId && { sourceId: sessionId }),
        }),
      );
      await refreshDrafts();
      openDraft(draft);
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  /** The open draft's name, saved before it is kept or played (and when the field loses focus). */
  async function saveName(draft: Draft) {
    const next = name.trim();
    if (next === (draft.name ?? '').trim()) return;
    try {
      await controller.run('draftUpdate', { draftId: draft.id, name: next });
    } catch {
      /* Controller shows the error. */
    }
  }
  /** Keep in Temp: the draft becomes a Temp clip (the 2.39 import); with `close` the draft goes too. */
  async function keepDraft(draft: Draft, close: boolean) {
    if (busy || waiting) return;
    setBusy(true);
    try {
      await saveName(draft);
      const result = draftClipSchema.parse(
        await controller.run('draftKeep', { draftId: draft.id, ...(close && { action: 'close' }) }),
      );
      controller.notify(
        `“${name.trim() || draft.name || 'Draft'}” is in Temp${result.closed ? '' : '; the draft stays open'}.`,
        'success',
      );
      if (result.closed) closeDraft();
      await refreshDrafts();
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  async function playDraft(draft: Draft) {
    if (busy || held || waiting) return;
    setBusy(true);
    try {
      await saveName(draft);
      await playback.make('draftPlay', { draftId: draft.id }, 'make:mimic');
      await refreshDrafts();
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  async function discardDraft(draft: Draft) {
    if (busy) return;
    setBusy(true);
    try {
      await controller.run('draftDiscard', { draftId: draft.id });
      closeDraft();
      await refreshDrafts();
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  /** Host (2.40): lets go of someone else's draft, such as one a guest left behind holding recording history. */
  async function discardBand(id: string) {
    if (busy) return;
    setBusy(true);
    try {
      await controller.run('draftDiscard', { draftId: id });
      await refreshDrafts();
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  async function copyBand(id: string) {
    if (busy) return;
    setBusy(true);
    try {
      const draft = draftSchema.parse(await controller.run('draftCopy', { draftId: id }));
      await refreshDrafts();
      openDraft(draft);
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
  function previewNow() {
    if (previewing) stopPreview();
    else if (open && !sameRange(open, selection)) {
      // Edges still being saved: hear what is selected now.
      const chosen = fields();
      if (chosen) void preview('mimicPreview', chosen);
    } else if (open) void preview('mimicPreview', { draftId: open.id });
    else {
      const chosen = fields();
      if (chosen) void preview('mimicPreview', chosen);
    }
  }

  // Shortcuts, as in the 30 s view: 3/5/0 select the last seconds, Enter captures (or with an old companion
  // mimics; with a draft open, keeps it in Temp), Shift+Enter plays, Escape clears.
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
        if (open) {
          if (!e.shiftKey) void keepDraft(open, false);
          else if (canPlay) void playDraft(open);
        } else if (!e.shiftKey) void (draftsOn ? capture() : clip(false));
        else if (canPlay) void clip(true);
      } else if (!typing && QUICK_KEYS[e.key]) {
        e.preventDefault();
        // A number key starts a new selection: an open draft is put away (it stays in the draft history).
        if (open) closeDraft();
        setFocusName(true);
        const next = selectionFrames(
          lastSeconds(QUICK_KEYS[e.key], WINDOW_MS),
          windowStart(displayEnd, rate),
          rate,
        );
        setSelection(next);
        setName('');
        if (follow) {
          setFollow(false);
          setViewEnd(displayEnd);
          frozenBySelection.current = true;
        }
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
  const bands = others.map((band) => ({
    ...bandSelection(band, start, rate),
    color: bandColor(band.tag),
    label: bandLabel(band),
  }));
  const recognising =
    timeline.transcribing && pending.some((p) => p.endFrame - p.startFrame > rate / 2);
  const selectionClock = selection ? wallOf(timeline, selection.startFrame) : null;
  const playTitle = held
    ? HELD_BY_HOST
    : waiting
      ? 'Waiting for the host to allow more'
      : undefined;
  const range =
    selection && selectedMs ? (
      <span className="mimic-range">
        {ago(selectedMs.from, WINDOW_MS)} → {ago(selectedMs.to, WINDOW_MS)} ·{' '}
        {(framesToMs(selection.endFrame - selection.startFrame, rate) / 1000).toFixed(1)} s
        {selectionClock !== null && <small> at {formatClock(selectionClock)}</small>}
      </span>
    ) : null;
  const previewButton = canPreview && (
    <button
      type="button"
      title="Hear it in this browser only; nobody else hears it and nothing plays in the game"
      onClick={previewNow}
    >
      <Headphones size={15} /> {previewing ? 'Stop preview' : 'Preview'}
    </button>
  );
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
            bands,
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
      {others.length > 0 && (
        <ul className="mimic-bands" aria-label="Other people's selections">
          {others.map((band) => (
            <li key={band.id} style={{ borderColor: bandColor(band.tag) }}>
              <span>
                <i style={{ background: bandColor(band.tag) }} aria-hidden="true" />
                {bandLabel(band)}
                {band.name ? `: “${band.name}”` : ''}{' '}
                <small>
                  {(framesToMs(band.endFrame - band.startFrame, rate) / 1000).toFixed(1)} s
                </small>
              </span>
              <button
                type="button"
                onClick={() => reveal({ startFrame: band.startFrame, endFrame: band.endFrame })}
              >
                <Crosshair size={14} /> Show
              </button>
              <button
                type="button"
                aria-disabled={busy || undefined}
                aria-label={`Copy ${bandLabel(band)} to my drafts`}
                onClick={() => void copyBand(band.id)}
              >
                <Copy size={14} /> Copy to my drafts
              </button>
              {host && (
                <button
                  type="button"
                  aria-disabled={busy || undefined}
                  aria-label={`Discard ${bandLabel(band)}`}
                  onClick={() => void discardBand(band.id)}
                >
                  <Trash2 size={14} /> Discard
                </button>
              )}
            </li>
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
          {recognising && <li className="muted mimic-pending">Recognising more of this part…</li>}
        </ol>
      )}
      <div className="mimic-quick" role="group" aria-label="Quick select">
        {QUICK_SECONDS.map((s) => (
          <button
            key={s}
            onClick={() => {
              if (open) closeDraft();
              select(
                selectionFrames(lastSeconds(s, WINDOW_MS), windowStart(displayEnd, rate), rate),
              );
            }}
          >
            Last {s} s
          </button>
        ))}
        {selection && !open && (
          <button onClick={() => select(null)}>
            <X size={14} /> Clear
          </button>
        )}
      </div>
      {draftsOn && (
        <div className="mimic-quick mimic-quick-drafts" role="group" aria-label="Quick Mimic">
          <button
            type="button"
            aria-disabled={busy || undefined}
            title="A draft of the most recent finished phrase (skips what the companion itself played)"
            onClick={() => void capture('phrase')}
          >
            <MessageSquareQuote size={15} /> Latest phrase
          </button>
          <button
            type="button"
            aria-disabled={busy || undefined}
            title="A draft of the last ten seconds"
            onClick={() => void capture('recent')}
          >
            <Scissors size={15} /> Last 10 seconds
          </button>
          {mine.length > 0 && (
            <label className="mimic-draft-history">
              <span>My drafts</span>
              <select
                aria-label="Draft history"
                value={open?.id ?? ''}
                onChange={(e) => openDraft(mine.find((d) => d.id === e.target.value) ?? null)}
              >
                <option value="">{open ? 'Close the draft' : `${mine.length} saved`}</option>
                {mine.map((d) => (
                  <option key={d.id} value={d.id}>
                    {draftLabel(d, rate)}
                    {d.inTemp ? ' (in Temp)' : ''}
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
      )}
      {waiting && (
        <p className="warning mimic-waiting" role="status">
          Waiting for the host to allow more {waiting === 'kept' ? 'kept audio' : 'Temp space'}.
          Drafts, preview and playing go on as usual.
        </p>
      )}
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
      {open ? (
        // The same element as the capture row below, so the name field keeps focus when Capture opens the draft.
        <div className="mimic-actions mimic-draft" role="region" aria-label="Draft">
          {range}
          <input
            ref={nameField}
            aria-label="Clip name"
            placeholder={suggestion || 'Name (optional)'}
            maxLength={100}
            value={name}
            onChange={(e) => setName(e.target.value)}
            onBlur={() => void saveName(open)}
          />
          {previewButton}
          <button
            type="button"
            className="primary"
            disabled={!canPlay}
            aria-disabled={busy || held || !!waiting || undefined}
            title={playTitle}
            onClick={() => void playDraft(open)}
          >
            <Play size={15} /> Play into game
          </button>
          <button
            type="button"
            aria-disabled={busy || !!waiting || undefined}
            title={waiting ? 'Waiting for the host to allow more' : undefined}
            onClick={() => void keepDraft(open, false)}
          >
            <BookmarkPlus size={15} /> Keep in Temp
          </button>
          <button
            type="button"
            aria-disabled={busy || !!waiting || undefined}
            title={waiting ? 'Waiting for the host to allow more' : undefined}
            onClick={() => void keepDraft(open, true)}
          >
            Keep in Temp and close draft
          </button>
          <button
            type="button"
            aria-disabled={busy || undefined}
            onClick={() => void discardDraft(open)}
          >
            <Trash2 size={15} /> Discard
          </button>
          <p className="muted mimic-draft-help">
            Drag an edge on the waveform, or click words, to change it; it can reach into the audio
            around it. Nobody else hears the preview.
          </p>
        </div>
      ) : selection && selectedMs ? (
        <div className="mimic-actions">
          {range}
          <input
            ref={nameField}
            aria-label="Clip name"
            placeholder={suggestion || 'Name (optional)'}
            maxLength={100}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          {previewButton}
          {host && timeline.fromFrame === null && (
            <button
              title="Let guests see and clip this part, although it is from before they joined"
              onClick={() => void share()}
            >
              <Share2 size={15} /> Share with guests
            </button>
          )}
          {draftsOn ? (
            <button
              title="Open it as a draft you can adjust before keeping or playing it"
              aria-disabled={busy || undefined}
              onClick={() => !busy && void capture()}
            >
              <Scissors size={15} /> Capture
            </button>
          ) : (
            <button aria-disabled={busy || undefined} onClick={() => !busy && void clip(false)}>
              <Scissors size={15} /> Mimic it
            </button>
          )}
          <button
            className="primary"
            disabled={!canPlay}
            aria-disabled={busy || held || !!waiting || undefined}
            title={playTitle}
            onClick={() => !busy && !held && !waiting && void clip(true)}
          >
            <Play size={15} /> Mimic &amp; play
          </button>
        </div>
      ) : (
        <>
          <p className="muted mimic-help-keys">
            Drag across the waveform, click a word (Shift-click another to select everything
            between), or press <kbd>3</kbd> <kbd>5</kbd> <kbd>0</kbd> for the last 3, 5 or 10
            seconds. Then <kbd>Enter</kbd> {draftsOn ? 'captures a draft' : 'mimics'},{' '}
            <kbd>Shift</kbd>+<kbd>Enter</kbd> mimics and plays, <kbd>Esc</kbd> clears. Back, Forward
            and the overview move through the last{' '}
            {formatSpan((history?.seconds ?? timeline.windowSeconds) * 1000)}; recording carries on.
          </p>
          <p className="muted mimic-help-touch">
            Drag across the waveform, tap a transcript line's word, or tap Last 3 s, 5 s or 10 s.
            Then {draftsOn ? 'Capture' : 'Mimic it'}. Drag the overview to go back in time.
          </p>
        </>
      )}
    </div>
  );
}
