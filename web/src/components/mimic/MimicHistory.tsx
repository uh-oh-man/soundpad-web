import {
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type PointerEvent,
  type ReactNode,
} from 'react';
import {
  ArrowDownToLine,
  BookAudio,
  BookmarkPlus,
  ChevronLeft,
  ChevronRight,
  Copy,
  Crosshair,
  Headphones,
  MessageSquareQuote,
  Pause,
  Play,
  Radio,
  Scissors,
  Search,
  Share2,
  Square,
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
  bind,
  clampViewEnd,
  DEFAULT_HISTORY_VIEW,
  drawnEnd,
  formatClock,
  formatSpan,
  framesToMs,
  gapMarks,
  hiddenParts,
  jump,
  levelsSchema,
  msToFrames,
  ofSession,
  oldestFrame,
  selectionFields,
  selectionFrames,
  selectionMs,
  selectionOn,
  sentences,
  timelineSchema,
  toPeaks,
  transcriptSchema,
  wallOf,
  WINDOW_MS,
  windowStart,
  wordSelected,
  wordCut,
  wordsText,
  type BoundSelection,
  type FrameRange,
  type Timeline,
  type Word,
} from '../../mimic/history';
import {
  bandColor,
  bandLabel,
  bandSelection,
  draftClipSchema,
  draftSchema,
  draftState,
  sameRange,
  type Draft,
} from '../../mimic/drafts';
import { refreshDrafts, useDraftStore } from '../../mimic/draft-store';
import { shareActiveDraft, useSharedDraft } from '../../ui-sync';
import { recall, remember } from '../../sections';
import { z } from 'zod';
import { takeOpen, useOpenRequests, useVoiceStore } from '../../library/voice-store';
import {
  canEditLibrary,
  historySearchSchema,
  speakerGroupsSchema,
  type SpeakerGroups,
} from '../../library/voices';
import { MimicWave } from './MimicWave';
import { MimicOverview } from './MimicOverview';
import { historyPoller, levelsToDraw, viewStillWanted } from '../../mimic/history-poll';
import { mimicKeysApply } from './keys';
import { useFollowNewest } from '../../mimic/follow';
import { followFocus } from '../../focus-follow';
import { deletedNow, deleteWithUndo, useDeleted } from '../../mimic/pending-delete';
import { useReducedMotion } from '../../motion';
import { tempAssetsSnapshot } from '../../temp-assets';
import {
  clipKey,
  effectiveMode,
  gameBlocked,
  gamePlayEnded,
  playFrom,
  playheadFrame,
  playingWordIds,
  readPlayMode,
  reusableClip,
  writePlayMode,
  type PlayMode,
} from './playMode';
import { announceSaved, canQuickSave, savedSchema } from '../../library/quick-save';

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
const NO_WORDS: Word[] = [];
const NO_RANGES: FrameRange[] = [];
/** How often the playing word is worked out while something plays (less often with reduced motion). */
const PLAYING_TICK_MS = 50,
  PLAYING_TICK_REDUCED_MS = 250;

/**
 * Something this window is playing (2.45): a word, a stretch from a waveform click, the selection's preview or a
 * draft played into the game. `startedAt` and `epoch` are set once a play into the game has started.
 */
type Playing = {
  token: number;
  sessionId: string;
  range: FrameRange;
  mode: PlayMode;
  lengthMs: number;
  startedAt: number | null;
  epoch: string | null;
};
const field = (data: unknown, key: string) => {
  const value =
    data && typeof data === 'object' ? (data as Record<string, unknown>)[key] : undefined;
  return typeof value === 'string' ? value : null;
};

