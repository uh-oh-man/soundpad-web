import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { createRoot } from 'react-dom/client';
import {
  AudioLines,
  Search,
  Link,
  Settings2,
  Music2,
  Plus,
  Minus,
  RefreshCw,
  Users,
  X,
  ArrowUpRight,
  ShieldCheck,
  Headphones,
  ChevronRight,
  Radio,
  Mic2,
} from 'lucide-react';
import { controller } from './controller';
import { flattenCategories, type Sound } from './protocol';
import { z } from 'zod';
import { useWorkspace, viewModes, type Workspace } from './workspace';
import { arrangeSounds, boardTitle, type Totals } from './arrange';
import {
  APP_VERSION_LABEL,
  MIMIC_COMPANION,
  REQUIRED_COMPANION,
  companionAtLeast,
} from './version';
import { SoundCollection } from './components/SoundCollection';
import { Player } from './components/Player';
import { IconEditor } from './components/IconEditor';
import { PersonalLayout, OrganizeSound } from './components/PersonalLayout';
import { Statistics } from './components/Statistics';
import { Notifications } from './components/Notifications';
import { TempManager } from './components/TempManager';
import { SessionUpload } from './components/SessionUpload';
import { Modal, dialogOpen } from './components/Modal';
import { Sidebar } from './components/Sidebar';
import { SettingsDialog } from './components/SettingsDialog';
import { MimicPanel } from './components/MimicPanel';
import { useAppearance } from './appearance';
import { GuestName } from './components/GuestName';
import { organization } from './shared-layout';
import { clearWaveforms } from './waveform-cache';
import './tokens.css';
import './styles.css';
import './v2.css';
import './polish.css';
import './mimic.css';

const PROJECT_URL = 'https://github.com/uh-oh-man/soundpad-web';
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

function ProjectFooter() {
  return (
    <small className="project-footer">
      Soundpad Web · v{APP_VERSION_LABEL} ·{' '}
      <a href={PROJECT_URL} target="_blank" rel="noreferrer">
        GitHub
      </a>{' '}
      · <span>made by AI</span>
    </small>
  );
}

/** Space pauses/resumes known playback (or stops it) unless focus is in something that uses Space. */
function useSpaceToggle() {
  useEffect(() => {
    function space(event: KeyboardEvent) {
      if (
        event.code !== 'Space' ||
        event.defaultPrevented ||
        event.ctrlKey ||
        event.altKey ||
        event.metaKey
      )
        return;
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
      const playback = current.state.playback;
      if (!['PLAYING', 'PAUSED', 'SEEKING'].includes(playback.status)) return;
      event.preventDefault();
      if (event.repeat) return;
      if (
        playback.soundId &&
        playback.epoch &&
        (current.mode === 'host' || current.state.permissions.guestSeeking !== false)
      )
        void controller
          .run('pause', { soundId: playback.soundId, epoch: playback.epoch })
          .catch(() => {});
      else if (playback.status !== 'PAUSED') void controller.run('stop').catch(() => {});
    }
    document.addEventListener('keydown', space, true);
    return () => document.removeEventListener('keydown', space, true);
  }, []);
}

/** "/" focuses search, except while typing or while a dialog is open. */
function useSearchShortcut() {
  useEffect(() => {
    function key(e: KeyboardEvent) {
      if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || dialogOpen()) return;
      const target = e.target;
      if (
        target instanceof HTMLElement &&
        (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))
      )
        return;
      e.preventDefault();
      document.querySelector<HTMLInputElement>('input[aria-label="Search sounds"]')?.focus();
    }
    document.addEventListener('keydown', key);
    return () => document.removeEventListener('keydown', key);
  }, []);
}

function statusLabel(
  mode: string,
  connected: boolean,
  host: boolean,
  available: boolean,
  status: string,
) {
  if (mode === 'landing') return 'Not connected';
  if (!connected) return status.split('.')[0];
  if (!available) return 'Soundpad unavailable';
  return host ? 'Hosting' : 'Connected';
}

