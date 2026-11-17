import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import { Asterisk, CircleDot, Music2, Play, Quote, Sparkle, Square, Volume2 } from 'lucide-react';
import { pulse } from './pulse';

/**
 * The start page's pretend soundboard (homepage, after the owner's concept): a small app window with
 * six pads, categories, and a Now playing bar with a waveform and Stop. A press plays a short
 * synthesised blip in this browser, presses the pad down with a ripple, and sends a pulse to the
 * header ribbon that flows out of the Now playing bar. It sends nothing anywhere and loads no files;
 * the real example session is "Mess with the demo".
 */

type Filter = 'all' | 'memes' | 'game' | 'temp';

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Everything' },
  { id: 'memes', label: 'Memes' },
  { id: 'game', label: 'Game clips' },
  { id: 'temp', label: 'Temp' },
];

const PADS = [
  { id: 'vine', label: 'Vine boom', filter: 'memes', tone: 110, time: '0:02', Icon: CircleDot },
  { id: 'bruh', label: 'Bruh', filter: 'memes', tone: 180, time: '0:01', Icon: Quote },
  { id: 'victory', label: 'Victory', filter: 'game', tone: 520, time: '0:06', Icon: Sparkle },
  { id: 'airhorn', label: 'Airhorn', filter: 'memes', tone: 260, time: '0:03', Icon: Volume2 },
  { id: 'wow', label: 'Wow', filter: 'temp', tone: 360, time: '0:02', Icon: Music2 },
  { id: 'applause', label: 'Applause', filter: 'game', tone: 420, time: '0:04', Icon: Asterisk },
] as const;
type Pad = (typeof PADS)[number];

/** The waveform's bars: fixed heights (percent) and phase offsets, so it looks the same every visit. */
const BARS = Array.from({ length: 44 }, (_, i) => ({
  h: Math.round(15 + Math.abs(Math.sin(i * 0.73) * Math.cos(i * 0.19)) * 82),
  delay: (i % 13) / 13,
}));

/** How long a pad stays lit after a press; the blip itself lasts 0.3 s. */
const LIT_MS = 900;

type AudioWindow = Window & { webkitAudioContext?: typeof AudioContext };

export function HomeBoard() {
  const [filter, setFilter] = useState<Filter>('all');
  const [playing, setPlaying] = useState<{ key: number; pad: Pad } | null>(null);
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
    setPlaying({ key: ++presses.current, pad });
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
      // No Web Audio here: the pad still lights up and the bar still reacts.
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
      className={`home-board ${playing ? 'is-live' : ''}`}
      aria-labelledby="home-board-title"
      onKeyDown={keys}
    >
      <div className="home-board-top">
        <span className="home-board-dots" aria-hidden="true">
          <b />
          <b />
          <b />
        </span>
        <p className="home-board-crumb">
          <strong>Soundboard</strong> <span>/ example session</span>
        </p>
        <span className="home-board-badge">
          <i aria-hidden="true" /> DEMO
        </span>
      </div>
      <div className="home-board-main">
        <div className="home-board-info">
          <div>
            <h2 id="home-board-title">Go on. Press something.</h2>
            <p>A tiny pretend soundboard. The real one uses your Soundpad library.</p>
          </div>
          <span className="home-board-count">{String(shown.length).padStart(2, '0')} SOUNDS</span>
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
        <div className="home-pads">
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
                onClick={() => press(pad)}
              >
                <span className="home-pad-key" aria-hidden="true">
                  {String(n).padStart(2, '0')}
                </span>
                <span className="home-pad-icon" aria-hidden="true">
                  <pad.Icon size={16} strokeWidth={2.4} />
                </span>
                <span className="home-pad-title">{pad.label}</span>
                <span className="home-pad-time">{pad.time}</span>
                {lit && <span key={playing?.key} className="home-pad-ripple" aria-hidden="true" />}
              </button>
            );
          })}
        </div>
      </div>

      <div className="home-now-bar" id="home-now-bar">
        <span className="home-now-icon" aria-hidden="true">
          {current ? (
            <Square size={13} fill="currentColor" />
          ) : (
            <Play size={15} fill="currentColor" />
          )}
        </span>
        <p className="home-now-label">
          <span>NOW PLAYING</span>
          <strong className="home-now" aria-live="polite">
            {current ? current.label : 'Nothing yet. Go ahead.'}
          </strong>
        </p>
        <span className="home-wave" aria-hidden="true">
          {BARS.map((bar, i) => (
            <i
              key={i}
              style={{ '--h': `${bar.h}%`, '--delay': `${-bar.delay}s` } as CSSProperties}
            />
          ))}
        </span>
        <button type="button" className="home-stop" onClick={stop}>
          <Square size={10} fill="currentColor" aria-hidden="true" /> STOP
        </button>
      </div>
    </section>
  );
}