/**
 * The Mimic window on a 2.39 companion: the rolling recording history. The main waveform shows the newest 10 s
 * (zoom 5/10/30 s), the overview the whole history; Back/Forward, dragging the overview and Go live move the view
 * while recording carries on. The transcript shows sentences of clickable words (click selects a word, Shift-click
 * a range to it). Guests see the history from when they joined, plus what the host shared.
 *
 * A click on the waveform plays from that point in the person's chosen mode. Word clicks and drags
 * select audio. The words being heard carry the `playing` class.
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
  recentClips,
}: {
  state: Snapshot;
  host: boolean;
  connected: boolean;
  canPlay: boolean;
  recentClips: ReactNode;
}) {
  const mimic = state.mimic;
  const demo = controller.getSnapshot().demo;
  const history = mimic?.history;
  // Drafts need the 2.40 companion, and for a guest a host page that relays their commands.
  const draftsOn =
    supports(state.companionVersion, 'drafts') && (host || controller.relaysDrafts());
  const remembered = useRef(recall('mimic'));
  const sameSession = remembered.current.sessionId === (history?.sessionId ?? null);
  const [timeline, setTimeline] = useState<Timeline | null>(null);
  const [follow, setFollow] = useState(sameSession ? remembered.current.follow !== false : true);
  const [viewEnd, setViewEnd] = useState(sameSession ? (remembered.current.viewEnd ?? 0) : 0);
  // Everything fetched or selected is tagged with the recording (history session) it came from (2.41), and only
  // shown or used while that recording is the current one; see `ofSession` and `selectionOn`.
  const [mainData, setMain] = useState<{ end: number; peaks: number[]; sessionId: string } | null>(
    null,
  );
  const [overviewData, setOverview] = useState<{
    range: FrameRange;
    peaks: number[];
    sessionId: string;
  } | null>(null);
  const [transcript, setTranscript] = useState<{
    sessionId: string;
    words: Word[];
    pending: FrameRange[];
    before: number | null;
  } | null>(null);
  const olderPage = useRef<{ session: string; before: number } | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const prependPosition = useRef<{ top: number; height: number } | null>(null);
  const transcriptList = useRef<HTMLOListElement | null>(null);
  const [picked, setPicked] = useState<BoundSelection | null>(null);
  const frozenBySelection = useRef(false);
  const [anchor, setAnchor] = useState<number | null>(null);
  const wordDrag = useRef<{
    pointer: number;
    first: number;
    x: number;
    y: number;
    sessionId: string;
    moved: boolean;
  } | null>(null);
  const draggedClick = useRef<number | null>(null);
  const [wordDragRange, setWordDragRange] = useState<BoundSelection | null>(null);
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
  const sessionId = timeline?.sessionId ?? null;
  /** The current recording, also for replies that arrive after this render. */
  const currentSession = useRef(sessionId);
  currentSession.current = sessionId;
  /** The waveform levels on screen (set once drawn), so a view held still keeps the window it was held on. */
  const drawnMain = useRef<typeof mainData>(null);
  const main = levelsToDraw(
    ofSession(mainData, sessionId),
    ofSession(drawnMain.current, sessionId),
    {
      follow,
      viewEnd: timeline ? clampViewEnd(timeline, viewEnd) : viewEnd,
    },
  );
  useLayoutEffect(() => {
    drawnMain.current = main;
  });
  const overview = ofSession(overviewData, sessionId);
  const shownTranscript = ofSession(transcript, sessionId);
  const words = useMemo(() => {
    const items = shownTranscript?.words ?? NO_WORDS;
    if (!timeline) return items;
    return items.filter((word) =>
      timeline.available.some(
        (range) => word.start >= range.startFrame && word.end <= range.endFrame,
      ),
    );
  }, [shownTranscript?.words, timeline?.available]);
  const selection = selectionOn(picked, sessionId);
  /** A selection on the recording shown in this render. */
  const setSelection = (next: FrameRange | null) => setPicked(bind(next, sessionId));
  // The window the waveform on screen was fetched for: selections map to frames through it, so they match what was seen.
  const displayEnd = main?.end ?? end;
  const start = windowStart(displayEnd, rate);
  /** How far the waveform drawn now ends before `displayEnd` (ms): the live glide's lag (2.47). */
  const glideShift = useRef(0);
  /**
   * Where the view on screen ends (a frame). Holding the view still keeps the levels' own window (so a drag
   * keeps its frame origin when they are fetched again) and the waveform keeps the glide's lag, so what was
   * seen stays where it was; "the last N seconds" are those up to this edge.
   */
  const visibleEnd = () => drawnEnd(displayEnd, glideShift.current, rate);

  // Drafts: this person's (most recent first) and the other people's selections on this history.
  const { drafts: listed, budget } = useDraftStore();
  // Drafts deleted here (2.46) are gone at once; the companion lets go of them when Undo has gone.
  const deleted = useDeleted();
  const list = useMemo(
    () =>
      listed && listed.drafts.some((d) => deleted.has(d.id))
        ? { ...listed, drafts: listed.drafts.filter((d) => !deleted.has(d.id)) }
        : listed,
    [listed, deleted],
  );
  const { mine, others, open, gone } = draftState(
    list,
    sessionId ?? history?.sessionId ?? null,
    openId,
    draftsOn,
  );
  const waiting = !host && budget?.guest?.own.paused ? budget.guest.own.waiting : null;
  // The voice library (2.41): Keep in voice library, word search in this recording, speaker groups.
  const libraryOn =
    supports(state.companionVersion, 'voiceLibrary') && (host || controller.relaysLibrary());
  const canLibraryKeep = libraryOn && (host || state.permissions.guestKeep === true);
  const { status: libraryStatus } = useVoiceStore();
  // One-press Save of a selection (2.46): straight into the voice library, named from its words.
  const canSave = libraryOn && draftsOn && canQuickSave(state, host, controller.hostSite());
  const canEditVoices = canEditLibrary(libraryStatus, state.permissions, host);
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<z.infer<typeof historySearchSchema> | null>(null);
  const [speakers, setSpeakers] = useState<SpeakerGroups | null>(null);

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
    setPicked(bind(open, open.sessionId));
    // Only when another draft is opened or its range changed elsewhere, never while typing its name.
  }, [open?.id, open?.startFrame, open?.endFrame]);
  useEffect(() => {
    setName(open?.name ?? '');
  }, [open?.id]);

  // Poll: the timeline every half second; the main levels with it (when live or the view moved), the transcript
  // every second and the overview every two. Recording never waits for any of it. One loop does it all (2.41,
  // history-poll.ts): moving the view nudges it instead of restarting it, so dragging the overview never has
  // more than one request of a kind on its way, and each pass fetches the newest wanted view.
  const viewRef = useRef({ follow, viewEnd });
  viewRef.current = { follow, viewEnd };
  const poller = useRef<ReturnType<typeof historyPoller> | null>(null);
  useEffect(() => {
    if (!connected) {
      olderPage.current = null;
      setLoadingOlder(false);
      return;
    }
    let shownEnd = -1;
    let wordsEnd = -1;
    let shownSession: string | null = null;
    let known: Timeline | null = null;
    let knownAt = -Infinity;
    const pendingPages = new Set<number>();
    const interval = host ? POLL_MS : GUEST_POLL_MS;
    const loop = historyPoller(
      async ({ tick, nudged, alive }) => {
        // A moved view uses the timeline it has, unless that is older than one ordinary pass.
        let t = known;
        if (!nudged || !t || Date.now() - knownAt > interval) {
          t = timelineSchema.parse(await controller.run('historyTimeline', {}, { quiet: true }));
          if (!alive()) return;
          known = t;
          knownAt = Date.now();
          setTimeline(t);
        }
        if (!t.sessionId) return;
        const session = t.sessionId;
        // Another recording (2.41): its waveform is fetched at once, whatever the old one showed.
        if (session !== shownSession) {
          shownSession = session;
          shownEnd = -1;
          wordsEnd = -1;
          pendingPages.clear();
        }
        // The newest wanted view, read now (not when the pass was asked for).
        const v = viewRef.current;
        const e = v.follow ? t.liveFrame : clampViewEnd(t, v.viewEnd);
        const s = windowStart(e, t.rate);
        const from = Math.max(0, s);
        if (e > from && (e !== shownEnd || (!nudged && tick % 4 === 0))) {
          const windowFrames = e - s;
          const lead = Math.round((MAIN_BINS * (from - s)) / windowFrames);
          const reply = levelsSchema.parse(
            await controller.run(
              'historyLevels',
              {
                startFrame: from,
                endFrame: e,
                bins: Math.max(1, MAIN_BINS - lead),
                sourceId: session,
              },
              { quiet: true },
            ),
          );
          if (!alive() || currentSession.current !== session) return;
          // Held still or moved meanwhile (2.45): this window is not the one wanted now, so it would shift the view.
          if (!viewStillWanted(v, viewRef.current)) {
            poller.current?.nudge();
            return;
          }
          setMain({
            end: e,
            peaks: [...new Array(lead).fill(0), ...toPeaks(reply.levels)],
            sessionId: session,
          });
          shownEnd = e;
        }
        const oldestAvailable = oldestFrame(t);
        for (const before of pendingPages.keys()) {
          if (before <= oldestAvailable) pendingPages.delete(before);
        }
        const page = olderPage.current?.session === session ? olderPage.current : null;
        const retry =
          !page && t.transcribing && !nudged && tick % 4 === 1
            ? pendingPages.keys().next().value
            : undefined;
        const olderEnd = page?.before ?? retry;
        const liveTranscript = wordsEnd < 0 || (!nudged && (v.follow || tick % 4 === 0));
        const transcriptEnd = olderEnd ?? (liveTranscript ? t.liveFrame : e);
        const transcriptStart = Math.max(
          oldestAvailable,
          olderEnd !== undefined ? transcriptEnd - t.rate * 30 : windowStart(transcriptEnd, t.rate),
        );
        if (page && transcriptEnd <= transcriptStart) {
          olderPage.current = null;
          setLoadingOlder(false);
        }
        if (
          transcriptEnd > transcriptStart &&
          (olderEnd !== undefined || (nudged ? e !== wordsEnd : tick % 2 === 0))
        ) {
          try {
            const reply = transcriptSchema.parse(
              await controller.run(
                'historyTranscript',
                { startFrame: transcriptStart, endFrame: transcriptEnd, sourceId: session },
                { quiet: true },
              ),
            );
            if (!alive() || currentSession.current !== session) return;
            const list = transcriptList.current;
            if (olderEnd !== undefined) {
              pendingPages.delete(olderEnd);
              if (reply.pending.length > 0) pendingPages.add(olderEnd);
              if (list && (page || !list.matches(':focus-within')))
                prependPosition.current = { top: list.scrollTop, height: list.scrollHeight };
            }
            const available = t.available;
            setTranscript((previous) => {
              const current = ofSession(previous, session);
              const kept = (current?.words ?? NO_WORDS).filter(
                (word) =>
                  (word.end <= transcriptStart || word.start >= transcriptEnd) &&
                  available.some(
                    (range) => word.start >= range.startFrame && word.end <= range.endFrame,
                  ),
              );
              const unique = new Map(kept.map((word) => [word.id, word]));
              for (const word of reply.words) unique.set(word.id, word);
              return {
                sessionId: session,
                words: [...unique.values()].sort((a, b) => a.start - b.start || a.id - b.id),
                pending: olderEnd !== undefined ? (current?.pending ?? NO_RANGES) : reply.pending,
                before:
                  page || (olderEnd === undefined && liveTranscript)
                    ? Math.min(current?.before ?? transcriptStart, transcriptStart)
                    : (current?.before ?? null),
              };
            });
            if (olderEnd === undefined) wordsEnd = e;
          } catch (error) {
            if (!page) throw error;
            if (alive() && currentSession.current === session)
              controller.notify('Earlier transcript could not be loaded. Try again.', 'error');
          } finally {
            if (page && olderPage.current === page && alive()) {
              olderPage.current = null;
              setLoadingOlder(false);
            }
          }
        }
        const oldest = oldestFrame(t);
        if (!nudged && tick % 4 === 0 && t.liveFrame > oldest) {
          const reply = levelsSchema.parse(
            await controller.run(
              'historyLevels',
              { startFrame: oldest, endFrame: t.liveFrame, bins: OVERVIEW_BINS, sourceId: session },
              { quiet: true },
            ),
          );
          if (!alive() || currentSession.current !== session) return;
          setOverview({
            range: { startFrame: oldest, endFrame: t.liveFrame },
            peaks: toPeaks(reply.levels),
            sessionId: session,
          });
        }
      },
      { intervalMs: interval },
    );
    poller.current = loop;
    loop.start();
    return () => {
      loop.stop();
      if (poller.current === loop) poller.current = null;
    };
  }, [connected, host]);
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
    // The next pass reads the view from here, even before React renders it.
    viewRef.current =
      clamped >= timeline.liveFrame
        ? { follow: true, viewEnd }
        : { follow: false, viewEnd: clamped };
    poller.current?.nudge();
  }
  function step(seconds: number) {
    if (!timeline) return;
    const next = jump(timeline, end, seconds);
    moveTo(next.live ? timeline.liveFrame : next.end);
  }
  function goLive() {
    frozenBySelection.current = false;
    viewRef.current = { follow: true, viewEnd };
    setFollow(true);
    poller.current?.nudge();
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
    poller.current?.nudge();
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
          // The draft's own recording (2.41): the companion refuses it on any other.
          sourceId: draft.sessionId,
        })
        .then(() => refreshDrafts())
        .catch(() => setSelection({ startFrame: draft.startFrame, endFrame: draft.endFrame }));
    }, SAVE_EDGES_MS);
  }

  /**
   * A new selection (frames). Selecting while live holds the view still, so the selection stays where it was made.
   * `fresh` (2.41 review): a new selection right after the open draft was put away (Jump, a search result, Last N s).
   * `open` still holds that draft in this render, and its edges must not move to the new selection.
   */
  function select(next: FrameRange | null, fresh = false) {
    if (!next || fresh) setAnchor(null);
    if (open && !fresh) {
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
    // The view as it is now (a ref): a drag's release runs the handler of the render it started in, after
    // the press already held the view still where it was drawn (2.47, the live glide).
    if (next && viewRef.current.follow) {
      const held = displayEnd;
      viewRef.current = { follow: false, viewEnd: held };
      setFollow(false);
      setViewEnd(held);
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
  function holdTimeline() {
    if (!viewRef.current.follow) return;
    const held = displayEnd;
    viewRef.current = { follow: false, viewEnd: held };
    setFollow(false);
    setViewEnd(held);
    frozenBySelection.current = true;
  }
  function abandonTimeline() {
    if (selection || !frozenBySelection.current) return;
    goLive();
  }
  const onWaveSelect = (s: Selection | null) => {
    if (sessionId === currentSession.current) select(s ? selectionFrames(s, start, rate) : null);
  };

  /**
   * `share` (2.43, host style sync): tell the host's other page (the companion window, or the browser)
   * which draft is open here; false when this page is following that page.
   */
  function openDraft(draft: Draft | null, share = true) {
    clearTimeout(saveTimer.current);
    if (!draft) {
      closeDraft(share);
      return;
    }
    if (host && share) shareActiveDraft({ sessionId: draft.sessionId, draftId: draft.id });
    setOpenId(draft.id);
    setSelection({ startFrame: draft.startFrame, endFrame: draft.endFrame });
    setName(draft.name ?? '');
    reveal({ startFrame: draft.startFrame, endFrame: draft.endFrame });
  }
  function closeDraft(share = true) {
    clearTimeout(saveTimer.current);
    if (host && share && openId) shareActiveDraft(null);
    setOpenId(null);
    setSelection(null);
    setName('');
    if (frozenBySelection.current) {
      frozenBySelection.current = false;
      setFollow(true);
    }
  }

  // Host style sync (2.43): the draft the host opened on their other page opens here too, and closing it
  // there closes it here. Only changes are followed, so a page opening Mimic keeps a draft it remembers
  // when the other page has none open.
  const shared = useSharedDraft();
  const sharedKey = shared ? `${shared.sessionId}:${shared.draftId}` : '';
  const followed = useRef<string | null>(null);
  useEffect(() => {
    if (!host || sharedKey === followed.current) return;
    if (!shared) {
      const was = followed.current;
      followed.current = '';
      if (was && openId) closeDraft(false);
      return;
    }
    if (shared.draftId === openId) {
      followed.current = sharedKey;
      return;
    }
    // The other page's new draft may not be in this page's list yet: wait for it.
    const draft = mine.find((d) => d.id === shared.draftId && d.sessionId === shared.sessionId);
    if (!draft) return;
    followed.current = sharedKey;
    openDraft(draft, false);
  });

  const selectedMs = selection ? selectionMs(selection, start, rate) : null;
  const suggestion = wordsText(words, selection);
  // The frames and the recording they were selected on (2.41).
  const fields = () => selectionFields(picked, sessionId);

  // Private preview: heard in this browser only, never through Soundpad. Guests have it with a 2.40 companion.
  const {
    previewing,
    paused,
    toggle: togglePreview,
    play: preview,
    stop: stopPreview,
    elapsedMs: previewElapsed,
  } = usePreviewAudio();
  // Another recording (Mimic restarted, the companion restarted): nothing made on the old one may act on this one
  // (2.41). Its waveform, words and selection are already hidden (they carry their recording); here the edit being
  // saved, a preview on its way, the open draft and the held view go too.
  const lastSession = useRef(sessionId);
  useEffect(() => {
    if (!sessionId) return;
    olderPage.current = null;
    prependPosition.current = null;
    setLoadingOlder(false);
    const was = lastSession.current;
    lastSession.current = sessionId;
    if (!was || was === sessionId) return;
    clearTimeout(saveTimer.current);
    stopPreview();
    setPlaying(null);
    setPicked(null);
    setOpenId(null);
    setAnchor(null);
    setHover(null);
    setName('');
    frozenBySelection.current = false;
    setFollow(true);
  }, [sessionId, stopPreview]);
  const canPreview = (host && supports(state.companionVersion, 'preview')) || draftsOn;

  // ---- Playing from the history (2.45): waveform clicks and the playing word ------------------
  const [chosenMode, setChosenMode] = useState<PlayMode>(readPlayMode);
  const gameWhy = gameBlocked({
    host,
    canPlay,
    guestMimic: state.permissions.guestMimic === true,
    held,
  });
  const playMode = effectiveMode(chosenMode, gameWhy);
  const chooseMode = (next: PlayMode) => {
    setChosenMode(next);
    writePlayMode(next);
  };
  const reduced = useReducedMotion();
  const [playing, setPlaying] = useState<Playing | null>(null);
  const [elapsed, setElapsed] = useState<number | null>(null);
  const playToken = useRef(0);
  /** Clips this page made for plays into the game, so the same word again replays its clip (2.45). */
  const clips = useRef(new Map<string, string>());
  // The interval below reads the newest of these, never those of the render it was started in.
  const latest = useRef({ playing, report: state.playback, suspended: !!state.playbackSuspended });
  latest.current = { playing, report: state.playback, suspended: !!state.playbackSuspended };
  const seenEpoch = useRef(false);
  // A preview's highlight goes when the preview does (it ended, was stopped or failed).
  useEffect(() => {
    if (!previewing) setPlaying((p) => (p?.mode === 'preview' ? null : p));
  }, [previewing]);
  useEffect(() => {
    setElapsed(null);
    seenEpoch.current = false;
    if (!playing) return;
    const token = playing.token;
    const tick = () => {
      const p = latest.current.playing;
      if (!p || p.token !== token) return;
      if (p.mode === 'preview') return setElapsed(previewElapsed());
      if (p.startedAt === null || p.epoch === null) return;
      const check = gamePlayEnded(
        { epoch: p.epoch, startedAt: p.startedAt, lengthMs: p.lengthMs },
        latest.current.report,
        latest.current.suspended,
        seenEpoch.current,
        Date.now(),
      );
      seenEpoch.current = check.seen;
      if (check.ended) setPlaying((q) => (q?.token === token ? null : q));
      else setElapsed(Date.now() - p.startedAt);
    };
    tick();
    const timer = setInterval(tick, reduced ? PLAYING_TICK_REDUCED_MS : PLAYING_TICK_MS);
    return () => clearInterval(timer);
  }, [playing?.token, playing?.startedAt, reduced, previewElapsed]);

  /** Starts showing a play: a newer one replaces whatever this window showed as playing. */
  function startPlaying(range: FrameRange, mode: PlayMode) {
    const token = ++playToken.current;
    if (!sessionId) return token;
    setPlaying({
      token,
      sessionId,
      range: { startFrame: range.startFrame, endFrame: range.endFrame },
      mode,
      lengthMs: framesToMs(range.endFrame - range.startFrame, rate),
      startedAt: null,
      epoch: null,
    });
    return token;
  }
  /** A play into the game was answered: it shows as playing from now, or goes when it did not start. */
  function gameAnswered(token: number, played: boolean, data: unknown) {
    const epoch = field(data, 'epoch');
    setPlaying((p) =>
      p?.token !== token ? p : played && epoch ? { ...p, epoch, startedAt: Date.now() } : null,
    );
  }
  /**
   * Plays a range of this recording in the person's mode: a private preview, or into the game. Into the game it
   * is made into a Temp clip and played as one command, so the companion plays it only if nothing newer was
   * asked for by anyone meanwhile (a newer click wins, and nothing obsolete plays); the same range again replays
   * that clip while it is in Temp.
   */
  function playRange(range: FrameRange) {
    if (!sessionId) return;
    const fields = {
      startFrame: Math.round(range.startFrame),
      endFrame: Math.round(range.endFrame),
      sourceId: sessionId,
    };
    if (playMode === 'preview') {
      if (!canPreview) {
        controller.notify('Preview needs a newer companion on the host.', 'error');
        return;
      }
      startPlaying(fields, 'preview');
      void preview('mimicPreview', fields);
      return;
    }
    if (waiting) {
      controller.notify('Waiting for the host to allow more Temp space.', 'error');
      return;
    }
    // A play into the game replaces a preview of this window too: the newest click is what is heard.
    stopPreview();
    const token = startPlaying(fields, 'game');
    const key = clipKey(sessionId, fields);
    const made = clips.current.get(key);
    void (async () => {
      try {
        const temp = tempAssetsSnapshot();
        // Never a clip being deleted or taken out of Temp from this page (2.47 review).
        const gone = (id: string) => deletedNow().has(id) || temp.hidden.has(id);
        if (made && reusableClip(temp.data?.manager.assets, made, gone)) {
          const outcome = await playback.audioPlay(made);
          gameAnswered(token, outcome.result === 'played', outcome.data);
          return;
        }
        const name = wordsText(words, fields);
        const { data, played } = await playback.make(
          'mimicClip',
          { ...fields, ...(name && { name }) },
          'make:mimic',
        );
        const assetId = field(data, 'assetId');
        if (assetId) clips.current.set(key, assetId);
        gameAnswered(token, played, data);
      } catch {
        // The controller shows the refusal.
        setPlaying((p) => (p?.token === token ? null : p));
      }
    })();
  }
  /** Stop the selection transport, whether it is previewing here or playing in Soundpad. */
  function stopPlaying() {
    const p = playing;
    setPlaying(null);
    if (p?.mode === 'game') void playback.stop();
    else stopPreview();
  }
  /** A click on the waveform (ms of its window): plays from there to the end of the audio there. */
  function playAt(ms: number) {
    if (!timeline) return;
    const cut = playFrom(start + msToFrames(ms, rate), timeline);
    // The press held a live view still; with nothing selected it follows live again.
    abandonTimeline();
    if ('reason' in cut) {
      controller.notify(cut.reason, 'error');
      return;
    }
    playRange(cut.range);
  }
  async function clip(play: boolean) {
    const chosen = fields();
    if (!chosen || busy) return;
    setBusy(true);
    try {
      const named = {
        ...chosen,
        ...((name.trim() || suggestion) && { name: name.trim() || suggestion }),
      };
      if (play) {
        stopPreview();
        const token = startPlaying(chosen, 'game');
        try {
          const { data, played } = await playback.make('mimicClip', named, 'make:mimic');
          gameAnswered(token, played, data);
        } catch (e) {
          setPlaying((p) => (p?.token === token ? null : p));
          throw e;
        }
      } else await controller.run('mimicClip', named);
      select(null);
      setName('');
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  /** Capture (2.40): the selection becomes a draft for this person, shown at once. Nothing is imported or played. */
  async function capture(quick?: 'phrase' | 'recent', play = false) {
    if (busy) return;
    const chosen = quick ? null : fields();
    if (!quick && !chosen) return;
    const sequence = playback.getSequence();
    const epoch = controller.getSnapshot().state.playback.epoch;
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
      // A draft on a recording that is no longer shown is not opened over this one (2.41).
      if (draft.sessionId === currentSession.current) {
        openDraft(draft);
        if (
          play &&
          sequence === playback.getSequence() &&
          epoch === controller.getSnapshot().state.playback.epoch
        )
          await playDraftAudio(draft);
      }
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  /** The open draft's name, saved before it is kept or played (and when the field loses focus). */
  async function saveName(draft: Draft) {
    // The name field belongs to the open draft; a draft played or kept from the list keeps its name.
    if (draft.id !== openId) return;
    const next = name.trim();
    if (next === (draft.name ?? '').trim()) return;
    try {
      await controller.run('draftUpdate', {
        draftId: draft.id,
        name: next,
        sourceId: draft.sessionId,
      });
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
        await controller.run('draftKeep', {
          draftId: draft.id,
          sourceId: draft.sessionId,
          ...(close && { action: 'close' }),
        }),
      );
      const label = (draft.id === openId && name.trim()) || draft.name || 'Draft';
      controller.notify(
        `“${label}” is in Temp${result.closed ? '' : draft.id === openId ? '; the draft stays open' : ''}.`,
        'success',
      );
      if (result.closed && draft.id === openId) closeDraft();
      await refreshDrafts();
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  /** Keep in voice library (2.41): kept permanently at once, with its words; the draft closes. */
  async function keepToLibrary(draft: Draft) {
    if (busy || waiting) return;
    setBusy(true);
    try {
      await saveName(draft);
      const result = savedSchema
        .extend({ recordingId: z.string().max(64).nullish(), closed: z.boolean().optional() })
        .parse(await controller.run('draftKeep', { draftId: draft.id, action: 'library' }));
      const recordingId = result.recordingId;
      const fallback = (draft.id === openId && name.trim()) || draft.name || 'Draft';
      // Where it went, with Undo and Rename (2.46); an older companion said nothing more than this.
      if (recordingId) announceSaved({ ...result, recordingId }, fallback, canEditVoices);
      else controller.notify(`“${fallback}” is in the voice library.`, 'success');
      closeDraft();
      await refreshDrafts();
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  /**
   * Save (2.46): the selection goes into the voice library in one press, through a draft that is kept at
   * once (the 2.41 path), named from its words unless a name was typed. A refusal leaves that draft open, so
   * nothing is lost and it can be saved again.
   */
  async function saveSelection() {
    const chosen = fields();
    if (!chosen || busy) return;
    setBusy(true);
    let made: Draft | null = null;
    try {
      const named = name.trim();
      made = draftSchema.parse(
        await controller.run('draftCreate', {
          ...chosen,
          ...(named && { name: named }),
          ...(sessionId && { sourceId: sessionId }),
        }),
      );
      const result = savedSchema
        .extend({ closed: z.boolean().optional() })
        .parse(await controller.run('draftKeep', { draftId: made.id, action: 'library' }));
      announceSaved(result, named || made.name || suggestion || 'Clip', canEditVoices);
      select(null);
      setName('');
      await refreshDrafts();
    } catch {
      /* Controller shows the error; the draft made on the way stays, open. */
      if (made) {
        await refreshDrafts();
        if (made.sessionId === currentSession.current) openDraft(made);
      }
    } finally {
      setBusy(false);
    }
  }
  async function searchHistory() {
    const text = query.trim();
    if (!text || !sessionId) return;
    try {
      setFound(
        historySearchSchema.parse(
          await controller.run('historySearch', { query: text, sourceId: sessionId }),
        ),
      );
    } catch {
      /* Controller shows the error. */
    }
  }
  async function playDraft(draft: Draft) {
    if (busy || held || waiting) return;
    setBusy(true);
    try {
      await saveName(draft);
      await playDraftAudio(draft);
    } catch {
      /* Controller shows the error. */
    } finally {
      setBusy(false);
    }
  }
  async function playDraftAudio(draft: Draft) {
    stopPreview();
    const token = startPlaying(draft, 'game');
    try {
      const { data, played } = await playback.make(
        'draftPlay',
        { draftId: draft.id, sourceId: draft.sessionId },
        'make:mimic',
      );
      gameAnswered(token, played, data);
    } catch (error) {
      setPlaying((current) => (current?.token === token ? null : current));
      throw error;
    }
    await refreshDrafts();
  }
  /**
   * Delete (2.46, was Discard): one press, with Undo. The draft goes from here at once and the companion
   * lets go of it once the Undo notice has gone; Undo opens it again.
   */
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const reopen = useRef<(draft: Draft) => void>(() => {});
  function deleteDraft(draft: Draft, from: Element) {
    if (busy) return;
    const label = (draft.id === openId && name.trim()) || draft.name?.trim();
    // The next draft in the list takes the keyboard when Delete was pressed there.
    const row = from.closest('li[data-draft]');
    const nextRow = (row?.nextElementSibling ?? row?.previousElementSibling) as HTMLElement | null;
    const nextId = nextRow?.dataset.draft;
    deleteWithUndo(
      draft.id,
      label ? `Deleted the draft “${label}”.` : 'Deleted the draft.',
      async () => {
        try {
          await controller.run(
            'draftDiscard',
            { draftId: draft.id, sourceId: draft.sessionId },
            { quiet: true },
          );
        } catch (e) {
          // Already gone (kept and closed elsewhere, a new recording): that is what was asked for.
          const message = e instanceof Error ? e.message : '';
          if (!/draft is gone/i.test(message)) {
            controller.notify(message || 'Could not delete the draft.', 'error');
            throw e;
          }
        } finally {
          void refreshDrafts();
        }
      },
      () => {
        if (mounted.current) reopen.current(draft);
      },
      // Written down: a page closed before the Undo goes still deletes it, the next time (2.47 review).
      { kind: 'draft', sourceId: draft.sessionId },
    );
    if (draft.id === openId) closeDraft();
    // The draft's buttons go with it: the keyboard goes to the next draft in the list, or the heading.
    followFocus(
      from,
      () =>
        (nextId &&
          document.querySelector<HTMLElement>(
            `#mimic-page li[data-draft="${CSS.escape(nextId)}"] .mimic-draft-open`,
          )) ||
        document.getElementById('mimic-title'),
    );
  }
  /** Host (2.40): lets go of someone else's draft, such as one a guest left behind holding recording history. */
  async function discardBand(id: string) {
    if (busy) return;
    setBusy(true);
    try {
      await controller.run('draftDiscard', {
        draftId: id,
        ...(sessionId && { sourceId: sessionId }),
      });
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
      const draft = draftSchema.parse(
        await controller.run('draftCopy', {
          draftId: id,
          ...(sessionId && { sourceId: sessionId }),
        }),
      );
      await refreshDrafts();
      if (draft.sessionId === currentSession.current) openDraft(draft);
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
      // With its recording (2.41): a 2.41 companion refuses a range from another one.
      await controller.run('historyShare', chosen);
      controller.notify('Guests can now see that part of the history.', 'success');
      poller.current?.refresh();
    } catch {
      /* Controller shows the error. */
    }
  }
  async function unshare() {
    try {
      await controller.run('historyUnshare');
      poller.current?.refresh();
    } catch {
      /* Controller shows the error. */
    }
  }
  function previewNow() {
    if (previewing) {
      togglePreview();
      return;
    }
    if (open && sameRange(open, selection)) {
      startPlaying(open, 'preview');
      void preview('mimicPreview', { draftId: open.id, sourceId: open.sessionId });
      return;
    }
    // If the draft's edges are still being saved, use the same current cut for both sound and playhead.
    const chosen = fields();
    if (!chosen || !selection) return;
    startPlaying(selection, 'preview');
    void preview('mimicPreview', chosen);
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
  // The live transcript follows the newest words (2.46); see follow.ts.
  const newest = useFollowNewest<HTMLOListElement>();
  const before = shownTranscript?.before ?? null;
  const canLoadOlder = before !== null && !!timeline && before > oldestFrame(timeline);
  function loadOlderTranscript() {
    if (!canLoadOlder || !sessionId || before === null || olderPage.current) return;
    newest.pause();
    olderPage.current = { session: sessionId, before };
    setLoadingOlder(true);
    poller.current?.nudge();
  }
  useLayoutEffect(() => {
    const el = transcriptList.current;
    const saved = prependPosition.current;
    prependPosition.current = null;
    if (el && saved) el.scrollTop = saved.top + el.scrollHeight - saved.height;
  }, [shownTranscript]);
  /** Jump to live: the view follows live again and the newest words show, in one press (or End). */
  function jumpToNewest() {
    if (timeline && !(follow || end >= timeline.liveFrame)) goLive();
    newest.jump();
  }
  reopen.current = (draft) => openDraft(draft);
  const shortcut = useRef<(e: KeyboardEvent) => void>(() => {});
  useEffect(() => {
    shortcut.current = (e: KeyboardEvent) => {
      if (!timeline?.sessionId || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
      if (!mimicKeysApply(e)) return;
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
      } else if (!typing && e.key === 'End') {
        e.preventDefault();
        jumpToNewest();
      } else if (!typing && QUICK_KEYS[e.key]) {
        e.preventDefault();
        // A number key starts a new selection: an open draft is put away (it stays in the draft history).
        if (open) closeDraft();
        setFocusName(true);
        const held = displayEnd;
        const next = selectionFrames(
          lastSeconds(QUICK_KEYS[e.key], WINDOW_MS),
          windowStart(visibleEnd(), rate),
          rate,
        );
        setSelection(next);
        setName('');
        if (follow) {
          setFollow(false);
          setViewEnd(held);
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

  // Speaker groups (experimental, 2.41), when the host turned them on: fetched when the library says they changed.
  const grouping = libraryOn && libraryStatus?.settings.groupSpeakers === true;
  useEffect(() => {
    if (!grouping || !connected) {
      setSpeakers(null);
      return;
    }
    let live = true;
    controller
      .run('speakerGroups', {}, { quiet: true })
      .then((raw) => {
        if (live) setSpeakers(speakerGroupsSchema.parse(raw));
      })
      .catch(() => {
        if (live) setSpeakers(null);
      });
    return () => {
      live = false;
    };
  }, [grouping, connected, libraryStatus?.revision]);
  // A notice's jump marker (2.41): show and select that moment once the history is here.
  const jumpTo = useRef<{ sessionId: string; range: FrameRange } | null>(null);
  const [jumpKick, setJumpKick] = useState(0);
  useEffect(() => {
    const request = takeOpen('history');
    if (request?.kind === 'history')
      jumpTo.current = {
        sessionId: request.sessionId,
        range: { startFrame: request.startFrame, endFrame: request.endFrame },
      };
  }, []);
  useOpenRequests((request) => {
    if (request.kind !== 'history') return;
    takeOpen('history');
    jumpTo.current = {
      sessionId: request.sessionId,
      range: { startFrame: request.startFrame, endFrame: request.endFrame },
    };
    setJumpKick((k) => k + 1);
  });
  useEffect(() => {
    const target = jumpTo.current;
    if (!target || !timeline?.sessionId) return;
    jumpTo.current = null;
    if (target.sessionId !== timeline.sessionId) return;
    if (open) closeDraft();
    select(target.range, true);
  }, [timeline?.sessionId, jumpKick]);

  const playTitle = held
    ? HELD_BY_HOST
    : waiting
      ? 'Waiting for the host to allow more'
      : undefined;
  const draftName = (d: Draft) => d.name?.trim() || `Draft at ${formatClock(d.createdUtcMs)}`;
  const clipsColumn = (
    <aside className="mimic-page-clips" aria-label="Clips">
      <h2 className="mimic-clips-title">Clips</h2>
      {mine.length > 0 && (
        <div className="mimic-clip-group">
          <h3 id="mimic-drafts-title" className="mimic-eyebrow">
            My drafts <span>{mine.length}</span>
          </h3>
          <ul className="mimic-draft-list" aria-label="My drafts">
            {mine.map((d) => {
              const label = draftName(d);
              const isOpen = d.id === open?.id;
              return (
                <li
                  key={d.id}
                  data-draft={d.id}
                  className={isOpen ? 'open' : undefined}
                  style={{ '--band': bandColor(d.id) } as CSSProperties}
                >
                  <button
                    type="button"
                    className="mimic-draft-open"
                    aria-pressed={isOpen}
                    aria-label={`Open draft ${label}`}
                    title={
                      isOpen ? 'Open now; press to close it' : 'Open it to adjust, keep or play'
                    }
                    onClick={() => (isOpen ? closeDraft() : openDraft(d))}
                  >
                    <span className="mimic-draft-name">{label}</span>
                    <small>
                      {formatClock(d.createdUtcMs)} ·{' '}
                      {(framesToMs(d.endFrame - d.startFrame, rate) / 1000).toFixed(1)} s
                      {d.inTemp ? ' · in Temp' : ''}
                    </small>
                  </button>
                  <button
                    type="button"
                    className="icon-button"
                    data-playback-trigger
                    disabled={!canPlay}
                    aria-disabled={busy || held || !!waiting || undefined}
                    aria-label={`Play draft ${label}`}
                    title={playTitle ?? 'Play it into the game'}
                    onClick={() => void playDraft(d)}
                  >
                    <Play size={14} aria-hidden="true" />
                  </button>
                  <button
                    type="button"
                    className="icon-button"
                    aria-disabled={busy || !!waiting || undefined}
                    aria-label={`Keep draft ${label} in Temp`}
                    title="Keep in Temp: it becomes a sound you can play from the board"
                    onClick={() => void keepDraft(d, false)}
                  >
                    <BookmarkPlus size={14} aria-hidden="true" />
                  </button>
                  <button
                    type="button"
                    className="icon-button mimic-delete"
                    aria-disabled={busy || undefined}
                    aria-label={`Delete draft ${label}`}
                    title="Delete this draft (Undo brings it back)"
                    onClick={(e) => deleteDraft(d, e.currentTarget)}
                  >
                    <Trash2 size={14} aria-hidden="true" />
                  </button>
                </li>
              );
            })}
          </ul>
        </div>
      )}
      {others.length > 0 && (
        <div className="mimic-clip-group">
          <h3 id="mimic-others-title" className="mimic-eyebrow">
            From others <span>{others.length}</span>
          </h3>
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
        </div>
      )}
      {recentClips}
    </aside>
  );
  if (!timeline || !timeline.sessionId)
    return (
      <div className="mimic-history">
        <div className="mimic-main">
          <div className="mimic-empty">
            <p className="muted">
              {!timeline
                ? 'Loading the recording history…'
                : host
                  ? 'Choose a source and start listening.'
                  : 'Waiting for the host to start Mimic.'}
            </p>
          </div>
        </div>
        {clipsColumn}
      </div>
    );

  const behind = behindLiveMs(timeline, end);
  const atLive = follow || end >= timeline.liveFrame;
  // The transcript follows independently of the waveform.
  const following = newest.following;
  // Held still only because something is selected (as the 30 s view did): it still reads as "the last N seconds".
  const moved = !atLive && !frozenBySelection.current;
  const viewClock = wallOf(timeline, end);
  const marks = gapMarks(timeline, start);
  const hidden = hiddenParts(timeline, start);
  const shown = sentences(words);
  const highlight = hover
    ? selectionMs({ startFrame: hover.start, endFrame: hover.end }, start, rate)
    : null;
  // What plays from this window now (2.45): the word being heard, and a line on the waveform where it is.
  const shownPlay = playing && playing.sessionId === sessionId ? playing : null;
  const playFrame =
    shownPlay && elapsed !== null ? playheadFrame(shownPlay.range, elapsed, rate) : null;
  const playingIds = playingWordIds(words, shownPlay?.range ?? null, playFrame, reduced);
  const playhead = playFrame !== null && !reduced ? framesToMs(playFrame - start, rate) : null;
  const bands = others.map((band) => ({
    ...bandSelection(band, start, rate),
    color: bandColor(band.tag),
    label: bandLabel(band),
  }));
  const selectionClock = selection ? wallOf(timeline, selection.startFrame) : null;
  const displayedSelection = selectionOn(wordDragRange, sessionId) ?? selection;
  // The live waveform glides between polls (2.47) while it follows live and nothing holds it still.
  const glideLive =
    atLive && timeline.recording && !reduced && !displayedSelection && main
      ? { endMs: framesToMs(main.end, rate), intervalMs: host ? POLL_MS : GUEST_POLL_MS }
      : undefined;
  const range =
    selection && selectedMs ? (
      <span className="mimic-range">
        {/* Seconds before the edge of the waveform as drawn (the live glide's lag left out, 2.47). */}
        {ago(selectedMs.from, WINDOW_MS - glideShift.current)} →{' '}
        {ago(selectedMs.to, WINDOW_MS - glideShift.current)} ·{' '}
        {(framesToMs(selection.endFrame - selection.startFrame, rate) / 1000).toFixed(1)} s
        {selectionClock !== null && <small> at {formatClock(selectionClock)}</small>}
      </span>
    ) : null;
  const wordAt = (e: PointerEvent<HTMLButtonElement>) => {
    const hit = document.elementFromPoint(e.clientX, e.clientY);
    const surface = e.currentTarget.closest('.mimic-word-track, .mimic-reading-body');
    const word = hit?.closest<HTMLButtonElement>('button[data-word-id]');
    if (word && surface?.contains(word)) return Number(word.dataset.wordId);
    if (surface?.matches('.mimic-word-track') && surface.contains(hit)) {
      // A horizontal drag can keep moving across the time track when labels alternate rows.
      const at = e.clientX;
      for (const timed of surface.querySelectorAll<HTMLButtonElement>('button[data-word-id]')) {
        const box = timed.getBoundingClientRect();
        if (at >= box.left && at <= box.right) return Number(timed.dataset.wordId);
      }
    }
    return null;
  };
  const updateWordDrag = (e: PointerEvent<HTMLButtonElement>) => {
    const gesture = wordDrag.current;
    if (!gesture || gesture.pointer !== e.pointerId || gesture.sessionId !== sessionId) return;
    if (!gesture.moved && Math.hypot(e.clientX - gesture.x, e.clientY - gesture.y) < 5) return;
    gesture.moved = true;
    const last = wordAt(e);
    if (last === null) return;
    const cut = wordCut(words, gesture.first, last, true, timeline);
    if ('range' in cut) setWordDragRange(bind(cut.range, sessionId));
    return cut;
  };
  const cancelWordDrag = (e: PointerEvent<HTMLButtonElement>) => {
    if (wordDrag.current?.pointer !== e.pointerId) return;
    wordDrag.current = null;
    setWordDragRange(null);
    abandonTimeline();
  };
  const wordButton = (w: Word, style?: CSSProperties, lane?: number) => (
    <button
      key={w.id}
      style={style}
      data-word-id={w.id}
      data-timed-lane={lane}
      type="button"
      className={`mimic-word${w.status === 'uncertain' ? ' uncertain' : ''}${hover?.id === w.id && !wordDragRange ? ' hover' : ''}${playingIds.has(w.id) ? (reduced ? ' playing playing-static' : ' playing') : ''}`}
      aria-pressed={wordSelected(w, displayedSelection)}
      title={w.status === 'uncertain' ? `${w.text} · Timing uncertain` : style ? w.text : undefined}
      onMouseEnter={() => setHover(w)}
      onMouseLeave={() => setHover((h) => (h?.id === w.id ? null : h))}
      onFocus={() => setHover(w)}
      onBlur={() => setHover((h) => (h?.id === w.id ? null : h))}
      onPointerDown={(e) => {
        if (
          !sessionId ||
          e.button !== 0 ||
          wordDrag.current ||
          (e.pointerType === 'touch' && !style)
        )
          return;
        draggedClick.current = null;
        wordDrag.current = {
          pointer: e.pointerId,
          first: w.id,
          x: e.clientX,
          y: e.clientY,
          sessionId,
          moved: false,
        };
        e.currentTarget.setPointerCapture(e.pointerId);
        if (style) holdTimeline();
      }}
      onPointerMove={updateWordDrag}
      onPointerUp={(e) => {
        const gesture = wordDrag.current;
        if (gesture?.pointer !== e.pointerId) return;
        const cut = updateWordDrag(e);
        wordDrag.current = null;
        if (!gesture.moved) return;
        draggedClick.current = w.id;
        const result = cut && 'range' in cut ? cut.range : selectionOn(wordDragRange, sessionId);
        setWordDragRange(null);
        if (result && gesture.sessionId === sessionId) {
          select(result);
          setAnchor(gesture.first);
        } else abandonTimeline();
      }}
      onPointerCancel={cancelWordDrag}
      onLostPointerCapture={cancelWordDrag}
      onClick={(e) => {
        if (e.detail > 0 && draggedClick.current === w.id) {
          draggedClick.current = null;
          return;
        }
        const cut = wordCut(words, anchor, w.id, e.shiftKey, timeline);
        if ('reason' in cut) {
          abandonTimeline();
          controller.notify(cut.reason, 'error');
          return;
        }
        select(cut.range);
        if (!e.shiftKey || anchor === null) setAnchor(w.id);
      }}
    >
      <span className={style ? 'mimic-timed-label' : undefined}>{w.text}</span>
    </button>
  );
  // One action bar (2.47): what is selected and what can be done with it, then the quick captures. The row
  // is the same element with or without a draft open, so the name field keeps the keyboard when Capture
  // turns the selection into a draft.
  const actionBar = (
    <div className="mimic-actionbar" data-state={open ? 'draft' : selection ? 'selection' : 'idle'}>
      <div
        className={open ? 'mimic-bar-row mimic-actions mimic-draft' : 'mimic-bar-row mimic-actions'}
        role={open ? 'region' : undefined}
        aria-label={open ? 'Draft' : undefined}
      >
        <div className="mimic-bar-what">
          {open && (
            <i
              className="mimic-bar-swatch"
              style={{ background: bandColor(open.id) }}
              aria-hidden="true"
            />
          )}
          {range && (
            <button
              type="button"
              className="icon-button mimic-bar-clear"
              aria-label={open ? 'Close draft' : 'Clear'}
              title={
                open ? 'Close the draft; it stays in My drafts (Esc)' : 'Clear the selection (Esc)'
              }
              onClick={() => select(null)}
            >
              <X size={14} aria-hidden="true" />
            </button>
          )}
          {range ?? (
            <>
              <span className="mimic-help-keys">
                Click or drag words (Shift-click to extend), or press <kbd>3</kbd> <kbd>5</kbd>{' '}
                <kbd>0</kbd> · <kbd>Enter</kbd> {draftsOn ? 'captures' : 'mimics'} ·{' '}
                <kbd>Shift</kbd>+<kbd>Enter</kbd> plays
              </span>
              <span className="mimic-help-touch">
                Tap a word, drag across the waveform, or choose a quick cut.
              </span>
            </>
          )}
        </div>
        {selection && selectedMs ? (
          <input
            ref={nameField}
            aria-label="Clip name"
            placeholder={suggestion || 'Name (optional)'}
            maxLength={100}
            value={name}
            onChange={(e) => setName(e.target.value)}
            onBlur={() => open && void saveName(open)}
          />
        ) : null}
        <div className="mimic-bar-actions">
          <div className="mimic-transport" role="group" aria-label="Selection playback">
            {canPreview && (selection || previewing) && (
              <button
                type="button"
                disabled={!selection && !previewing}
                data-playback-trigger
                title="Hear the selection in this browser only"
                onClick={previewNow}
              >
                {previewing && !paused ? <Pause size={15} /> : <Headphones size={15} />}
                {previewing ? (paused ? 'Resume preview' : 'Pause preview') : 'Preview'}
              </button>
            )}
            {selection && (
              <button
                type="button"
                className="primary"
                data-playback-trigger
                disabled={!canPlay || !selection}
                aria-disabled={busy || held || !!waiting || undefined}
                title={playTitle ?? 'Play it into the game (Shift+Enter)'}
                onClick={() => {
                  if (busy || held || waiting) return;
                  if (open) void playDraft(open);
                  else void clip(true);
                }}
              >
                <Play size={15} /> {open ? 'Play into game' : 'Mimic & play'}
              </button>
            )}
            {(previewing || shownPlay?.mode === 'game') && (
              <button type="button" className="mimic-transport-stop" onClick={stopPlaying}>
                <Square size={13} /> Stop playing
              </button>
            )}
          </div>
          {open ? (
            <div className="mimic-bar-keep">
              <button
                type="button"
                aria-disabled={busy || !!waiting || undefined}
                title={waiting ? 'Waiting for the host to allow more' : 'Keep it in Temp (Enter)'}
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
              {canLibraryKeep && (
                <button
                  type="button"
                  aria-disabled={busy || !!waiting || undefined}
                  title="Keep it permanently as a voice-library recording, with its words"
                  onClick={() => void keepToLibrary(open)}
                >
                  <BookAudio size={15} /> Keep in voice library
                </button>
              )}
              <button
                type="button"
                className="mimic-delete"
                aria-disabled={busy || undefined}
                title="Delete this draft (Undo brings it back)"
                onClick={(e) => deleteDraft(open, e.currentTarget)}
              >
                <Trash2 size={15} /> Delete
              </button>
            </div>
          ) : selection && selectedMs ? (
            <div className="mimic-bar-keep">
              {draftsOn ? (
                <button
                  title="Open it as a draft you can adjust before keeping or playing it (Enter)"
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
              {canSave && (
                <button
                  className="mimic-save"
                  title="Save it to the voice library now, named from its words (kept where Keep puts files)"
                  aria-disabled={busy || !!waiting || undefined}
                  onClick={() => !busy && !waiting && void saveSelection()}
                >
                  <BookAudio size={15} /> Save
                </button>
              )}
              {host && !demo && timeline.fromFrame === null && (
                <button
                  title="Let guests see and clip this part, although it is from before they joined"
                  onClick={() => void share()}
                >
                  <Share2 size={15} /> Share with guests
                </button>
              )}
            </div>
          ) : null}
        </div>
      </div>
      <div className="mimic-bar-quick">
        <div className="mimic-quick" role="group" aria-label="Quick select">
          {QUICK_SECONDS.map((s) => (
            <button
              key={s}
              title={`Select the last ${s} seconds (key ${s === 10 ? 0 : s})`}
              onClick={() => {
                if (open) closeDraft();
                select(
                  selectionFrames(lastSeconds(s, WINDOW_MS), windowStart(visibleEnd(), rate), rate),
                  true,
                );
              }}
            >
              Last {s} s
            </button>
          ))}
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
            {!selection && !open && canPlay && !held && !waiting && (
              <button
                type="button"
                className="primary"
                aria-disabled={busy || undefined}
                title="Capture the latest phrase and play it into the game, in one press"
                onClick={() => void capture('phrase', true)}
              >
                <Play size={15} /> Play latest phrase
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
  return (
    <div className="mimic-history">
      <div className="mimic-main">
        {!host && (
          <p className="muted mimic-guest-note">
            You see what was recorded since you joined
            {timeline.shared.length ? ', and the parts the host shared with guests' : ''}.
          </p>
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
        <section className="mimic-timeline" aria-labelledby="mimic-timeline-title">
          <h2 id="mimic-timeline-title" className="sr-only">
            Recording timeline
          </h2>
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
            <PlayModeSwitch mode={playMode} set={chooseMode} blocked={gameWhy} />
          </div>
          {main && (
            <MimicWave
              peek={{ windowMs: WINDOW_MS, levels: main.peaks }}
              selection={displayedSelection ? selectionMs(displayedSelection, start, rate) : null}
              onSelect={onWaveSelect}
              extras={{
                storageKey: VIEW_KEY,
                defaultView: DEFAULT_HISTORY_VIEW,
                where: moved ? `ending ${formatSpan(behind)} before live` : undefined,
                endLabel: atLive && timeline.recording ? 'Live' : 'View end',
                marks,
                hidden,
                highlight,
                bands,
                playhead,
                live: glideLive,
                viewEndMs: framesToMs(main.end, rate),
                shiftRef: glideShift,
                renderWords: words.length
                  ? (visibleStart, visibleEnd, width) => {
                      const lanes = [0, 0];
                      const span = visibleEnd - visibleStart;
                      const visibleFrom = start + msToFrames(visibleStart, rate);
                      const visibleTo = start + msToFrames(visibleEnd, rate);
                      return words
                        .filter((word) => word.start < visibleTo && word.end > visibleFrom)
                        .map((word) => {
                          const from = Math.max(visibleStart, framesToMs(word.start - start, rate));
                          const to = Math.min(visibleEnd, framesToMs(word.end - start, rate));
                          const left = ((from - visibleStart) / span) * width;
                          const durationWidth = ((to - from) / span) * width;
                          const labelWidth = Math.min(
                            width,
                            Math.ceil(word.text.length * 7.5 + 14),
                          );
                          const labelLeft = Math.max(
                            0,
                            Math.min(width - labelWidth, left + (durationWidth - labelWidth) / 2),
                          );
                          // Each hit still starts and ends at the word's exact times. Offset the label
                          // within its lane so short words stay readable without covering neighbours.
                          const lane = lanes.findIndex((available) => available <= labelLeft);
                          if (lane !== -1) lanes[lane] = labelLeft + labelWidth + 3;
                          const row = lane === -1 ? lanes.length : lane;
                          return wordButton(
                            word,
                            {
                              left: `${(left / width) * 100}%`,
                              width: `${(durationWidth / width) * 100}%`,
                              '--label-x': lane === -1 ? '0px' : `${labelLeft - left}px`,
                              '--label-w': lane === -1 ? '100%' : `${labelWidth}px`,
                              '--label-y': `${6 + row * 18}px`,
                            } as CSSProperties,
                            row,
                          );
                        });
                    }
                  : undefined,
                ruler: true,
                onPlayAt: playAt,
                onGrab: holdTimeline,
                onAbandon: abandonTimeline,
              }}
            />
          )}
          {overview && (
            <MimicOverview
              range={overview.range}
              peaks={overview.peaks}
              // The wanted view, not the one last fetched: the outline follows a drag at once (2.41).
              view={{
                startFrame: Math.max(overview.range.startFrame, windowStart(end, rate)),
                endFrame: end,
              }}
              rate={rate}
              gaps={timeline.gaps.map((g) => g.atFrame)}
              shared={host ? timeline.shared : []}
              behindMs={behind}
              onMove={moveTo}
            />
          )}
          {marks.length > 0 && (
            <ul className="mimic-gaps" aria-label="Gaps in the recording">
              {marks.map((m) => (
                <li key={m.ms}>{m.label}</li>
              ))}
            </ul>
          )}
        </section>
        {speakers?.enabled && (
          <div className="mimic-speakers" role="group" aria-label="Speakers">
            <span className="muted">Speakers (experimental):</span>
            {speakers.groups.map((g) => (
              <SpeakerControls
                key={g.id}
                group={g}
                voices={libraryStatus?.voices ?? []}
                canEdit={canEditLibrary(libraryStatus, state.permissions, host)}
                sessionId={sessionId}
              />
            ))}
            {speakers.groups.length === 0 && <span className="muted">none yet</span>}
            {speakers.unassigned > 0 && (
              <span className="muted">{speakers.unassigned} unassigned</span>
            )}
          </div>
        )}
        <section
          className="mimic-reading"
          aria-labelledby="mimic-transcript-title"
          data-following={following || undefined}
        >
          <div className="mimic-transcript-head">
            <h2 id="mimic-transcript-title">Transcript</h2>
            {/* Always here (its label changes), so it keeps the keyboard when pressed. While the
                transcript is scrolled back it floats over the newest words' place, where it is seen. */}
            <button
              type="button"
              className={following ? 'mimic-follow live' : 'mimic-follow primary'}
              aria-pressed={following}
              title="Show the newest words and follow them (End)"
              aria-keyshortcuts="End"
              onClick={jumpToNewest}
            >
              <ArrowDownToLine size={14} aria-hidden="true" />{' '}
              {following ? 'Following latest' : 'Jump to latest'}
              <kbd className="mimic-follow-key" aria-hidden="true">
                End
              </kbd>
            </button>
            <div className="mimic-reading-actions">
              {libraryOn && (
                <form
                  className="mimic-search"
                  role="search"
                  aria-label="Search this recording"
                  onSubmit={(e) => {
                    e.preventDefault();
                    void searchHistory();
                  }}
                >
                  <input
                    type="search"
                    aria-label="Words to find in the recording"
                    placeholder="Find words"
                    maxLength={100}
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                  />
                  <button type="submit">
                    <Search size={14} /> Find
                  </button>
                  {found && (
                    <button type="button" onClick={() => setFound(null)}>
                      <X size={14} /> Close results
                    </button>
                  )}
                </form>
              )}
              <button
                type="button"
                onClick={loadOlderTranscript}
                aria-disabled={!canLoadOlder || loadingOlder}
                title={
                  canLoadOlder
                    ? 'Load the 30 seconds before these words'
                    : 'The oldest available transcript is loaded'
                }
              >
                {loadingOlder ? 'Loading…' : 'Earlier words'}
              </button>
            </div>
          </div>
          {found && (
            <div className="mimic-found">
              {found.matches.length ? (
                <ul aria-label="Found in the recording">
                  {found.matches.map((m) => {
                    const at = wallOf(timeline, m.startFrame);
                    return (
                      <li key={m.startFrame}>
                        <button
                          type="button"
                          onClick={() => {
                            if (open) closeDraft();
                            select({ startFrame: m.startFrame, endFrame: m.endFrame }, true);
                          }}
                        >
                          {at !== null && <time>{formatClock(at)}</time>} “{m.context || m.text}”
                        </button>
                      </li>
                    );
                  })}
                </ul>
              ) : (
                <p className="muted">Not found in what you can see of the recording.</p>
              )}
            </div>
          )}
          <ol
            ref={(element) => {
              newest.ref.current = element;
              transcriptList.current = element;
            }}
            onScroll={newest.onScroll}
            onPointerDown={newest.onPointerDown}
            onPointerUp={newest.onPointerUp}
            onPointerCancel={newest.onPointerUp}
            onPointerLeave={newest.onPointerUp}
            className="mimic-transcript mimic-words mimic-reading-body"
            aria-label="Transcript"
          >
            {shown.length ? (
              shown.map((sentence, index) => (
                <li
                  key={sentence[0].id}
                  className={
                    index === shown.length - 1
                      ? 'mimic-reading-line mimic-reading-newest'
                      : 'mimic-reading-line'
                  }
                >
                  <time>
                    {formatClock(wallOf(timeline, sentence[0].start) ?? 0).replace(/\s[AP]M$/i, '')}
                  </time>
                  {speakers?.enabled && sentence[0].speaker != null && (
                    <SpeakerChip id={sentence[0].speaker} groups={speakers} />
                  )}
                  <span className="mimic-sentence">{sentence.map((w) => wordButton(w))}</span>
                </li>
              ))
            ) : (
              <li className="muted mimic-reading-empty">
                {mimic?.transcribing
                  ? 'Listening… words appear here a few seconds after they are said.'
                  : host
                    ? 'Turn on Transcript in Mimic settings to see the words here.'
                    : 'Words appear here when the host turns on transcripts.'}
              </li>
            )}
          </ol>
        </section>
        {actionBar}
      </div>
      {clipsColumn}
    </div>
  );
}

/**
 * Where waveform clicks play (2.45): Preview here (this browser only) or Play into game, as in
 * the Voices window. Play into game is offered only while this person may play; Stop ends what this window plays.
 */
function PlayModeSwitch({
  mode,
  set,
  blocked,
}: {
  mode: PlayMode;
  set: (mode: PlayMode) => void;
  blocked: string | null;
}) {
  return (
    <div className="mimic-play-mode">
      <div
        role="radiogroup"
        aria-label="Waveform clicks"
        title="Where a click on the waveform plays"
      >
        <span>Clicks play</span>
        <label title="Heard in this browser only; nobody else hears it and nothing plays in the game">
          {/* The short words are shown; the full name is read (2.47: the switch sits in the timeline's row). */}
          <input
            type="radio"
            name="mimic-play-mode"
            aria-label="Preview here"
            checked={mode === 'preview'}
            onChange={() => set('preview')}
          />
          <Headphones size={13} aria-hidden="true" /> Preview
        </label>
        <label title={blocked ?? 'Played through Soundpad, so everyone in the game hears it'}>
          <input
            type="radio"
            name="mimic-play-mode"
            aria-label="Play into game"
            checked={mode === 'game'}
            disabled={!!blocked}
            onChange={() => set('game')}
          />
          <Play size={13} aria-hidden="true" /> Game
        </label>
        {blocked && <span className="sr-only">{blocked}.</span>}
      </div>
    </div>
  );
}

/** A phrase's speaker group, coloured; "Speaker N" until a person names it. */
function SpeakerChip({ id, groups }: { id: number; groups: SpeakerGroups }) {
  const group = groups.groups.find((g) => g.id === id);
  if (!group) return null;
  return (
    <span
      className="mimic-speaker"
      style={{ color: group.color }}
      title="Grouped by how it sounds (experimental)"
    >
      {group.label}
    </span>
  );
}

/** Naming a speaker group, and linking it to a library voice: only ever done by a person. */
function SpeakerControls({
  group,
  voices,
  canEdit,
  sessionId,
}: {
  group: SpeakerGroups['groups'][number];
  voices: { id: string; name: string }[];
  canEdit: boolean;
  /** The recording the group belongs to (2.41 review): group numbers start again with each one. */
  sessionId: string | null;
}) {
  const sourceId = sessionId ?? undefined;
  const [name, setName] = useState(group.name ?? '');
  return (
    <form
      className="mimic-speakers"
      onSubmit={(e) => {
        e.preventDefault();
        void controller
          .run('speakerName', { groupId: group.id, name: name.trim(), sourceId })
          .catch(() => {});
      }}
    >
      <span className="mimic-speaker" style={{ color: group.color }}>
        {group.label} · {group.phrases}
      </span>
      {canEdit && (
        <>
          <input
            type="search"
            aria-label={`Name ${group.label}`}
            placeholder="Name"
            maxLength={40}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <select
            aria-label={`Link ${group.label} to a library voice`}
            value={group.voiceId ?? ''}
            onChange={(e) =>
              void controller
                .run('speakerLink', { groupId: group.id, voiceId: e.target.value, sourceId })
                .catch(() => {})
            }
          >
            <option value="">No library voice</option>
            {voices.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name}
              </option>
            ))}
          </select>
        </>
      )}
    </form>
  );
}
