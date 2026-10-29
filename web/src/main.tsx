import React, { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { createRoot } from 'react-dom/client';
import {
  AudioLines,
  Search,
  Square,
  Link,
  Settings2,
  Folder,
  Music2,
  Plus,
  Minus,
  RefreshCw,
  Users,
  X,
  ImagePlus,
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
import { flattenCategories, selectSounds, type Sound } from './protocol';
import './styles.css';

function App() {
  const view = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const { state, mode, status } = view;
  const [category, setCategory] = useState('all');
  const [search, setSearch] = useState('');
  const [settings, setSettings] = useState(false);
  const [editing, setEditing] = useState<Sound | null>(null);
  const [limit, setLimit] = useState(120);
  const [pressed, setPressed] = useState('');
  const [pending, setPending] = useState(0);
  const host = mode === 'host';
  const connected = status === (host ? 'Host ready' : 'Connected');
  const canPlay = connected && state.available && (host || state.permissions.remotePlayback);
  const canCount = connected && (host || state.permissions.guestCounter);
  const repositoryUrl = import.meta.env.VITE_REPOSITORY_URL as string | undefined;
  const categories = useMemo(
    () => flattenCategories(state.library.categories),
    [state.library.categories],
  );
  const sounds = useMemo(() => selectSounds(state, category, search), [state, category, search]);
  const title = search
    ? 'Search results'
    : category === 'all'
      ? 'All sounds'
      : categories.find((c) => c.category.id === category)?.category.name || 'All sounds';
  useEffect(() => {
    controller.start();
  }, []);
  useEffect(() => {
    setLimit(120);
  }, [category, search]);
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
  }, [settings, editing]);
  useEffect(() => {
    if (settings || editing) {
      const el = document.querySelector<HTMLButtonElement>('.dialog button');
      el?.focus();
    }
    function key(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        setSettings(false);
        setEditing(null);
      }
    }
    document.addEventListener('keydown', key);
    return () => document.removeEventListener('keydown', key);
  }, [settings, editing]);
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
  async function play(sound: Sound) {
    setPressed(sound.id);
    await run('play', { soundId: sound.id, libraryVersion: state.library.version });
    setTimeout(() => setPressed(''), 250);
  }
  async function icon(file: File) {
    try {
      if (!['image/png', 'image/jpeg'].includes(file.type) || file.size > 131072)
        throw Error('Choose a PNG or JPEG under 128 KiB.');
      const bitmap = await createImageBitmap(file);
      if (bitmap.width > 512 || bitmap.height > 512) {
        bitmap.close();
        throw Error('Use an image up to 512 × 512 pixels.');
      }
      const canvas = document.createElement('canvas');
      canvas.width = 96;
      canvas.height = 96;
      canvas.getContext('2d')!.drawImage(bitmap, 0, 0, 96, 96);
      bitmap.close();
      await run('icon', { soundId: editing!.id, image: canvas.toDataURL('image/png') });
      setEditing(null);
    } catch (e) {
      controller.notify(e instanceof Error ? e.message : 'Could not read image.');
    }
  }
  const time = (ms: number | null) =>
    ms == null
      ? '—'
      : `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}`;
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
    <div className="app-shell">
      <header className="topbar">
        <a href={location.pathname} className="brand" aria-label="Soundpad Web home">
          <span className="brand-icon">
            <AudioLines size={23} />
          </span>
          <span>
            soundpad<span className="brand-light"> web</span>
          </span>
        </a>
        <span className="edition">BROWSER REMOTE</span>
        <div className="top-actions">
          <span className={`status ${connected ? 'online' : ''}`} title={status}>
            <i />
            {mode === 'landing'
              ? 'Not connected'
              : connected
                ? host
                  ? 'Hosting'
                  : 'Connected'
                : 'Connecting'}
          </span>
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
            accounts, no audio uploads.
          </p>
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
              <div className="sidebar-heading">
                LIBRARY <span>{state.library.sounds.length}</span>
              </div>
              <button
                className={`category ${category === 'all' ? 'selected' : ''}`}
                onClick={() => {
                  setCategory('all');
                  setSearch('');
                }}
              >
                <AudioLines size={18} />
                <span>All sounds</span>
                <small>{state.library.sounds.length}</small>
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
              <div className="sidebar-bottom">
                <span>
                  <ShieldCheck size={15} /> Private session
                </span>
                <p>{host ? 'Keep this tab open to host.' : 'Connected directly to your host.'}</p>
              </div>
            </aside>
            <main className="soundboard">
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
                        onClick={() => void run('refresh')}
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
                    value={category}
                    onChange={(e) => {
                      setCategory(e.target.value);
                      setSearch('');
                    }}
                  >
                    <option value="all">All sounds</option>
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
              {sounds.length ? (
                <>
                  <div className="sound-grid">
                    {sounds.slice(0, limit).map((sound, i) => (
                      <div
                        className={`sound-tile ${pressed === sound.id ? 'pressed' : ''}`}
                        key={sound.id}
                      >
                        <button
                          className="play-sound"
                          disabled={!canPlay}
                          aria-label={`Play ${sound.name}`}
                          onClick={() => void play(sound)}
                        >
                          <span className={`sound-icon tone-${i % 5}`}>
                            {sound.icon ? <img src={sound.icon} alt="" /> : <Music2 size={22} />}
                          </span>
                          <span className="sound-name">{sound.name}</span>
                          <span className="sound-hint">
                            {pressed === sound.id ? 'SENDING…' : 'PLAY SOUND'}
                          </span>
                        </button>
                        {host && (
                          <button
                            className="edit-icon"
                            aria-label={`Edit icon for ${sound.name}`}
                            onClick={() => setEditing(sound)}
                          >
                            <ImagePlus size={14} />
                          </button>
                        )}
                      </div>
                    ))}
                  </div>
                  {sounds.length > limit && (
                    <button className="load-more" onClick={() => setLimit((l) => l + 120)}>
                      Show more sounds ({sounds.length - limit} remaining)
                    </button>
                  )}
                </>
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
              </div>
            </main>
          </div>
          <footer className="player">
            <div className="playback">
              <span
                className={`playback-icon ${state.playback.status === 'PLAYING' ? 'playing' : ''}`}
              >
                <AudioLines size={20} />
              </span>
              <div>
                <strong>
                  {state.playback.status === 'PLAYING'
                    ? 'Soundpad is playing'
                    : state.playback.status === 'PAUSED'
                      ? 'Playback paused'
                      : state.playback.status === 'STOPPED'
                        ? 'Ready to make some noise'
                        : 'Playback status unavailable'}
                </strong>
                <small>
                  {state.playback.durationMs
                    ? `${time(state.playback.positionMs)} / ${time(state.playback.durationMs)}`
                    : 'Playback follows your existing Soundpad mode'}
                </small>
                {state.playback.durationMs != null && state.playback.positionMs != null && (
                  <progress
                    aria-label="Playback progress"
                    max={state.playback.durationMs}
                    value={state.playback.positionMs}
                  />
                )}
              </div>
            </div>
            <button className="stop" disabled={!canPlay} onClick={() => void run('stop')}>
              <Square size={15} fill="currentColor" /> Stop
            </button>
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
          </footer>
        </>
      )}
      {view.notice && (
        <div className="toast" role="status">
          <span>{view.notice}</span>
          <button
            className="icon-button"
            aria-label="Dismiss notification"
            onClick={() => controller.notify('')}
          >
            <X size={17} />
          </button>
        </div>
      )}
      {settings &&
        modal(
          'Session & access',
          <>
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
                <h3>
                  Connected guests <small>{view.guests.length} / 12</small>
                </h3>
                {view.guests.length ? (
                  view.guests.map((id, i) => (
                    <div className="guest-row" key={id}>
                      <span>
                        <Users size={17} /> Guest {i + 1} <small>{id.slice(0, 8)}</small>
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
      {editing &&
        modal(
          'Sound icon',
          <>
            <p className="muted">{editing.name}</p>
            <div className="icon-preview">
              {editing.icon ? <img src={editing.icon} alt="Current icon" /> : <Music2 size={40} />}
            </div>
            <label className="upload primary">
              <ImagePlus size={17} /> Choose PNG or JPEG
              <input
                type="file"
                accept="image/png,image/jpeg"
                aria-label="Choose sound icon"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  if (file) void icon(file);
                }}
              />
            </label>
            <p className="muted">
              Up to 512 × 512 pixels and 128 KiB. Stored on your PC and shared with connected
              guests.
            </p>
            <button
              disabled={!editing.icon}
              onClick={() => {
                void run('icon', { soundId: editing.id, image: '' });
                setEditing(null);
              }}
            >
              <Trash2 size={16} /> Remove custom icon
            </button>
          </>,
          () => setEditing(null),
        )}
    </div>
  );
}
createRoot(document.getElementById('root')!).render(<App />);
