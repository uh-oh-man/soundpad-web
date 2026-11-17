import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import { Asterisk, CircleDot, Music2, Quote, Sparkle, Square, Volume2 } from 'lucide-react';
import { pulse } from './pulse';

/**
 * The start page's pretend soundboard (homepage): six pads on an orbit around a hub, like the app's
 * icon. A press plays a short synthesised blip in this browser, lights the pad, sends a spark into the
 * hub and a ripple through the ribbon, and posts "Sam pressed Airhorn" to the little feed: pick who you
 * are with "Pressing as", because the real thing is several friends pressing one person's soundboard.
 * It sends nothing anywhere and loads no files; the real example session is "Mess with the demo".
 */

type Filter = 'all' | 'memes' | 'game' | 'temp';

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Everything' },
  { id: 'memes', label: 'Memes' },
  { id: 'game', label: 'Game clips' },
  { id: 'temp', label: 'Temp' },
];

/** Where each pad sits on the orbit (percent of the stage): an ellipse around the hub. */
const PADS = [
  {
    id: 'vine',
    label: 'Vine boom',
    filter: 'memes',
    tone: 110,
    time: '0:02',
    Icon: CircleDot,
    x: 31,
    y: 14,
  },
  {
    id: 'bruh',
    label: 'Bruh',
    filter: 'memes',
    tone: 180,
    time: '0:01',
    Icon: Quote,
    x: 69,
    y: 14,
  },
  {
    id: 'victory',
    label: 'Victory',
    filter: 'game',
    tone: 520,
    time: '0:06',
    Icon: Sparkle,
    x: 87,
    y: 50,
  },
  {
    id: 'airhorn',
    label: 'Airhorn',
    filter: 'memes',
    tone: 260,
    time: '0:03',
    Icon: Volume2,
    x: 69,
    y: 86,
  },
  { id: 'wow', label: 'Wow', filter: 'temp', tone: 360, time: '0:02', Icon: Music2, x: 31, y: 86 },
  {
    id: 'applause',
    label: 'Applause',
    filter: 'game',
    tone: 420,
    time: '0:04',
    Icon: Asterisk,
    x: 13,
    y: 50,
  },
] as const;
type Pad = (typeof PADS)[number];

/** The friends you can press as; each has one of the app's voice colours. */
const FRIENDS = [
  { id: 'you', name: 'You', colour: 'var(--brand-cyan)' },
  { id: 'sam', name: 'Sam', colour: 'var(--voice-1)' },
  { id: 'maya', name: 'Maya', colour: 'var(--voice-3)' },
  { id: 'jo', name: 'Jo', colour: 'var(--voice-2)' },
] as const;
type Friend = (typeof FRIENDS)[number];

type Press = { key: number; friend: Friend; pad: Pad };

/** How long a pad stays lit after a press; the blip itself lasts 0.3 s. */
const LIT_MS = 900;
const FEED_SIZE = 3;

type AudioWindow = Window & { webkitAudioContext?: typeof AudioContext };

