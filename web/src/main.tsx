import {
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type CSSProperties,
} from 'react';
import { createRoot } from 'react-dom/client';
import { Search, Link, Music2, RefreshCw, Users, X, Radio, OctagonPause } from 'lucide-react';
import { controller } from './controller';
import { flattenCategories, type Sound } from './protocol';
import { z } from 'zod';
import { useWorkspace, viewModes, type Workspace } from './workspace';
import { arrangeSounds, boardTitle, type Totals } from './arrange';
import { REQUIRED_COMPANION, companionAtLeast, supports } from './version';
import { DEMO_COPY, hostCopy } from './hosting';
import { SoundCollection } from './components/SoundCollection';
import { Player } from './components/Player';
import { IconEditor } from './components/IconEditor';
import { PersonalLayout, OrganizeSound } from './components/PersonalLayout';
import { Statistics } from './components/Statistics';
import { NoticeAnnouncer, Notifications } from './components/Notifications';
import { TempManager } from './components/TempManager';
import { SessionUpload } from './components/SessionUpload';
import { Modal, dialogOpen } from './components/Modal';
import { Sidebar } from './components/Sidebar';
import { SettingsDialog } from './components/SettingsDialog';
import { LobbyRequests } from './components/LobbyRequests';
import { BudgetRequests, QuitPromptDialog, TempRecovery } from './components/StoragePanel';
import { useBudgetSync, useDraftStore, useDraftSync } from './mimic/draft-store';
import type { ConnectionInfo } from './network/session';
import { WhatsNew } from './components/WhatsNew';
import { MimicPanel } from './components/MimicPanel';
import { TempCopyEditor } from './components/EditCopyForm';
import { VoicesPanel } from './components/VoicesPanel';
import { PinnedSentences, SentencesPanel } from './components/SentencesPanel';
import { useSentenceSync } from './library/sentence-store';
import { useLibrarySync } from './library/voice-store';
import { requestTemp } from './edit-copy';
import { removeNewTempAudio } from './remove-temp';
import { guestHeld, HELD_BY_HOST, playback } from './playback';
import { useAppearance } from './appearance';
import { GuestName } from './components/GuestName';
import { organization } from './shared-layout';
import { clearWaveforms } from './waveform-cache';
import { useAccessibilityPrefs } from './motion';
import { KickCounter } from './components/KickCounter';
import { Landing, PROJECT_URL, ProjectFooter } from './components/shell/Landing';
import { DemoBanner } from './components/shell/DemoBanner';
import { TopBar } from './components/shell/TopBar';
import { SectionHeader } from './components/shell/SectionHeader';
import { AppShell, useMediaQuery, useViewportWidth } from './components/shell/AppShell';
import { SectionNav, navPlacement } from './components/shell/SectionNav';
import { SectionView } from './components/shell/SectionView';
import { EditorPanel } from './components/shell/EditorPanel';
import { SoundEditor } from './components/shell/SoundEditor';
import { ActivityView, useSharedActivity } from './components/shell/ActivityView';
import { Splitter, clampEditorWidth, editorMaxWidth } from './components/shell/Splitter';
import {
  useEditorRequests,
  useViewRequests,
  type EditorRequest,
  type PanelMode,
  type ShellView,
} from './shell';
import { recall, remember } from './sections';
import { usePref } from './prefs';
import { useHostStyleSync } from './ui-sync';
import { currentOpener, returnFocus } from './focus-return';
import { notify, useUnreadNotices } from './notifications';
import { useNoticeSounds } from './notice-sound';
import { useTempAssetsSync } from './temp-assets';
import { StageHContext, organizeActive } from './stage-h';
import { ariaChord, matchesShortcut, useShortcut } from './shortcuts';
import { SetupCard } from './components/setup/SetupCard';
import { SoundpadFix } from './components/setup/SoundpadFix';
import { SetupGuide } from './components/setup/SetupGuide';
import { UpdateOffer } from './components/updates/UpdateOffer';
import { BackupLayoutSync } from './components/backups/BackupLayoutSync';
// Stylesheets in their cascade order (Stage G spec 3.1). Each later file overrides earlier ones at equal
// specificity; see the ownership note at the top of each file. DM Sans (self-hosted, SIL OFL 1.1, see
// THIRD-PARTY-NOTICES.md) comes right after the tokens: its variable-weight faces, latin and latin-ext by
// unicode-range, so a page in English loads only the latin file. --font-ui names it first.
import './tokens.css';
import '@fontsource-variable/dm-sans/wght.css';
import './studio.css';
import './controls.css';
import './mimic.css';
import './voices.css';
import './sentences.css';
import './shell.css';
import './playback.css';
import './surfaces.css';
import './backups.css';
import './updates.css';
import './organize.css';
import './setup.css';

const viewLabels: Record<(typeof viewModes)[number], string> = {
  icons: 'Icons',
  large: 'Large Icons',
  list: 'List',
  details: 'Details',
  waveforms: 'Waveforms',
};
const sortLabels: [Workspace['sort'], string][] = [
  ['original', 'Original order'],
  ['name', 'Name'],
  ['duration', 'Duration'],
  ['native', 'Soundpad plays'],
  ['web', 'Web plays'],
  ['last', 'Last web played'],
];
const totalsSchema = z.object({
  totals: z.record(
    z.string(),
    z.object({ total: z.number(), lastPlayedAt: z.number().nullable() }),
  ),
});

/**
 * Space (2.44: the Play/Pause shortcut, Settings → Keyboard) pauses/resumes known playback (or stops
 * it) unless focus is in something that uses the key.
 */
