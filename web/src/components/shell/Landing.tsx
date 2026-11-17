import {
  ArchiveRestore,
  ArrowUpRight,
  AudioLines,
  Library,
  Link2,
  Monitor,
  MousePointerClick,
  Play,
  ShieldCheck,
  Sparkles,
  Wand2,
  type LucideIcon,
} from 'lucide-react';
import type { ReactNode } from 'react';
import { controller } from '../../controller';
import { APP_VERSION_LABEL } from '../../version';
import { HomeBoard } from '../home/HomeBoard';
import { demoLoading, startDemo } from '../home/HomeBar';

/** The project's home, linked from the footer and the update notes. */
export const PROJECT_URL = 'https://github.com/uh-oh-man/soundpad-web';
/** Where hosts get the companion: the latest release's page, with its notes and the EXE. */
const RELEASE_URL = `${PROJECT_URL}/releases/latest`;
const CHANGELOG_URL = `${PROJECT_URL}/blob/main/CHANGELOG.md`;
const SOUND_CREDITS_URL = `${PROJECT_URL}/blob/main/web/public/demo/CREDITS.md`;

/** The start page and footer (Stage G P0.6: moved out of main.tsx unchanged; WP-C owns them). */
export function ProjectFooter() {
  return (
    <footer className="project-footer">
      Soundpad Web · v{APP_VERSION_LABEL} ·{' '}
      <a href={PROJECT_URL} target="_blank" rel="noreferrer">
        GitHub
      </a>{' '}
      · <span>made by AI</span>
    </footer>
  );
}

/** A link that opens in a new tab and says so to screen readers. */
function Out({
  href,
  className,
  children,
}: {
  href: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <a href={href} className={className} target="_blank" rel="noopener noreferrer">
      {children}
      <ArrowUpRight size={15} aria-hidden="true" />
      <span className="sr-only"> (opens in a new tab)</span>
    </a>
  );
}

const STEPS: { title: string; body: ReactNode; Icon: LucideIcon }[] = [
  {
    title: 'Run the companion on your PC.',
    Icon: Monitor,
    body: (
      <>
        Open Soundpad, then start <b>SoundpadWeb.exe</b>. It’s one portable file for Windows 11: no
        installer, no admin rights. It opens its own small window and hosts from there.
      </>
    ),
  },
  {
    title: 'Send one link.',
    Icon: Link2,
    body: (
      <>
        Press <b>Copy Invite</b> and drop it in the group chat. Treat it like a password: whoever
        has it can join. You decide what guests may do, and you can replace the link any time.
      </>
    ),
  },
  {
    title: 'They click. Your PC plays it.',
    Icon: MousePointerClick,
    body: (
      <>
        Friends open the link in a browser, on a computer or a phone, pick a name and start
        pressing. Your Soundpad plays the sound, just as if you had pressed it yourself.
      </>
    ),
  },
];

const FEATURES: { name: string; title: string; body: string; Icon: LucideIcon }[] = [
  {
    name: 'Mimic',
    title: 'Grab what was just said.',
    Icon: AudioLines,
    body: 'Someone says something legendary in Discord or a game. Mimic keeps a rolling recording of the app you pick, on your PC, with word-by-word subtitles. Select the moment and play it straight back. It never records a microphone, and everyone sees when it’s on.',
  },
  {
    name: 'Voice library',
    title: 'Every keeper, searchable by what was said.',
    Icon: Library,
    body: 'Clips you keep land in Voices with their words. Search for a word or a phrase, tag who said it, star the classics, and click a single word to hear just that word.',
  },
  {
    name: 'Sentences',
    title: 'Make them say it. Sort of.',
    Icon: Wand2,
    body: 'Type a sentence and it gets built from words people really said in your recordings. Nothing is synthesised or cloned: a word nobody said stays missing, and plays as a gap or a bleep. Your choice.',
  },
  {
    name: 'Organize',
    title: 'Tidy up without the chaos.',
    Icon: Sparkles,
    body: 'Tick sounds, drag them onto a category, then review the plan. Soundpad Web makes the changes Soundpad allows, checks every one, and says plainly which steps are yours to do in Soundpad.',
  },
  {
    name: 'Backups and updates',
    title: 'Safe to mess with.',
    Icon: ArchiveRestore,
    body: 'Daily backups to a folder you choose, with restores down to a single clip. New versions arrive inside the app, checked against a published hash, and your session carries on afterwards.',
  },
  {
    name: 'Private by default',
    title: 'No accounts. No cloud. A Panic button.',
    Icon: ShieldCheck,
    body: 'No sign-ups, no hosted backend, no cloud database and nothing for guests to install. Up to 12 guests, with the permissions you pick. Panic stops the sound and pauses guests in one press.',
  },
];

const PROMISES = [
  'No accounts. No guest downloads.',
  'Private sessions, hosted by you.',
  'Made for playing sounds, not selling them.',
];