export function HomeOrbit() {
  const [filter, setFilter] = useState<Filter>('all');
  const [friend, setFriend] = useState<Friend>(FRIENDS[0]);
  const [playing, setPlaying] = useState<Press | null>(null);
  const [feed, setFeed] = useState<Press[]>(() => [
    { key: -1, friend: FRIENDS[2], pad: PADS[2] },
    { key: -2, friend: FRIENDS[3], pad: PADS[1] },
  ]);
  const presses = useRef(0);
  const context = useRef<AudioContext | null>(null);
  const voice = useRef<OscillatorNode | null>(null);
  const timer = useRef<number | undefined>(undefined);

  const silence = () => {
    try {
      voice.current?.stop();
    } catch {
      // Already stopped.
    }
    voice.current = null;
  };

  const stop = () => {
    window.clearTimeout(timer.current);
    silence();
    setPlaying(null);
  };

  const press = (pad: Pad) => {
    window.clearTimeout(timer.current);
    silence();
    const entry = { key: ++presses.current, friend, pad };
    setPlaying(entry);
    setFeed((list) => [entry, ...list].slice(0, FEED_SIZE));
    pulse(0.6 + (pad.tone / 520) * 0.4);
    try {
      const Context = window.AudioContext ?? (window as AudioWindow).webkitAudioContext;
      if (Context) {
        const ctx = (context.current ??= new Context());
        void ctx.resume?.();
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        const now = ctx.currentTime;
        osc.type = 'sine';
        osc.frequency.value = pad.tone;
        // A quick attack and decay at a low level: a blip, never a blast.
        gain.gain.setValueAtTime(0.001, now);
        gain.gain.exponentialRampToValueAtTime(0.1, now + 0.025);
        gain.gain.exponentialRampToValueAtTime(0.001, now + 0.29);
        osc.connect(gain).connect(ctx.destination);
        osc.start(now);
        osc.stop(now + 0.3);
        voice.current = osc;
      }
    } catch {
      // No Web Audio here: the pad still lights up and the hub still reacts.
    }
    timer.current = window.setTimeout(() => {
      voice.current = null;
      setPlaying(null);
    }, LIT_MS);
  };

  useEffect(
    () => () => {
      window.clearTimeout(timer.current);
      void context.current?.close().catch(() => {});
      context.current = null;
    },
    [],
  );

  const shown = PADS.filter((pad) => filter === 'all' || pad.filter === filter);
  const current = playing?.pad;

  // Like Soundpad's hotkeys, but only while the keyboard is in this board: 1 to 6 press a pad.
  const keys = (e: KeyboardEvent<HTMLElement>) => {
    if (e.altKey || e.ctrlKey || e.metaKey || !/^[1-6]$/.test(e.key)) return;
    const pad = PADS[Number(e.key) - 1];
    if (!shown.includes(pad)) return;
    e.preventDefault();
    press(pad);
  };

  return (
    <section
      className={`home-orbit ${playing ? 'is-live' : ''}`}
      aria-labelledby="home-board-title"
      onKeyDown={keys}
      style={{ '--who': friend.colour } as CSSProperties}
    >
      <div className="home-orbit-head">
        <div>
          <h2 id="home-board-title">Go on. Press something.</h2>
          <p>A tiny pretend soundboard. The real one uses your Soundpad library.</p>
        </div>
        <span className="home-orbit-count">{String(shown.length).padStart(2, '0')} SOUNDS</span>
      </div>
      <div className="home-chips" role="group" aria-label="Categories">
        {FILTERS.map((f) => (
          <button
            key={f.id}
            type="button"
            aria-pressed={filter === f.id}
            onClick={() => setFilter(f.id)}
          >
            {f.label}
          </button>
        ))}
      </div>

      {/* The gradients the rings, the hub and How it works share: in an svg that is never display:none,
          because Chrome drops gradients defined inside a hidden one (the phone layout hides the rings). */}
      <svg className="home-defs" width="0" height="0" aria-hidden="true" focusable="false">
        <defs>
          <linearGradient id="home-ring" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="var(--brand-cyan)" />
            <stop offset="1" stopColor="var(--brand-violet)" />
          </linearGradient>
          <linearGradient id="home-bar" x1="0" y1="1" x2="0" y2="0">
            <stop offset="0" stopColor="var(--brand-violet)" />
            <stop offset="1" stopColor="var(--brand-cyan)" />
          </linearGradient>
        </defs>
      </svg>
      <div className="home-stage">
        <svg
          className="home-rings"
          viewBox="0 0 100 100"
          preserveAspectRatio="none"
          aria-hidden="true"
        >
          <ellipse className="home-ring-outer" cx="50" cy="50" rx="48" ry="49" />
          <ellipse className="home-ring" cx="50" cy="50" rx="37" ry="41.5" />
        </svg>
        {PADS.map((pad) => (
          <span
            key={pad.id}
            className="home-slot"
            aria-hidden="true"
            style={{ '--x': `${pad.x}%`, '--y': `${pad.y}%` } as CSSProperties}
          />
        ))}

        {shown.map((pad) => {
          const n = PADS.indexOf(pad) + 1;
          const lit = current?.id === pad.id;
          return (
            <button
              key={pad.id}
              type="button"
              className={`home-pad ${lit ? 'is-playing' : ''}`}
              data-pad={n}
              aria-keyshortcuts={String(n)}
              style={{ '--x': `${pad.x}%`, '--y': `${pad.y}%` } as CSSProperties}
              onClick={() => press(pad)}
            >
              <span className="home-pad-icon" aria-hidden="true">
                <pad.Icon size={16} strokeWidth={2.4} />
              </span>
              <span className="home-pad-text">
                <span className="home-pad-title">{pad.label}</span>
                <span className="home-pad-time">{pad.time}</span>
              </span>
              <span className="home-pad-key" aria-hidden="true">
                {n}
              </span>
              {lit && <span key={playing?.key} className="home-pad-ripple" aria-hidden="true" />}
            </button>
          );
        })}
        <div className="home-hub" id="home-hub">
          {/* The app's icon, live: the orbit's back half, five bars that dance while a sound plays, the front half. */}
          <svg className="home-hub-mark" viewBox="0 0 128 128" aria-hidden="true">
            <path
              className="home-hub-orbit is-back"
              d="M8 64a56 17 0 0 1 112 0"
              transform="rotate(-18 64 64)"
            />
            <rect x="39" y="56" width="8" height="16" rx="4" />
            <rect x="49" y="42" width="8" height="44" rx="4" />
            <rect x="59" y="30" width="8" height="68" rx="4" />
            <rect x="69" y="44" width="8" height="40" rx="4" />
            <rect x="79" y="58" width="8" height="12" rx="4" />
            <path
              className="home-hub-orbit"
              d="M8 64a56 17 0 0 0 112 0"
              transform="rotate(-18 64 64)"
            />
          </svg>
          <p className="home-hub-label">
            <span>NOW PLAYING</span>
            <strong className="home-now" aria-live="polite">
              {current ? current.label : 'Nothing yet. Go ahead.'}
            </strong>
            <span className="home-hub-by">
              {playing ? `pressed by ${playing.friend.name}` : 'on the host’s PC'}
            </span>
          </p>
          <button type="button" className="home-stop" onClick={stop}>
            <Square size={10} fill="currentColor" aria-hidden="true" /> STOP
          </button>
        </div>

        {playing && (
          <span
            key={playing.key}
            className="home-spark"
            aria-hidden="true"
            style={{ '--x': `${playing.pad.x}%`, '--y': `${playing.pad.y}%` } as CSSProperties}
          />
        )}
      </div>

      <div className="home-friends">
        <div className="home-chips is-friends" role="group" aria-label="Pressing as">
          <span className="home-friends-label" aria-hidden="true">
            Pressing as
          </span>
          {FRIENDS.map((f) => (
            <button
              key={f.id}
              type="button"
              aria-pressed={friend.id === f.id}
              style={{ '--dot': f.colour } as CSSProperties}
              onClick={() => setFriend(f)}
            >
              <i aria-hidden="true" />
              {f.name}
            </button>
          ))}
        </div>
        <ol className="home-feed" aria-label="Who pressed what">
          {feed.map((entry) => (
            <li key={entry.key} style={{ '--dot': entry.friend.colour } as CSSProperties}>
              <i aria-hidden="true" />
              <b>{entry.friend.name}</b> pressed {entry.pad.label}
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