function useSpaceToggle() {
  useEffect(() => {
    function space(event: KeyboardEvent) {
      // Organize mode (2.44): Space there selects a sound; it never plays or pauses.
      if (organizeActive(event.target as Element)) return;
      if (!matchesShortcut('playPause', event) || event.defaultPrevented) return;
      const target = event.target;
      if (target instanceof HTMLElement) {
        if (target.isContentEditable || target.closest('input,textarea,select,[role="dialog"]'))
          return;
        const media = target.closest(
          '.tile-play-strip,.tile-art-button,.tile-wave-surface,.wave-surface',
        );
        if (target.closest('button,a,[role="slider"]') && !media) return;
        const button = target.closest('button');
        if (button && !button.matches('.tile-play-strip,.tile-art-button')) return;
      }
      const current = controller.getSnapshot();
      if (
        !current.state.available ||
        !['Host ready', 'Connected'].includes(current.status) ||
        (current.mode !== 'host' && !current.state.permissions.remotePlayback)
      )
        return;
      const playing = current.state.playback;
      if (!['PLAYING', 'PAUSED', 'SEEKING'].includes(playing.status)) return;
      event.preventDefault();
      if (event.repeat) return;
      if (
        playing.soundId &&
        playing.epoch &&
        (current.mode === 'host' || current.state.permissions.guestSeeking !== false)
      )
        void playback.pause();
      else if (playing.status !== 'PAUSED') void playback.stop();
    }
    document.addEventListener('keydown', space, true);
    return () => document.removeEventListener('keydown', space, true);
  }, []);
}

/**
 * Shift+Escape (2.44: the Panic shortcut, Settings → Keyboard) is the host's Panic (2.35) from anywhere
 * on the page, dialogs included, except while typing in a field. It runs before a dialog or waveform
 * would take the Escape for itself.
 */
function usePanicShortcut() {
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (!matchesShortcut('panic', event)) return;
      const target = event.target;
      if (
        target instanceof HTMLElement &&
        (target.isContentEditable ||
          target.matches(
            'select, textarea, input:not([type="checkbox"]):not([type="radio"]):not([type="button"]):not([type="range"])',
          ))
      )
        return;
      const current = controller.getSnapshot();
      if (current.mode !== 'host' || !supports(current.state.companionVersion, 'intents')) return;
      event.preventDefault();
      event.stopPropagation();
      if (!event.repeat) void playback.panic();
    }
    document.addEventListener('keydown', key, true);
    return () => document.removeEventListener('keydown', key, true);
  }, []);
}

/**
 * "/" (2.44: the Search shortcut, Settings → Keyboard) focuses the current view's search (2.43:
 * `[data-view-search]`), except while typing or while a dialog is open. The editor panel is not a
 * modal dialog, so "/" works while it is open.
 */
function useSearchShortcut() {
  useEffect(() => {
    function key(e: KeyboardEvent) {
      if (!matchesShortcut('search', e) || dialogOpen()) return;
      const target = e.target;
      if (
        target instanceof HTMLElement &&
        (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))
      )
        return;
      const search =
        document.querySelector<HTMLInputElement>('#board [data-view-search]') ??
        document.querySelector<HTMLInputElement>(
          '#board input[type="search"], #board input[aria-label^="Search"]',
        );
      if (!search) return;
      e.preventDefault();
      search.focus();
    }
    document.addEventListener('keydown', key);
    return () => document.removeEventListener('keydown', key);
  }, []);
}

/**
 * Stop (2.44, Settings → Keyboard; no key until someone sets one) stops what plays, for the host and
 * for guests allowed remote playback, except while typing in a field.
 */
function useStopShortcut() {
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (!matchesShortcut('stop', event) || event.defaultPrevented) return;
      const target = event.target;
      if (
        target instanceof HTMLElement &&
        (target.isContentEditable ||
          target.matches(
            'select, textarea, input:not([type="checkbox"]):not([type="radio"]):not([type="button"]):not([type="range"])',
          ))
      )
        return;
      const current = controller.getSnapshot();
      if (
        !current.state.available ||
        !['Host ready', 'Connected'].includes(current.status) ||
        (current.mode !== 'host' && !current.state.permissions.remotePlayback)
      )
        return;
      event.preventDefault();
      if (!event.repeat) void playback.stop();
    }
    document.addEventListener('keydown', key, true);
    return () => document.removeEventListener('keydown', key, true);
  }, []);
}