function Landing({ view }: { view: ReturnType<typeof controller.getSnapshot> }) {
  const repositoryUrl = import.meta.env.VITE_REPOSITORY_URL as string | undefined;
  return (
    <main className="welcome">
      <div className="eyebrow">
        <Radio size={15} /> YOUR SOUNDPAD, A LINK AWAY
      </div>
      <h1>
        A soundboard.
        <br />
        <span>For the whole party.</span>
      </h1>
      <p>
        Share your Soundpad library with friends. They tap a sound, your Soundpad plays it. No
        accounts. Session audio goes directly to your host.
      </p>
      {view.resumeAvailable && (
        <button className="primary resume-host" onClick={() => controller.resume()}>
          Resume hosting
        </button>
      )}
      <div className="setup-card">
        <div className="setup-icon">
          <Headphones size={29} />
        </div>
        <div>
          <h2>Hosting? Start on your PC.</h2>
          <p>
            Open Soundpad, then launch the portable companion. Your library appears here
            automatically.
          </p>
        </div>
      </div>
      <div className="steps">
        <span>
          <b>1</b> Launch the companion
        </span>
        <ChevronRight size={16} />
        <span>
          <b>2</b> Copy Invite
        </span>
        <ChevronRight size={16} />
        <span>
          <b>3</b> Let friends play
        </span>
      </div>
      <p className="join-note">Joining a friend? Open their invitation link to connect.</p>
      {repositoryUrl && /^https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/?$/.test(repositoryUrl) && (
        <a className="text-link" href={repositoryUrl} target="_blank" rel="noreferrer">
          Source & downloads <ArrowUpRight size={15} />
        </a>
      )}
      {view.status === 'Connection failed' && (
        <p role="alert" className="warning">
          {view.notice}
        </p>
      )}
      <div className="privacy">
        <ShieldCheck size={16} /> Audio stays on the host. Only commands travel between browsers.
      </div>
    </main>
  );
}

