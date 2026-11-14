import { ArrowUpRight, ChevronRight, Headphones, Radio, ShieldCheck, Sparkles } from 'lucide-react';
import { controller } from '../../controller';
import { APP_VERSION_LABEL } from '../../version';

/** The project's home, linked from the footer and the update notes. */
export const PROJECT_URL = 'https://github.com/uh-oh-man/soundpad-web';

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

export function Landing({ view }: { view: ReturnType<typeof controller.getSnapshot> }) {
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
      <TryExample loading={view.status === 'Loading the example'} />
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

/**
 * "Try the example": a host session with example sounds that runs entirely in this page, so a
 * visitor can try the app before installing anything. Its code loads only when pressed. It says
 * what can be tried (2.41): not "every feature", since the Windows hotkey, Keep, Spotify and the
 * newer Mimic history need the host's PC.
 */
export function TryExample({ loading }: { loading: boolean }) {
  return (
    <div className="try-example">
      <button
        aria-describedby="try-example-note"
        // Busy stays focusable (aria-disabled); presses are ignored until the example has loaded.
        aria-disabled={loading || undefined}
        aria-busy={loading || undefined}
        onClick={() => !loading && void controller.startDemo()}
      >
        <Sparkles size={17} aria-hidden="true" />
        {loading ? 'Loading the example…' : 'Try the example'}
      </button>
      <p id="try-example-note">
        Try the example: sounds, Mimic history and drafts, voices, sentences and simulated guests.
        Nothing is sent to Soundpad; sounds play in this browser.
      </p>
    </div>
  );
}