/** "35 s" or "2 min 5 s": how long a guest has been reconnecting (2.37). */
function elapsed(ms: number) {
  const s = Math.max(0, Math.floor(ms / 1000));
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`;
}

/** The current time, once a second while `on`; for counters a person watches. */
function useNow(on: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!on) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [on]);
  return now;
}

/**
 * What a guest who is not connected is told (2.37), with the step they can take: ask for an extra
 * place, ask again, or wait while it reconnects by itself. Playback clicks made meanwhile are dropped.
 */
function GuestConnection({ status, info }: { status: string; info?: ConnectionInfo }) {
  const reconnecting = !!info?.reconnecting && !info.lobby && !info.ended;
  const now = useNow(reconnecting);
  if (reconnecting)
    return (
      <span>
        <strong>Reconnecting for {elapsed(now - info!.since)}.</strong> Playback is unavailable
        until the host is back; it keeps trying by itself.
        {info!.diagnosis ? ` ${info!.diagnosis.text}` : ''}
      </span>
    );
  return (
    <span>
      {status}{' '}
      {info?.lobby === 'full' && info.canAskExtra && (
        <button className="primary" onClick={() => controller.askForPlace()}>
          Ask for an extra place
        </button>
      )}
      {info?.lobby === 'expired' && (
        <button className="primary" onClick={() => controller.askAgain()}>
          Ask again
        </button>
      )}
    </span>
  );
}

function App() {
  const appearance = useAppearance();
  useAccessibilityPrefs();
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const { state, mode, status } = view;
  const [category, setCategory] = useState('all');
  const [myCategory, setMyCategory] = useState('all');
  const [search, setSearch] = useState('');
  const [settings, setSettings] = useState(false);
  const [managing, setManaging] = useState(false);
  const [editing, setEditing] = useState<Sound | null>(null);
  const [organizing, setOrganizing] = useState<Sound | null>(null);
  const [statistics, setStatistics] = useState<Sound | null>(null);
  const [totals, setTotals] = useState<Totals>({});
  const [pending, setPending] = useState(0);
  const [resuming, setResuming] = useState(false);
  const personal = useWorkspace(state.hostId);
  const w = personal.workspace;
  useSpaceToggle();
  usePanicShortcut();
  useSearchShortcut();
  useStopShortcut();
  const searchKey = useShortcut('search');
  useEffect(() => {
    void controller.start();
  }, []);

  const host = mode === 'host';
  const protocol = state.protocol ?? 1;
  const connected = status === (host ? 'Host ready' : 'Connected');
  const canPlay = connected && state.available && (host || state.permissions.remotePlayback);
  const canCount = connected && (host || state.permissions.guestCounter);
  const canSeek = protocol >= 2 && (host || state.permissions.guestSeeking !== false);
  const needsCompanionUpdate = !companionAtLeast(
    state.companionVersion,
    REQUIRED_COMPANION.major,
    REQUIRED_COMPANION.minor,
  );
  const repositoryUrl = import.meta.env.VITE_REPOSITORY_URL as string | undefined;
  const mimicSupported = supports(state.companionVersion, 'mimic');
  const canMimic = connected && mimicSupported && (host || state.permissions.guestMimic === true);
  const mimicListening = mode !== 'landing' && !!state.mimic?.active;
  // Drafts, guest Keep and storage budgets (2.40): a guest also needs a host page that relays them.
  const draftsReady =
    connected && supports(state.companionVersion, 'drafts') && (host || controller.relaysDrafts());
  useDraftSync(state, draftsReady && canMimic);
  useBudgetSync(state, host, draftsReady);
  const { budget } = useDraftStore();
  // The voice library (2.41): a guest also needs a host page that relays its commands.
  const libraryReady =
    connected &&
    supports(state.companionVersion, 'voiceLibrary') &&
    (host || controller.relaysLibrary());
  useLibrarySync(state, libraryReady, canMimic);
  // Sentences and the speaking palette (2.42): on the library, and (guests) a host page that relays them.
  const sentencesReady =
    libraryReady &&
    supports(state.companionVersion, 'sentences') &&
    (host || controller.relaysSentences());
  useSentenceSync(state, sentencesReady);
  // Host style sync (2.43): the host's browser and the companion window share style and Mimic's open
  // draft through the companion's ui.json. Never on a guest's page or in the example.
  useHostStyleSync(
    state,
    host && connected && !view.demo && supports(state.companionVersion, 'uiSync'),
  );
  // A notice's Jump opens Mimic at that moment and Open shows the recording in Voices: the library
  // store asks the shell (showView / requestEditor), and the view or panel takes the request.
  // After the host's Panic (2.35): guests cannot start audio until the host resumes; the host can.
  const suspended = mode !== 'landing' && state.playbackSuspended === true;
  const held = guestHeld(state, host);

  // ---- The shell (2.43): which view shows, the editor panel, navigation style and widths ----
  const [navStyle] = usePref('nav');
  const [sidebarRail, setSidebarRail] = usePref('sidebarRail');
  const [storedWidth, setStoredWidth] = usePref('editorWidth');
  const narrow = useMediaQuery('(max-width: 1000px)');
  const belowWide = useMediaQuery('(max-width: 1279px)');
  const viewport = useViewportWidth();
  const [section, setSection] = useState<ShellView>(() => recall('shell').view ?? 'board');
  // A view whose feature is not (yet) available shows the Soundboard; the choice is kept for when it is.
  const shown: ShellView =
    section === 'voices' && libraryReady
      ? 'voices'
      : section === 'sentences' && sentencesReady
        ? 'sentences'
        : 'board';
  const [panel, setPanel] = useState<EditorRequest | null>(restorePanel);
  const [panelFocus, setPanelFocus] = useState(0);
  const [minimized, setMinimized] = useState(false);
  const [dragWidth, setDragWidth] = useState<number | null>(null);
  const opener = useRef<HTMLElement | null>(null);
  const returning = useRef<{ kind: EditorRequest['kind'] } | null>(null);
  const shownPanel = mode !== 'landing' ? panel : null;
  const panelMode: PanelMode | 'closed' = !shownPanel
    ? 'closed'
    : narrow
      ? minimized
        ? 'minimized'
        : 'sheet'
      : 'docked';
  // With the panel docked under 1280 px (the VR or companion window) the sidebar folds to icons, so the
  // board keeps its room; in top tabs style the sidebar can be folded by hand.
  const rail =
    !narrow && ((panelMode === 'docked' && belowWide) || (navStyle === 'tabs' && sidebarRail));
  const sidebarWidth = rail ? 64 : navStyle === 'tabs' && shown !== 'board' ? 0 : 234;
  const editorMax = editorMaxWidth(viewport, sidebarWidth);
  const editorWidth = clampEditorWidth(dragWidth ?? storedWidth, viewport, sidebarWidth);
  const placement = navPlacement(navStyle, narrow);
  const unread = useUnreadNotices();
  useNoticeSounds();
  useSharedActivity(view, host);
  useTempAssetsSync(
    connected && !needsCompanionUpdate && mode !== 'landing',
    state.managedAudioVersion,
    // Whose Temp list it is: leaving the example or a session (the landing page) drops the old one.
    mode === 'landing' ? '' : `${view.demo ? 'example' : 'host'}:${state.hostId ?? ''}`,
  );

  /** Opens the panel at `request`, or switches it there; the heading gets the keyboard. */
  function openEditor(request: EditorRequest) {
    if (controller.getSnapshot().mode === 'landing') return;
    if (!panel) opener.current = currentOpener();
    setPanel((current) =>
      // Asking again for what is open keeps it as it is (a copy editor keeps its unsaved fields).
      current && JSON.stringify(current) === JSON.stringify(request) ? current : request,
    );
    setMinimized(false);
    setPanelFocus((n) => n + 1);
  }
  function closePanel() {
    if (!panel) return;
    if (panel.kind === 'temp-edit')
      remember('tempEdit', { sourceId: undefined, fields: undefined });
    returning.current = { kind: panel.kind };
    setPanel(null);
    setMinimized(false);
  }
  // Once the panel has gone, focus goes back to what opened it, else to the nav's Mimic or the board.
  useEffect(() => {
    if (panel || !returning.current) return;
    const { kind } = returning.current;
    returning.current = null;
    const back = opener.current;
    opener.current = null;
    const active = document.activeElement;
    // Something outside took the keyboard meanwhile (Keep's form in Temp): leave it there.
    if (active && active !== document.body && !active.closest('#editor-panel')) return;
    returnFocus(back, () =>
      kind === 'mimic'
        ? document.querySelector<HTMLElement>('.section-nav [data-section="mimic"]')
        : document.getElementById('board'),
    );
  }, [panel]);
  // A panel that comes back after the landing page (a reconnect) is not a new open: focus stays where it is.
  useEffect(() => {
    if (mode === 'landing') setPanelFocus(0);
  }, [mode]);
  useEditorRequests(openEditor);
  useViewRequests((request) => switchView(request.view));
  useEffect(() => {
    remember('shell', {
      view: section,
      panel: panel
        ? {
            kind: panel.kind,
            ...(panel.kind === 'temp-edit' && { sourceId: panel.sourceId }),
            ...(panel.kind === 'sound-edit' && { soundId: panel.soundId }),
          }
        : null,
    });
  }, [section, panel]);

  /** Shows a main view; where each one was scrolled to is kept for coming back. */
  function switchView(next: ShellView) {
    const board = document.getElementById('board');
    if (board) remember(shown, { scrollTop: board.scrollTop });
    setSection(next);
    // On a phone the editor sheet covers the view: fold it to its bar, so the chosen view shows and can be
    // used (the editor keeps its state, and "Show editor" brings it back).
    if (panelMode === 'sheet') setMinimized(true);
  }
  useLayoutEffect(() => {
    const board = document.getElementById('board');
    if (!board) return;
    const top = recall(shown).scrollTop ?? 0;
    board.scrollTop = top;
    // The view's content (tiles, lists) lays out over a few frames: keep asking for the position
    // until it holds, for at most about half a second.
    let frame = 0;
    let tries = 0;
    const again = () => {
      if (Math.abs(board.scrollTop - top) <= 1 || ++tries > 30) return;
      board.scrollTop = top;
      frame = requestAnimationFrame(again);
    };
    frame = requestAnimationFrame(again);
    return () => cancelAnimationFrame(frame);
  }, [shown]);

  // Tell everyone when a Mimic clip is made (also by the host's F6 mid-game). Clips that existed
  // before this tab joined are not announced.
  const newestClip = state.mimic?.recent?.[0];
  const seenClip = useRef<number | null>(null);
  useEffect(() => {
    if (mode === 'landing') return;
    const at = newestClip?.at ?? 0;
    if (seenClip.current === null) seenClip.current = at;
    else if (newestClip && at > seenClip.current) {
      seenClip.current = at;
      // The host can take a clip straight back out (2.29), like Undo after Edit copy, and so can the
      // guest who made it (2.32; the companion checks it really is theirs).
      const clipId = newestClip.assetId;
      const mine =
        !host &&
        newestClip.by === view.profile.name &&
        state.permissions.guestUploads !== false &&
        supports(state.companionVersion, 'guestUndo');
      controller.notify(
        `${newestClip.by} mimicked \u201c${newestClip.name}\u201d`,
        'success',
        host || mine
          ? {
              label: 'Undo',
              run: () =>
                void removeNewTempAudio(clipId)
                  .then(() => controller.notify('Clip removed from Temp.', 'success'))
                  .catch(() => {}),
            }
          : undefined,
      );
    }
  }, [mode, newestClip, host]);

  // Mimic records other people's voices, so everyone is told when it starts, changes source or
  // stops, and anyone joining while it is on is told right away.
  // How much a 2.39 companion keeps (its recording history), or the 30 s of older ones.
  const kept = state.mimic?.history
    ? `the last ${Math.round(state.mimic.history.seconds / 60)} min`
    : 'the last 30 s';
  const listeningTo =
    mode !== 'landing' && state.mimic?.active ? (state.mimic.source ?? 'a source') : null;
  const heardListening = useRef<string | null>(null);
  useEffect(() => {
    if (listeningTo === heardListening.current) return;
    if (listeningTo)
      controller.notify(
        host
          ? `Mimic is listening to ${listeningTo}. Everyone in the session can see this.`
          : `The host's Mimic is listening to ${listeningTo} (${kept}, kept on their PC).`,
      );
    else if (heardListening.current) controller.notify('Mimic stopped listening.');
    heardListening.current = listeningTo;
  }, [listeningTo, host]);

  const organizationText = w ? JSON.stringify(organization(w)) : '';
  useEffect(() => {
    if (!connected || !organizationText) return;
    const timer = setTimeout(() => {
      void controller
        .publishLayout(JSON.parse(organizationText))
        .catch((e) => controller.notify(e instanceof Error ? e.message : String(e), 'error'));
    }, 700);
    return () => clearTimeout(timer);
  }, [connected, organizationText]);

  useEffect(() => {
    if (protocol < 2 || !connected) return;
    let live = true;
    // Every accepted play bumps the version. Ask once a run of plays pauses (2.35), not once per
    // play, and stay quiet when it fails: the counts are a detail and the next play asks again.
    const timer = setTimeout(() => {
      void controller
        .run('statistics', {}, { quiet: true })
        .then((raw) => {
          if (live) setTotals(totalsSchema.parse(raw).totals);
        })
        .catch(() => {});
    }, 250);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [state.statisticsVersion, protocol, connected]);

  const categories = useMemo(
    () => flattenCategories(state.library.categories),
    [state.library.categories],
  );
  const librarySize = useMemo(
    () => state.library.sounds.filter((s) => !s.internalVariant).length,
    [state.library.sounds],
  );
  const selection = { category, myCategory, search };
  const sounds = useMemo(
    () => arrangeSounds(state, w, { category, myCategory, search }, totals),
    // Only the library matters from state; playback updates must not re-sort the board.
    [state.library, w, category, myCategory, search, totals],
  );
  const title = boardTitle(state, w, selection);

  function selectHost(id: string) {
    setCategory(id);
    personal.update((current) => ({ ...current, layout: 'host' }));
    setSearch('');
  }
  function selectMine(id: string) {
    personal.update((current) => ({ ...current, layout: 'mine' }));
    setMyCategory(id);
    setSearch('');
  }
  async function run(
    op: Parameters<typeof controller.run>[0],
    fields: Parameters<typeof controller.run>[1] = {},
  ) {
    setPending((p) => p + 1);
    try {
      await controller.run(op, fields);
    } catch {
      /* Controller displays the failure. */
    } finally {
      setPending((p) => p - 1);
    }
  }

  const ribbon = {
    playback: state.playback,
    hostId: state.hostId,
    held: held || suspended,
    active: true,
  };
  const sectionNav = (where: 'sidebar' | 'topbar') =>
    mode !== 'landing' && placement === where ? (
      <SectionNav
        view={shown}
        panelKind={shownPanel?.kind ?? null}
        placement={where}
        canMimic={canMimic}
        libraryReady={libraryReady}
        sentencesReady={sentencesReady}
        showView={switchView}
        openMimic={() => openEditor({ kind: 'mimic' })}
      />
    ) : null;
  const sheetShown = panelMode === 'sheet';
  const panelTitle = !shownPanel
    ? ''
    : shownPanel.kind === 'mimic'
      ? 'Mimic'
      : shownPanel.kind === 'temp-edit'
        ? 'Copy editor'
        : shownPanel.kind === 'activity'
          ? 'Activity'
          : `Edit ${state.library.sounds.find((s) => s.id === shownPanel.soundId)?.name ?? 'sound'}`;

  return (
    <StageHContext.Provider value={{ state, controller, mode, demo: view.demo, notify }}>
      <AppShell
        className={`app-shell ${mode === 'landing' ? 'is-landing' : 'is-connected'} ${held ? 'playback-held' : ''}`}
        style={
          panelMode === 'docked'
            ? ({ '--editor-cur': `${editorWidth}px` } as CSSProperties)
            : undefined
        }
        landing={mode === 'landing'}
        view={shown}
        nav={navStyle}
        panelMode={panelMode}
        rail={rail}
        topbar={
          <TopBar
            view={view}
            connected={connected}
            host={host}
            mimicListening={mimicListening}
            kept={kept}
            canMimic={canMimic}
            openMimic={() => openEditor({ kind: 'mimic' })}
            openSettings={() => setSettings(true)}
            nav={sectionNav('topbar')}
            activity={{
              unread,
              open: shownPanel?.kind === 'activity',
              show: () => openEditor({ kind: 'activity' }),
            }}
          />
        }
        banner={mode !== 'landing' && view.demo ? <DemoBanner inert={sheetShown} /> : undefined}
        sidebar={
          <Sidebar
            state={state}
            host={host}
            categories={categories}
            librarySize={librarySize}
            workspace={w}
            selection={selection}
            selectHost={selectHost}
            selectMine={selectMine}
            update={personal.update}
            manage={() => setManaging(true)}
            hostNote={
              view.demo
                ? DEMO_COPY.sidebar
                : hostCopy(view.hosting, state.companionVersion, view.ownerWindow).sidebar
            }
            nav={sectionNav('sidebar')}
            library={shown === 'board'}
            rail={
              navStyle === 'tabs' && !narrow
                ? { collapsed: sidebarRail, toggle: () => setSidebarRail(!sidebarRail) }
                : undefined
            }
            inert={sheetShown}
          />
        }
        board={
          <main
            id="board"
            className="soundboard"
            tabIndex={-1}
            aria-label={
              shown === 'board' ? 'Soundboard' : shown === 'voices' ? 'Voices' : 'Sentences'
            }
            inert={sheetShown}
          >
            {shown === 'voices' ? (
              <SectionView view="voices" ribbon={ribbon}>
                <VoicesPanel state={state} host={host} connected={connected} canPlay={canPlay} />
              </SectionView>
            ) : shown === 'sentences' ? (
              <SectionView view="sentences" ribbon={ribbon}>
                <SentencesPanel
                  state={state}
                  host={host}
                  connected={connected}
                  canPlay={canPlay}
                  // Section memory: the sentence that was open when Sentences was last left.
                  openId={recall('sentences').sentenceId ?? null}
                />
              </SectionView>
            ) : (
              <>
                <WhatsNew projectUrl={repositoryUrl || PROJECT_URL} />
                {connected && (protocol < 2 || needsCompanionUpdate) && (
                  <aside className="companion-update" role="status">
                    <strong>
                      {host
                        ? protocol < 2
                          ? 'Update your Windows companion for V2'
                          : `Update your Windows companion to ${REQUIRED_COMPANION.label}`
                        : 'Ask the host to update the Windows companion'}
                    </strong>
                    <p>
                      Basic playback still works. Install the latest companion for session audio,
                      reverse playback, shared waveforms and guest Spotify.
                    </p>
                    {host && (
                      <a
                        href={`${repositoryUrl || PROJECT_URL}/releases/latest/download/SoundpadWeb.exe`}
                      >
                        Download the updated companion
                      </a>
                    )}
                  </aside>
                )}
                <SectionHeader
                  className="board-heading"
                  eyebrow={host ? 'YOUR SOUNDBOARD' : 'SHARED SOUNDBOARD'}
                  title={title}
                  ribbon={ribbon}
                  actions={
                    host && (
                      <>
                        <button
                          className="icon-button"
                          title="Refresh library"
                          aria-label="Refresh library"
                          disabled={!connected}
                          aria-disabled={pending > 0 || undefined}
                          onClick={() => {
                            if (pending > 0) return;
                            clearWaveforms();
                            void run('refresh');
                          }}
                        >
                          <RefreshCw size={19} />
                        </button>
                        <button
                          className="primary"
                          disabled={!view.invite}
                          onClick={() => void controller.copyInvite()}
                        >
                          <Link size={17} /> Copy Invite
                        </button>
                      </>
                    )
                  }
                >
                  <p>
                    {search
                      ? `${sounds.length} matching sound${sounds.length === 1 ? '' : 's'}`
                      : `${sounds.length} sound${sounds.length === 1 ? '' : 's'} · ${host ? 'Ready when your friends are' : 'Tap a sound to play on the host'}`}
                  </p>
                  {host && (
                    // Who is connected, at the top where the host looks (it used to sit in the footer).
                    <p className="connected-guests" role="status" aria-live="polite">
                      <Users size={15} aria-hidden="true" />
                      <span>{`${view.guests.length} connected guest${view.guests.length === 1 ? '' : 's'}`}</span>
                      {view.guests.length > 0 && (
                        <span className="guest-names">
                          {view.guests
                            .slice(0, 4)
                            .map((id, i) => view.people[id]?.name ?? `Guest ${i + 1}`)
                            .join(', ')}
                          {view.guests.length > 4 && ` +${view.guests.length - 4} more`}
                        </span>
                      )}
                    </p>
                  )}
                  {host && <LobbyRequests requests={view.requests} />}
                  {host && budget?.host && <BudgetRequests requests={budget.host.requests} />}
                  {host && !!state.tempRecovery && <TempRecovery clips={state.tempRecovery} />}
                </SectionHeader>
                {host && <SetupCard state={state} />}
                {(!connected || state.error) && (
                  <div className="connection-banner" role="status">
                    <Radio size={18} />
                    {!connected && !host ? (
                      <GuestConnection status={status} info={view.connection} />
                    ) : connected && host && state.soundpadProblem && !state.available ? (
                      // 2.44 (gate setup): what to do about the reason the companion found.
                      <SoundpadFix
                        problem={state.soundpadProblem}
                        elevated={state.companionElevated === true}
                        connected={connected}
                      />
                    ) : (
                      <span>
                        {!connected ? status : state.error}{' '}
                        {!connected &&
                          host &&
                          'Allow the browser’s local-network request if prompted. Keep the companion running.'}
                      </span>
                    )}
                  </div>
                )}
                {suspended && (
                  // Stays on screen until the host resumes (2.35); a notice would time out.
                  <div className="playback-suspended" role="status">
                    <OctagonPause size={18} aria-hidden="true" />
                    {host ? (
                      <>
                        <span>
                          <strong>Guest playback is paused.</strong> You can still play; guests
                          cannot until you resume.
                        </span>
                        <button
                          className="primary"
                          disabled={!connected}
                          aria-disabled={resuming || undefined}
                          onClick={() => {
                            if (resuming) return;
                            setResuming(true);
                            void playback.resume().finally(() => setResuming(false));
                          }}
                        >
                          Resume guest playback
                        </button>
                      </>
                    ) : (
                      <span>
                        <strong>{HELD_BY_HOST}</strong>. Play buttons work again when the host
                        resumes it.
                      </span>
                    )}
                  </div>
                )}
                {sentencesReady && <PinnedSentences canPlay={canPlay && (host || !suspended)} />}
                {!!view.layouts.length && (
                  <section className="shared-layouts" aria-label="Shared guest layouts">
                    <small>Shared layouts · updates automatically</small>
                    {view.layouts.map((layout) => (
                      <button
                        key={layout.owner}
                        disabled={!w}
                        onClick={() => {
                          personal.update((current) => ({
                            ...current,
                            ...layout.organization,
                            layout: 'mine',
                          }));
                          setMyCategory('all');
                          setSearch('');
                        }}
                      >
                        {layout.name} · {layout.organization.folders.length} folders · Copy into My
                        Layout
                      </button>
                    ))}
                  </section>
                )}
                {/* One toolbar row (2.43): search, layout and view, Temp and session audio. */}
                <div className="board-toolbar">
                  <div className="toolbar">
                    <label className="search">
                      <Search size={18} />
                      <input
                        aria-label="Search sounds"
                        aria-keyshortcuts={ariaChord(searchKey)}
                        data-view-search
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        onKeyDown={(e) => {
                          if (e.key === 'Escape' && search) {
                            e.preventDefault();
                            setSearch('');
                          }
                        }}
                        placeholder="Search all sounds…"
                      />
                      {search && (
                        <button aria-label="Clear search" onClick={() => setSearch('')}>
                          <X size={15} />
                        </button>
                      )}
                      <kbd>/</kbd>
                    </label>
                    <label className="mobile-categories">
                      <span className="sr-only">Category</span>
                      <select
                        aria-label="Category"
                        value={w?.layout === 'mine' ? 'personal:' + myCategory : category}
                        onChange={(e) => {
                          const value = e.target.value;
                          if (value.startsWith('personal:'))
                            selectMine(value.slice('personal:'.length));
                          else selectHost(value);
                        }}
                      >
                        <option value="all">All sounds</option>
                        {w && (
                          <optgroup label="My layout">
                            <option value="personal:all">My sounds</option>
                            <option value="personal:favorites">Favorites</option>
                            {w.folders.map((f) => (
                              <option key={f.id} value={'personal:' + f.id}>
                                {f.name}
                              </option>
                            ))}
                          </optgroup>
                        )}
                        {categories.map(({ category: c, depth }) => (
                          <option value={c.id} key={c.id}>
                            {'　'.repeat(depth) + (c.name || 'Unnamed category')}
                          </option>
                        ))}
                      </select>
                    </label>
                  </div>
                  <div className="view-toolbar">
                    <div className="layout-switch" role="group" aria-label="Layout">
                      <button
                        aria-pressed={w?.layout !== 'mine'}
                        onClick={() =>
                          personal.update((current) => ({ ...current, layout: 'host' }))
                        }
                      >
                        Host Layout
                      </button>
                      <button
                        disabled={!w}
                        aria-pressed={w?.layout === 'mine'}
                        onClick={() =>
                          personal.update((current) => ({ ...current, layout: 'mine' }))
                        }
                      >
                        My Layout
                      </button>
                    </div>
                    <label>
                      <span className="toolbar-word">View</span>
                      <select
                        aria-label="View mode"
                        disabled={!w}
                        value={w?.view ?? 'icons'}
                        onChange={(e) =>
                          personal.update((current) => ({
                            ...current,
                            view: e.target.value as Workspace['view'],
                          }))
                        }
                      >
                        {viewModes.map((mode) => (
                          <option key={mode} value={mode}>
                            {viewLabels[mode]}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label>
                      <span className="toolbar-word">Sort</span>
                      <select
                        aria-label="Sort sounds"
                        disabled={!w}
                        value={w?.sort ?? 'original'}
                        onChange={(e) =>
                          personal.update((current) => ({
                            ...current,
                            sort: e.target.value as Workspace['sort'],
                          }))
                        }
                      >
                        {sortLabels.map(([value, label]) => (
                          <option key={value} value={value}>
                            {label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <button
                      disabled={!w || w.sort === 'original'}
                      aria-label={`Sort ${w?.descending ? 'descending' : 'ascending'}; toggle direction`}
                      title={w?.descending ? 'Descending' : 'Ascending'}
                      onClick={() =>
                        personal.update((current) => ({
                          ...current,
                          descending: !current.descending,
                        }))
                      }
                    >
                      {w?.descending ? '↓' : '↑'}
                    </button>
                    <button disabled={!w} onClick={() => setManaging(true)}>
                      Manage layout
                    </button>
                  </div>
                  <SessionUpload
                    disabled={
                      !connected ||
                      needsCompanionUpdate ||
                      (!host && state.permissions.guestUploads === false)
                    }
                  />
                  {!needsCompanionUpdate && (
                    <TempManager state={state} host={host} connected={connected} />
                  )}
                  <span className="grid-caption" data-ok={(!held && canPlay) || undefined}>
                    {held
                      ? 'PAUSED BY HOST'
                      : canPlay
                        ? 'CLICK TO PLAY'
                        : !state.available
                          ? 'WAITING FOR SOUNDPAD'
                          : 'PLAYBACK DISABLED'}
                  </span>
                </div>
                {w && w.layout === 'mine' && (
                  <p className="workspace-save">
                    {personal.status} · Personal organization never changes Soundpad.
                  </p>
                )}
                {sounds.length ? (
                  <SoundCollection
                    sounds={sounds}
                    state={state}
                    host={host}
                    canPlay={canPlay}
                    workspace={w}
                    update={personal.update}
                    edit={setEditing}
                    organize={setOrganizing}
                    stats={setStatistics}
                    totals={totals}
                    folderId={
                      w?.layout === 'mine' && myCategory !== 'all' && myCategory !== 'favorites'
                        ? myCategory
                        : undefined
                    }
                  />
                ) : (
                  <div className="empty">
                    <Music2 size={35} />
                    <h2>
                      {search
                        ? 'No sounds found'
                        : state.available
                          ? 'Nothing in this category yet'
                          : 'Your sounds belong here'}
                    </h2>
                    <p>
                      {search
                        ? 'Try another name. Search includes every category.'
                        : state.available
                          ? 'Add sounds in Soundpad, then refresh the library.'
                          : host
                            ? 'Open Soundpad normally. We’ll pick up your library automatically.'
                            : 'Waiting for the host’s Soundpad library.'}
                    </p>
                  </div>
                )}
                <div className="board-footer">
                  <span>
                    {view.demo
                      ? 'Example sounds play in this browser'
                      : 'Audio plays through the host’s Soundpad'}
                  </span>
                  <ProjectFooter />
                </div>
              </>
            )}
          </main>
        }
        dock={<Notifications />}
        splitter={
          panelMode === 'docked' ? (
            <Splitter
              width={editorWidth}
              max={editorMax}
              onChange={setDragWidth}
              onCommit={(width) => {
                setStoredWidth(width);
                setDragWidth(null);
              }}
            />
          ) : undefined
        }
        panel={
          shownPanel && (
            <EditorPanel
              kind={shownPanel.kind}
              mode={panelMode === 'closed' ? 'docked' : panelMode}
              title={panelTitle}
              summary={shownPanel.kind === 'mimic' && mimicListening ? 'listening' : 'open'}
              focusKey={panelFocus}
              onClose={closePanel}
              onMinimize={() => setMinimized(true)}
              onRestore={() => {
                setMinimized(false);
                setPanelFocus((n) => n + 1);
              }}
            >
              {shownPanel.kind === 'mimic' ? (
                <MimicPanel
                  state={state}
                  host={host}
                  connected={connected}
                  canPlay={canPlay}
                  onTempRequest={(kind, assetId) => {
                    // Keep happens in the Temp list on the Soundboard; Edit switches the panel to the
                    // copy editor (2.43: Mimic used to close for both).
                    if (kind === 'keep') closePanel();
                    requestTemp(kind, assetId);
                  }}
                />
              ) : shownPanel.kind === 'temp-edit' ? (
                <TempCopyEditor
                  key={shownPanel.sourceId}
                  state={state}
                  host={host}
                  sourceId={shownPanel.sourceId}
                  fields={shownPanel.fields}
                  onFields={(fields) =>
                    remember('tempEdit', { sourceId: shownPanel.sourceId, fields })
                  }
                  onDone={closePanel}
                />
              ) : shownPanel.kind === 'sound-edit' ? (
                <SoundEditor
                  key={shownPanel.soundId}
                  state={state}
                  host={host}
                  canPlay={canPlay}
                  soundId={shownPanel.soundId}
                  focus={shownPanel.focus}
                  workspace={w}
                  update={personal.update}
                  held={held}
                  onDone={closePanel}
                />
              ) : (
                <ActivityView />
              )}
            </EditorPanel>
          )
        }
        player={
          mode !== 'landing' && (
            <Player
              state={state}
              host={host}
              connected={connected}
              disabled={!canPlay}
              canSeek={canSeek}
              workspace={w}
              update={personal.update}
            >
              <KickCounter state={state} send={run} pending={pending} canEdit={canCount} />
            </Player>
          )
        }
      >
        {mode === 'landing' && <Landing view={view} />}
        {mode === 'landing' && <ProjectFooter />}
        {view.needsName && <GuestName initial={view.profile.name} />}
      </AppShell>
      <NoticeAnnouncer />
      {/* Stage H (2.44): the setup guide, the update offer and the layout attach for backups. */}
      <SetupGuide />
      <UpdateOffer />
      <BackupLayoutSync />
      {settings && (
        <Modal label="Session & access" onClose={() => setSettings(false)}>
          <SettingsDialog
            view={view}
            connected={connected}
            appearance={appearance}
            run={run}
            close={() => setSettings(false)}
          />
        </Modal>
      )}
      {view.quitPrompt && host && <QuitPromptDialog clips={view.quitPrompt.clips} />}
      {editing && (
        <Modal label="Sound icon" onClose={() => setEditing(null)}>
          <IconEditor sound={editing} close={() => setEditing(null)} />
        </Modal>
      )}
      {statistics && (
        <Modal label="Sound statistics" onClose={() => setStatistics(null)}>
          <Statistics sound={statistics} version={state.statisticsVersion ?? 0} />
        </Modal>
      )}
      {managing && w && (
        <Modal label="My layout" onClose={() => setManaging(false)}>
          <PersonalLayout
            workspace={w}
            update={personal.update}
            replace={personal.replace}
            sounds={state.library.sounds}
            status={personal.status}
          />
        </Modal>
      )}
      {organizing && w && (
        <Modal label="Organize sound" onClose={() => setOrganizing(null)}>
          <OrganizeSound sound={organizing} workspace={w} update={personal.update} />
        </Modal>
      )}
    </StageHContext.Provider>
  );
}

/** The panel this tab showed before a reload (2.43 section memory), if it can be shown again. */
function restorePanel(): EditorRequest | null {
  const saved = recall('shell').panel;
  if (!saved) return null;
  if (saved.kind === 'mimic' || saved.kind === 'activity') return { kind: saved.kind };
  if (saved.kind === 'temp-edit' && saved.sourceId) {
    const edit = recall('tempEdit');
    return {
      kind: 'temp-edit',
      sourceId: saved.sourceId,
      ...(edit.sourceId === saved.sourceId && edit.fields && { fields: edit.fields }),
    };
  }
  if (saved.kind === 'sound-edit' && saved.soundId)
    return { kind: 'sound-edit', soundId: saved.soundId };
  return null;
}
createRoot(document.getElementById('root')!).render(<App />);