function App() {
  const appearance = useAppearance();
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const { state, mode, status } = view;
  const [category, setCategory] = useState('all');
  const [myCategory, setMyCategory] = useState('all');
  const [search, setSearch] = useState('');
  const [settings, setSettings] = useState(false);
  const [mimicOpen, setMimicOpen] = useState(false);
  const [managing, setManaging] = useState(false);
  const [editing, setEditing] = useState<Sound | null>(null);
  const [organizing, setOrganizing] = useState<Sound | null>(null);
  const [statistics, setStatistics] = useState<Sound | null>(null);
  const [totals, setTotals] = useState<Totals>({});
  const [pending, setPending] = useState(0);
  const personal = useWorkspace(state.hostId);
  const w = personal.workspace;
  useSpaceToggle();
  useSearchShortcut();
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
  const mimicSupported = companionAtLeast(
    state.companionVersion,
    MIMIC_COMPANION.major,
    MIMIC_COMPANION.minor,
  );
  const canMimic = connected && mimicSupported && (host || state.permissions.guestMimic === true);
  const mimicListening = mode !== 'landing' && !!state.mimic?.active;

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
    void controller
      .run('statistics')
      .then((raw) => {
        if (live) setTotals(totalsSchema.parse(raw).totals);
      })
      .catch(() => {});
    return () => {
      live = false;
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

  return (
    <div className={`app-shell ${mode === 'landing' ? 'is-landing' : 'is-connected'}`}>
      <header className="topbar">
        <a href={location.pathname} className="brand" aria-label="Soundpad Web home">
          <span className="brand-icon">
            <AudioLines size={23} />
          </span>
          <span>
            soundpad<span className="brand-light"> web</span>
          </span>
          <span className="release-version">v{APP_VERSION_LABEL}</span>
        </a>
        <span className="edition">BROWSER REMOTE</span>
        <div className="top-actions">
          <span className={`status ${connected && state.available ? 'online' : ''}`} title={status}>
            <i />
            {statusLabel(mode, connected, host, state.available, status)}
          </span>
          {mimicListening && (
            <button
              className="mimic-badge"
              title={`Mimic is keeping the last 30 seconds of ${state.mimic?.source ?? 'a source'} on the host's PC`}
              disabled={!canMimic}
              onClick={() => setMimicOpen(true)}
            >
              <i /> Mimic on
            </button>
          )}
          {mode !== 'landing' && (
            <input
              className="profile-name"
              key={view.profile.name}
              aria-label="Your display name"
              title="Display names are not authenticated identities"
              maxLength={40}
              defaultValue={view.profile.name}
              onBlur={(e) => controller.rename(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') e.currentTarget.blur();
              }}
            />
          )}
          {mode !== 'landing' && (
            <button
              className="icon-button"
              aria-label="Access settings"
              onClick={() => setSettings(true)}
            >
              <Settings2 size={20} />
            </button>
          )}
        </div>
      </header>
      {mode === 'landing' ? (
        <Landing view={view} />
      ) : (
        <>
          <div className="workspace">
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
            />
            <main className="soundboard">
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
              <div className="board-heading">
                <div>
                  <div className="eyebrow">{host ? 'YOUR SOUNDBOARD' : 'SHARED SOUNDBOARD'}</div>
                  <h1>{title}</h1>
                  <p>
                    {search
                      ? `${sounds.length} matching sound${sounds.length === 1 ? '' : 's'}`
                      : `${sounds.length} sound${sounds.length === 1 ? '' : 's'} · ${host ? 'Ready when your friends are' : 'Tap a sound to play on the host'}`}
                  </p>
                </div>
                <div className="board-actions">
                  {canMimic && (
                    <button onClick={() => setMimicOpen(true)}>
                      <Mic2 size={17} /> Mimic
                    </button>
                  )}
                  {host && (
                    <>
                      <button
                        className="icon-button"
                        title="Refresh library"
                        aria-label="Refresh library"
                        disabled={!connected || pending > 0}
                        onClick={() => {
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
                  )}
                </div>
              </div>
              {(!connected || state.error) && (
                <div className="connection-banner" role="status">
                  <Radio size={18} />
                  <span>
                    {!connected ? status : state.error}{' '}
                    {!connected &&
                      host &&
                      'Allow the browser’s local-network request if prompted. Keep the companion running.'}
                  </span>
                </div>
              )}
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
              <div className="toolbar">
                <label className="search">
                  <Search size={18} />
                  <input
                    aria-label="Search sounds"
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
                <span className="grid-caption">
                  {canPlay
                    ? 'CLICK TO PLAY'
                    : !state.available
                      ? 'WAITING FOR SOUNDPAD'
                      : 'PLAYBACK DISABLED'}
                </span>
              </div>
              <div className="view-toolbar">
                <div className="layout-switch" role="group" aria-label="Layout">
                  <button
                    aria-pressed={w?.layout !== 'mine'}
                    onClick={() => personal.update((current) => ({ ...current, layout: 'host' }))}
                  >
                    Host Layout
                  </button>
                  <button
                    disabled={!w}
                    aria-pressed={w?.layout === 'mine'}
                    onClick={() => personal.update((current) => ({ ...current, layout: 'mine' }))}
                  >
                    My Layout
                  </button>
                </div>
                <label>
                  View
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
                  Sort
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
                    personal.update((current) => ({ ...current, descending: !current.descending }))
                  }
                >
                  {w?.descending ? '↓' : '↑'}
                </button>
                <button disabled={!w} onClick={() => setManaging(true)}>
                  Manage layout
                </button>
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
                  <Users size={15} />
                  {host
                    ? `${view.guests.length} connected guest${view.guests.length === 1 ? '' : 's'}`
                    : 'Guest access'}
                </span>
                <span>Audio plays through the host’s Soundpad</span>
                <ProjectFooter />
              </div>
            </main>
          </div>
          <Player
            state={state}
            disabled={!canPlay}
            canSeek={canSeek}
            workspace={w}
            update={personal.update}
          >
            <div className="counter">
              <div className="counter-label">
                KICK COUNTER<small>It happens.</small>
              </div>
              <button
                aria-label="Decrease Kick counter"
                disabled={!canCount || pending > 0 || state.counter === 0}
                onClick={() => void run('counter', { delta: -1 })}
              >
                <Minus size={16} />
              </button>
              <output aria-label="Kick count">{state.counter}</output>
              <button
                aria-label="Increase Kick counter"
                disabled={!canCount || pending > 0}
                onClick={() => void run('counter', { delta: 1 })}
              >
                <Plus size={16} />
              </button>
            </div>
          </Player>
        </>
      )}
      <Notifications />
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
      {mimicOpen && mode !== 'landing' && (
        <Modal label="Mimic" onClose={() => setMimicOpen(false)}>
          <MimicPanel state={state} host={host} connected={connected} canPlay={canPlay} />
        </Modal>
      )}
      {mode === 'landing' && <ProjectFooter />}
      {view.needsName && <GuestName initial={view.profile.name} />}
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
    </div>
  );
}
createRoot(document.getElementById('root')!).render(<App />);