export function Landing({ view }: { view: ReturnType<typeof controller.getSnapshot> }) {
  const loading = demoLoading(view);
  return (
    <main className="home" aria-labelledby="home-title">
      <section className="home-hero">
        <div className="home-hero-copy">
          {view.resumeAvailable && (
            <div className="home-resume">
              <p>
                <strong>Hosting is off.</strong> Start it again with a fresh invite.
              </p>
              <button className="home-resume-button" onClick={() => controller.resume()}>
                Resume hosting
              </button>
            </div>
          )}
          <p className="home-eyebrow">The group chat, but louder.</p>
          <h1 id="home-title">
            <span>Give your</span> <span>friends the</span>{' '}
            <span className="home-highlight">
              <span>soundboard.</span>
            </span>
          </h1>
          <p className="home-intro">They click a sound. Your PC plays it.</p>
          <p className="home-lede">
            Share your actual Soundpad library with one link. No installs for friends, no accounts,
            and absolutely no guarantee of good timing.
          </p>
          <div className="home-actions">
            <button
              type="button"
              className="home-cta"
              aria-describedby="try-example-note"
              // Busy stays focusable (aria-disabled); presses are ignored until the example has loaded.
              aria-disabled={loading || undefined}
              aria-busy={loading || undefined}
              onClick={() => startDemo(view)}
            >
              <Play size={17} fill="currentColor" aria-hidden="true" />
              {loading ? 'Loading the demo…' : 'Mess with the demo'}
            </button>
            <Out href={RELEASE_URL} className="home-host">
              Host from your PC
            </Out>
          </div>
          <p id="try-example-note" className="home-note">
            The demo is the real app with example sounds: the soundboard, Mimic, voices and
            sentences. Nothing is sent to Soundpad; it all plays in this browser.
          </p>
          {view.status === 'Connection failed' && (
            <p role="alert" className="home-alert">
              {view.notice}
            </p>
          )}
          <p className="home-safe">
            <ShieldCheck size={15} aria-hidden="true" />
            The audio stays on your computer. Guests just push buttons.
          </p>
        </div>
        <div className="home-hero-demo">
          <div className="home-board-wrap">
            <span className="home-board-slab" aria-hidden="true" />
            <HomeBoard />
            <p className="home-scribble" aria-hidden="true">
              <svg viewBox="0 0 100 56">
                <path d="M6 52C10 26 52 42 88 9" />
                <path d="M73 9 89 7 88 23" />
              </svg>
              ↖ yes, these buttons work
            </p>
          </div>
        </div>
      </section>

      <ul className="home-promises" aria-label="The short version">
        {PROMISES.map((line) => (
          <li key={line}>{line}</li>
        ))}
      </ul>

      <section className="home-section home-how" id="how" tabIndex={-1} aria-labelledby="how-title">
        <p className="home-kicker">How it works</p>
        <h2 id="how-title">Three steps. One of them is sending a link.</h2>
        <ol className="home-steps">
          {STEPS.map(({ title, body, Icon }, i) => (
            <li key={title}>
              <span className="home-step-n" aria-hidden="true">
                {String(i + 1).padStart(2, '0')}
              </span>
              <span className="home-tile-icon" aria-hidden="true">
                <Icon size={18} />
              </span>
              <h3>{title}</h3>
              <p>{body}</p>
            </li>
          ))}
        </ol>
        <p className="home-how-foot">
          <span>Joining a friend? Open their invitation link to connect.</span>
          <Out href={RELEASE_URL}>Hosting? Get the companion</Out>
        </p>
      </section>

      <section className="home-section home-features" aria-labelledby="features-title">
        <p className="home-kicker">What’s in the box</p>
        <h2 id="features-title">More than a wall of buttons.</h2>
        <p className="home-section-lede">
          The board is the easy part. Here’s what the rest of it does, explained by the AI that
          built it, so blame us for the jokes.
        </p>
        <ul className="home-features-grid">
          {FEATURES.map(({ name, title, body, Icon }) => (
            <li key={name}>
              <span className="home-tile-icon" aria-hidden="true">
                <Icon size={18} />
              </span>
              <p className="home-feature-name">{name}</p>
              <h3>{title}</h3>
              <p>{body}</p>
            </li>
          ))}
        </ul>
      </section>

      <section className="home-section home-credits" aria-labelledby="credits-title">
        <div className="home-credits-copy">
          <p className="home-kicker">The credits</p>
          <h2 id="credits-title">Built with AI. Credited like it.</h2>
          <p>
            Soundpad Web is made by AI. The app, its docs and this page were written by AI models,
            directed by one person who really wanted a better soundboard. Every change is written
            down, and every release stays downloadable.
          </p>
          <Out href={CHANGELOG_URL} className="home-text-link">
            Read the changelog
          </Out>
        </div>
        <dl className="home-roll">
          <div>
            <dt>Written by</dt>
            <dd>
              <b>Codex</b> (OpenAI): the first versions, up to 2.3.0, and again from 2.46.
            </dd>
            <dd>
              <b>Claude</b> (Anthropic), in Claude Code: from 2.3.1 through the big overhaul, and
              this page.
            </dd>
          </div>
          <div>
            <dt>Directed by</dt>
            <dd>
              <b>uh-oh-man</b>, who has the ideas and the final say.
            </dd>
          </div>
          <div>
            <dt>Look of this page</dt>
            <dd>Started as an AI-made image the owner liked, then rebuilt here for real.</dd>
          </div>
          <div>
            <dt>Demo sounds</dt>
            <dd>
              Public domain and CC0 recordings,{' '}
              <Out href={SOUND_CREDITS_URL} className="home-text-link">
                each one credited
              </Out>
              . The pads above are simple synthesised blips.
            </dd>
          </div>
        </dl>
      </section>
    </main>
  );
}
