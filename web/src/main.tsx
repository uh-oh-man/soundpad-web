import React, { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { createRoot } from 'react-dom/client';
import {
  AudioLines,
  Search,
  Link,
  Settings2,
  Folder,
  Music2,
  Plus,
  Minus,
  RefreshCw,
  Users,
  X,
  Trash2,
  ArrowUpRight,
  ShieldCheck,
  Headphones,
  ChevronRight,
  Radio,
  RotateCcw,
  LogOut,
} from 'lucide-react';
import { controller } from './controller';
import { flattenCategories, selectSounds, soundKeys, hasSound, type Sound } from './protocol';
import { z } from 'zod';
import { useWorkspace, viewModes, moveSound } from './workspace';
import { SoundCollection, type Totals } from './components/SoundCollection';
import { Player } from './components/Player';
import { IconEditor } from './components/IconEditor';
import { PersonalLayout, OrganizeSound } from './components/PersonalLayout';
import { Statistics } from './components/Statistics';
import { SpotifyPanel } from './components/SpotifyPanel';
import { Notifications } from './components/Notifications';
import { TempManager } from './components/TempManager';
import { SessionUpload } from './components/SessionUpload';
import { useAppearance, themes, type Theme } from './appearance';
import { GuestName } from './components/GuestName';
import { organization } from './shared-layout';
import { clearWaveforms } from './waveform-cache';
import './tokens.css';
import './styles.css';
import './v2.css';
import './polish.css';

function ProjectFooter() {
  return (
    <small className="project-footer">
      Soundpad Web · v2.3 ·{' '}
      <a href="https://github.com/uh-oh-man/soundpad-web" target="_blank" rel="noreferrer">
        GitHub
      </a>{' '}
      · <span>made by AI</span>
    </small>
  );
}
function App() {
  const appearance = useAppearance();
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const { state, mode, status } = view;
  const [category, setCategory] = useState('all');
  const [search, setSearch] = useState('');
  const [settings, setSettings] = useState(false);
  const [editing, setEditing] = useState<Sound | null>(null);
  const personal = useWorkspace(state.hostId);
  const w = personal.workspace;
  const [myCategory, setMyCategory] = useState('all');
  const [managing, setManaging] = useState(false);
  const [organizing, setOrganizing] = useState<Sound | null>(null);
  const [statistics, setStatistics] = useState<Sound | null>(null);
  const [totals, setTotals] = useState<Totals>({});
  const canSeek = (state.protocol ?? 1) >= 2 && hostPermission();
  function hostPermission() {
    return mode === 'host' || state.permissions.guestSeeking !== false;
  }
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
  const [pending, setPending] = useState(0);
  const host = mode === 'host';
  const connected = status === (host ? 'Host ready' : 'Connected');
  const canPlay = connected && state.available && (host || state.permissions.remotePlayback);
  const canCount = connected && (host || state.permissions.guestCounter);
  const organizationText = w ? JSON.stringify(organization(w)) : '';
  useEffect(() => {
    if (!connected || !organizationText) return;
    const timer = setTimeout(() => {
      void controller
        .publishLayout(JSON.parse(organizationText))
        .catch((e) => controller.notify(String(e)));
    }, 700);
    return () => clearTimeout(timer);
  }, [connected, organizationText]);
  const [nativeMajor, nativeMinor] = (state.companionVersion ?? '0.0').split('.').map(Number);
  const needsCompanionUpdate = !(nativeMajor > 2 || (nativeMajor === 2 && nativeMinor >= 3));
  const repositoryUrl = import.meta.env.VITE_REPOSITORY_URL as string | undefined;
  const categories = useMemo(
    () => flattenCategories(state.library.categories),
    [state.library.categories],
  );
  const sounds = useMemo(() => {
    let list = selectSounds(state, category, search);
    if (w?.layout === 'mine') {
      const ids = search
        ? null
        : myCategory === 'favorites'
          ? w.favorites
          : w.folders.find((f) => f.id === myCategory)?.soundIds;
      const byId = new Map(
        state.library.sounds
          .filter((s) => !s.internalVariant)
          .flatMap((s) => soundKeys(s).map((key) => [key, s] as const)),
      );
      list = (
        ids ? ids.map((id) => byId.get(id)).filter((s): s is Sound => !!s) : state.library.sounds
      ).filter(
        (s) =>
          !s.internalVariant &&
          !hasSound(w.hidden, s) &&
          (!search || s.name.toLocaleLowerCase().includes(search.toLocaleLowerCase())),
      );
      if (!ids && w.order.length) {
        const rank = new Map(w.order.map((id, i) => [id, i]));
        list = [...list].sort(
          (a, b) =>
            Math.min(...soundKeys(a).map((id) => rank.get(id) ?? 1e9)) -
            Math.min(...soundKeys(b).map((id) => rank.get(id) ?? 1e9)),
        );
      }
    }
    if (w && w.sort !== 'original') {
      list = [...list].sort((a, b) => {
        let value = 0;
        if (w.sort === 'name') value = a.name.localeCompare(b.name);
        else if (w.sort === 'duration') value = (a.durationMs ?? -1) - (b.durationMs ?? -1);
        else if (w.sort === 'native') value = (a.nativePlays ?? -1) - (b.nativePlays ?? -1);
        else if (w.sort === 'web') value = (totals[a.id]?.total ?? 0) - (totals[b.id]?.total ?? 0);
        else if (w.sort === 'last')
          value = (totals[a.id]?.lastPlayedAt ?? 0) - (totals[b.id]?.lastPlayedAt ?? 0);
        return w.descending ? -value : value;
      });
    }
    return [...new Map(list.map((s) => [s.id, s])).values()];
  }, [state.library, category, search, w, totals]);
  const title = search
    ? 'Search results'
    : w?.layout === 'mine'
      ? myCategory === 'all'
        ? 'My sounds'
        : myCategory === 'favorites'
          ? 'Favorites'
          : (w.folders.find((f) => f.id === myCategory)?.name ?? 'My sounds')
      : category === 'all'
        ? 'All sounds'
        : (categories.find((c) => c.category.id === category)?.category.name ?? 'All sounds');
  useEffect(() => {
    if ((state.protocol ?? 1) < 2 || !connected) return;
    let live = true;
    void controller
      .run('statistics')
      .then((raw) => {
        const parsed = z
          .object({
            totals: z.record(
              z.string(),
              z.object({ total: z.number(), lastPlayedAt: z.number().nullable() }),
            ),
          })
          .parse(raw);
        if (live) setTotals(parsed.totals);
      })
      .catch(() => {});
    return () => {
      live = false;
    };
  }, [state.statisticsVersion, state.protocol, connected]);
  useEffect(() => {
    controller.start();
  }, []);
  useEffect(() => {
    function key(e: KeyboardEvent) {
      if (
        e.key === '/' &&
        !settings &&
        !editing &&
        !(
          e.target instanceof HTMLElement &&
          ['INPUT', 'TEXTAREA', 'SELECT'].includes(e.target.tagName)
        )
      ) {
        e.preventDefault();
        document.querySelector<HTMLInputElement>('input[aria-label="Search sounds"]')?.focus();
      }
    }
    document.addEventListener('keydown', key);
    return () => document.removeEventListener('keydown', key);
  }, [settings, editing, managing, organizing, statistics]);
  useEffect(() => {
    if (settings || editing || managing || organizing || statistics) {
      const el = document.querySelector<HTMLButtonElement>('.dialog button');
      el?.focus();
    }
    function key(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        setSettings(false);
        setEditing(null);
        setManaging(false);
        setOrganizing(null);
        setStatistics(null);
      }
    }
    document.addEventListener('keydown', key);
    return () => document.removeEventListener('keydown', key);
  }, [settings, editing, managing, organizing, statistics]);
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
  const modal = (label: string, body: React.ReactNode, close: () => void) => (
    <div className="scrim" onClick={close}>
      <section
        role="dialog"
        aria-modal="true"
        aria-label={label}
        className="dialog"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === 'Tab') {
            const nodes = e.currentTarget.querySelectorAll<HTMLElement>(
              'button:not(:disabled),input:not(:disabled),a[href]',
            );
            const first = nodes[0],
              last = nodes[nodes.length - 1];
            if (e.shiftKey && document.activeElement === first) {
              e.preventDefault();
              last?.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
              e.preventDefault();
              first?.focus();
            }
          }
        }}
      >
        <div className="dialog-title">
          <h2>{label}</h2>
          <button className="icon-button" aria-label="Close dialog" onClick={close}>
            <X size={19} />
          </button>
        </div>
        {body}
      </section>
    </div>
  );
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
          <span className="release-version">V2</span>
        </a>
        <span className="edition">BROWSER REMOTE</span>
        <div className="top-actions">
          <span className={`status ${connected && state.available ? 'online' : ''}`} title={status}>
            <i />
            {mode === 'landing'
              ? 'Not connected'
              : connected
                ? host
                  ? state.available
                    ? 'Hosting'
                    : 'Soundpad unavailable'
                  : state.available
                    ? 'Connected'
                    : 'Soundpad unavailable'
                : status
                    .split('.')[0]
                    .replace('Companion disconnected', 'Companion disconnected')
                    .replace('Host unavailable', 'Host unavailable')}
          </span>
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
          {status === 'Connection failed' && (
            <p role="alert" className="warning">
              {view.notice}
            </p>
          )}
          <div className="privacy">
            <ShieldCheck size={16} /> Audio stays on the host. Only commands travel between
            browsers.
          </div>
        </main>
      ) : (
        <>
          <div className="workspace">
            <aside className="sidebar">
              {(state.protocol ?? 1) >= 2 && <SpotifyPanel host={host} />}
              <div className="sidebar-heading">
                LIBRARY <span>{state.library.sounds.filter((s) => !s.internalVariant).length}</span>
              </div>
              <button
                className={`category ${category === 'all' ? 'selected' : ''}`}
                onClick={() => {
                  setCategory('all');
                  personal.update((current) => ({ ...current, layout: 'host' }));
                  setSearch('');
                }}
              >
                <AudioLines size={18} />
                <span>All sounds</span>
                <small>{state.library.sounds.filter((s) => !s.internalVariant).length}</small>
              </button>
              <div className="sidebar-label">SOUNDPAD CATEGORIES</div>
              <nav aria-label="Sound categories">
                {categories.map(({ category: c, depth }) => (
                  <button
                    key={c.id}
                    style={{ paddingLeft: 14 + depth * 16 }}
                    className={`category ${category === c.id && !search ? 'selected' : ''}`}
                    onClick={() => {
                      setCategory(c.id);
                      personal.update((current) => ({ ...current, layout: 'host' }));
                      setSearch('');
                    }}
                  >
                    {c.icon ? <img src={c.icon} alt="" /> : <Folder size={17} />}
                    <span>{c.name || 'Unnamed category'}</span>
                    <small>{c.soundIds.length}</small>
                  </button>
                ))}
              </nav>
              {categories.length === 0 && (
                <p className="sidebar-empty">Your Soundpad categories will appear here.</p>
              )}
              {w && (
                <>
                  <div className="sidebar-label">MY WORKSPACE</div>
                  <button
                    className={`category ${w.layout === 'mine' && myCategory === 'all' ? 'selected' : ''}`}
                    onClick={() => {
                      personal.update((current) => ({ ...current, layout: 'mine' }));
                      setMyCategory('all');
                      setSearch('');
                    }}
                  >
                    <Folder size={17} />
                    <span>My sounds</span>
                  </button>
                  <button
                    className={`category ${w.layout === 'mine' && myCategory === 'favorites' ? 'selected' : ''}`}
                    onClick={() => {
                      personal.update((current) => ({ ...current, layout: 'mine' }));
                      setMyCategory('favorites');
                      setSearch('');
                    }}
                  >
                    <Music2 size={17} />
                    <span>Favorites</span>
                    <small>{w.favorites.length}</small>
                  </button>
                  {w.folders.map((f) => {
                    let depth = 0,
                      parent = f.parent;
                    while (parent && depth < 16) {
                      depth++;
                      parent = w.folders.find((x) => x.id === parent)?.parent ?? null;
                    }
                    return (
                      <button
                        key={f.id}
                        className={`category ${w.layout === 'mine' && myCategory === f.id ? 'selected' : ''}`}
                        style={{ paddingLeft: 14 + depth * 16 }}
                        onDragOver={(e) => e.preventDefault()}
                        onDrop={(e) => {
                          e.preventDefault();
                          const id = e.dataTransfer.getData('application/x-soundpad-id');
                          if (state.library.sounds.some((s) => s.id === id))
                            personal.update((current) => moveSound(current, id, f.id));
                        }}
                        onClick={() => {
                          personal.update((current) => ({ ...current, layout: 'mine' }));
                          setMyCategory(f.id);
                          setSearch('');
                        }}
                      >
                        <Folder size={17} />
                        <span>{f.name}</span>
                        <small>{f.soundIds.length}</small>
                      </button>
                    );
                  })}
                  <button className="category" onClick={() => setManaging(true)}>
                    Manage personal layout
                  </button>
                </>
              )}
              <div className="sidebar-bottom">
                <span>
                  <ShieldCheck size={15} /> Private session
                </span>
                <p>{host ? 'Keep this tab open to host.' : 'Connected directly to your host.'}</p>
              </div>
            </aside>
            <main className="soundboard">
              {connected && ((state.protocol ?? 1) < 2 || needsCompanionUpdate) && (
                <aside className="companion-update" role="status">
                  <strong>
                    {host
                      ? (state.protocol ?? 1) < 2
                        ? 'Update your Windows companion for V2'
                        : 'Update your Windows companion to 2.3'
                      : 'Ask the host to update the Windows companion'}
                  </strong>
                  <p>
                    Basic playback still works. Install the latest companion for session audio,
                    reverse playback, shared waveforms and guest Spotify.
                  </p>
                  {host && (
                    <a
                      href={`${repositoryUrl || 'https://github.com/uh-oh-man/soundpad-web'}/releases/latest/download/SoundpadWeb.exe`}
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
                      ? `${sounds.length} matching sounds`
                      : `${sounds.length} sounds · ${host ? 'Ready when your friends are' : 'Tap a sound to play on the host'}`}
                  </p>
                </div>
                <div className="board-actions">
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
                  (mode !== 'host' && state.permissions.guestUploads === false)
                }
              />
              {!needsCompanionUpdate && (
                <TempManager state={state} host={mode === 'host'} connected={connected} />
              )}
              <div className="toolbar">
                <label className="search">
                  <Search size={18} />
                  <input
                    aria-label="Search sounds"
                    value={search}
                    onChange={(e) => setSearch(e.target.value)}
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
                      if (e.target.value.startsWith('personal:')) {
                        personal.update((current) => ({ ...current, layout: 'mine' }));
                        setMyCategory(e.target.value.slice(9));
                      } else {
                        personal.update((current) => ({ ...current, layout: 'host' }));
                        setCategory(e.target.value);
                      }
                      setSearch('');
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
                        {'　'.repeat(depth) + c.name}
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
                <div className="layout-switch" aria-label="Layout">
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
                        view: e.target.value as typeof current.view,
                      }))
                    }
                  >
                    {viewModes.map((mode) => (
                      <option key={mode} value={mode}>
                        {
                          {
                            icons: 'Icons',
                            large: 'Large Icons',
                            list: 'List',
                            details: 'Details',
                            waveforms: 'Waveforms',
                          }[mode]
                        }
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
                        sort: e.target.value as typeof current.sort,
                      }))
                    }
                  >
                    {[
                      ['original', 'Original order'],
                      ['name', 'Name'],
                      ['duration', 'Duration'],
                      ['native', 'Soundpad plays'],
                      ['web', 'Web plays'],
                      ['last', 'Last web played'],
                    ].map(([value, label]) => (
                      <option key={value} value={value}>
                        {label}
                      </option>
                    ))}
                  </select>
                </label>
                <button
                  disabled={!w || w.sort === 'original'}
                  aria-label="Toggle sort direction"
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
      {settings &&
        modal(
          'Session & access',
          <>
            <section className="appearance-setting" aria-label="Appearance">
              <h3>Appearance</h3>
              <label>
                Theme
                <select
                  aria-label="Theme"
                  value={appearance.theme}
                  onChange={(event) => appearance.setTheme(event.target.value as Theme)}
                >
                  {themes.map((theme) => (
                    <option key={theme.id} value={theme.id}>
                      {theme.name}
                    </option>
                  ))}
                </select>
              </label>
              <p>Only your browser's appearance changes.</p>
              {appearance.error && <p className="inline-error">{appearance.error}</p>}
            </section>
            <div className="settings-status">
              <Radio size={18} />
              <div>
                <strong>{status}</strong>
                <p>
                  {host
                    ? 'The companion controls Soundpad. This tab connects your guests.'
                    : 'Your host controls playback and permissions.'}
                </p>
              </div>
            </div>
            {host && (
              <>
                <div className="setting-row">
                  <div>
                    <strong>Remote playback</strong>
                    <p>Guests can play and stop sounds.</p>
                  </div>
                  <input
                    type="checkbox"
                    aria-label="Allow remote playback"
                    checked={state.permissions.remotePlayback}
                    onChange={(e) =>
                      void run('permissions', {
                        permissions: { ...state.permissions, remotePlayback: e.target.checked },
                      })
                    }
                  />
                </div>
                <div className="setting-row">
                  <div>
                    <strong>Guest Kick counter</strong>
                    <p>Allow guests to add or subtract kicks.</p>
                  </div>
                  <input
                    type="checkbox"
                    aria-label="Allow guest counter changes"
                    checked={state.permissions.guestCounter}
                    onChange={(e) =>
                      void run('permissions', {
                        permissions: { ...state.permissions, guestCounter: e.target.checked },
                      })
                    }
                  />
                </div>
                {(state.protocol ?? 1) >= 2 && (
                  <>
                    <div className="setting-row">
                      <div>
                        <strong>Guest Spotify controls</strong>
                        <p>Guests can control the host's enabled Spotify player.</p>
                      </div>
                      <input
                        type="checkbox"
                        aria-label="Allow guest Spotify controls"
                        checked={state.permissions.guestSpotify !== false}
                        onChange={(e) =>
                          void run('permissions', {
                            permissions: { ...state.permissions, guestSpotify: e.target.checked },
                          })
                        }
                      />
                    </div>
                    <div className="setting-row">
                      <div>
                        <strong>Guest seeking & pause</strong>
                        <p>Allow waveform seeking, cues and regions.</p>
                      </div>
                      <input
                        type="checkbox"
                        aria-label="Allow guest seeking"
                        checked={state.permissions.guestSeeking !== false}
                        onChange={(e) =>
                          void run('permissions', {
                            permissions: { ...state.permissions, guestSeeking: e.target.checked },
                          })
                        }
                      />
                    </div>
                  </>
                )}
                {(['guestUploads', 'guestTransforms'] as const).map((key) => (
                  <div className="setting-row" key={key}>
                    <div>
                      <strong>
                        {key === 'guestUploads'
                          ? 'Guest session uploads'
                          : 'Guest reverse generation'}
                      </strong>
                      <p>
                        {key === 'guestUploads'
                          ? 'Allow bounded audio uploads into Temp.'
                          : 'Allow guests to prepare reversed variants.'}
                      </p>
                    </div>
                    <input
                      type="checkbox"
                      aria-label={
                        key === 'guestUploads'
                          ? 'Allow guest uploads'
                          : 'Allow guest reverse generation'
                      }
                      checked={state.permissions[key] !== false}
                      onChange={(e) =>
                        void run('permissions', {
                          permissions: { ...state.permissions, [key]: e.target.checked },
                        })
                      }
                    />
                  </div>
                ))}
                <div className="invite-field">
                  <label htmlFor="invite">Invitation link · anyone with this link can join</label>
                  <input
                    id="invite"
                    value={view.invite}
                    readOnly
                    onFocus={(e) => e.target.select()}
                  />
                  <button onClick={() => void controller.copyInvite()}>
                    <Link size={15} /> Copy Invite
                  </button>
                  <button
                    onClick={() => {
                      if (confirm('Revoke the current invite and disconnect all guests?'))
                        void controller.rotate();
                    }}
                  >
                    <RotateCcw size={15} /> Replace invite
                  </button>
                </div>
                <p className="muted">
                  Disconnect removes this connection only. Replace invite revokes the bearer link; a
                  disconnected guest can otherwise rejoin.
                </p>
                <h3>
                  Connected guests <small>{view.guests.length} / 12</small>
                </h3>
                {view.guests.length ? (
                  view.guests.map((id, i) => (
                    <div className="guest-row" key={id}>
                      <span>
                        <Users size={17} /> {view.people[id]?.name ?? `Guest ${i + 1}`}{' '}
                        <small>{id.slice(0, 8)}</small>
                      </span>
                      <button onClick={() => controller.disconnect(id)}>Disconnect</button>
                    </div>
                  ))
                ) : (
                  <p className="muted">Share your invitation to bring friends in.</p>
                )}
                <button
                  className="danger-link"
                  disabled={!connected}
                  onClick={() => {
                    if (confirm('Reset the Kick counter to zero?')) void run('reset');
                  }}
                >
                  <Trash2 size={15} /> Reset Kick counter
                </button>
              </>
            )}
            {host && (state.protocol ?? 1) >= 2 && (
              <>
                <button
                  className="danger-link"
                  onClick={() => {
                    if (confirm('Clear web play statistics? The Kick counter is preserved.'))
                      void run('clearStatistics');
                  }}
                >
                  Clear web statistics
                </button>
              </>
            )}
            <div className="dialog-foot">
              <span>Keep the host tab and companion open. Closing either ends guest control.</span>
              <button
                onClick={() => {
                  setSettings(false);
                  void controller.leave();
                }}
              >
                <LogOut size={16} /> {host ? 'End hosting' : 'Leave session'}
              </button>
            </div>
          </>,
          () => setSettings(false),
        )}
      {mode === 'landing' && <ProjectFooter />}
      {view.needsName && <GuestName initial={view.profile.name} />}
      {editing &&
        modal('Sound icon', <IconEditor sound={editing} close={() => setEditing(null)} />, () =>
          setEditing(null),
        )}
      {statistics &&
        modal(
          'Sound statistics',
          <Statistics sound={statistics} version={state.statisticsVersion ?? 0} />,
          () => setStatistics(null),
        )}
      {managing &&
        w &&
        modal(
          'My layout',
          <PersonalLayout
            workspace={w}
            update={personal.update}
            replace={personal.replace}
            sounds={state.library.sounds}
            status={personal.status}
          />,
          () => setManaging(false),
        )}
      {organizing &&
        w &&
        modal(
          'Organize sound',
          <OrganizeSound sound={organizing} workspace={w} update={personal.update} />,
          () => setOrganizing(null),
        )}
    </div>
  );
}
createRoot(document.getElementById('root')!).render(<App />);
