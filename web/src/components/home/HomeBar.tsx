import type { MouseEvent } from 'react';
import { ArrowUpRight } from 'lucide-react';
import { controller } from '../../controller';
import { APP_VERSION, APP_VERSION_LABEL } from '../../version';
import { statusLabel } from '../shell/TopBar';
import { PROJECT_URL } from '../shell/Landing';

type View = ReturnType<typeof controller.getSnapshot>;

/** True while the example session is loading (its buttons stay focusable, see the aria-disabled rule). */
export const demoLoading = (view: View) => view.status === 'Loading the example';

/** Starts the example session; ignored while it is already loading. */
export function startDemo(view: View) {
  if (!demoLoading(view)) void controller.startDemo();
}

/** Moves to a section of the start page without touching the address (its hash carries invites). */
export function goToSection(e: MouseEvent<HTMLAnchorElement>, id: string) {
  const target = document.getElementById(id);
  if (!target) return;
  e.preventDefault();
  const still =
    document.documentElement.dataset.motion === 'reduce' ||
    matchMedia('(prefers-reduced-motion: reduce)').matches;
  target.scrollIntoView({ behavior: still ? 'auto' : 'smooth', block: 'start' });
  target.focus({ preventScroll: true });
}

/**
 * The start page's own header (homepage): icon, name, tagline, links, the version and the connection
 * status. Connected screens keep the app's TopBar.
 */
export function HomeBar({ view }: { view: View }) {
  const loading = demoLoading(view);
  return (
    <header className="home-bar">
      <div className="home-bar-inner">
        <a href={location.pathname} className="home-brand">
          <img src={`${import.meta.env.BASE_URL}icon.svg`} alt="" width={40} height={40} />
          <span className="home-wordmark">
            soundpad<span> web</span>
            <span className="sr-only">, home</span>
          </span>
        </a>
        <span className="home-tagline">a little thing for your friends</span>
        <nav className="home-nav" aria-label="Start page">
          <a href="#how" className="home-nav-section" onClick={(e) => goToSection(e, 'how')}>
            How it works
          </a>
          <button
            type="button"
            className="home-nav-demo"
            aria-disabled={loading || undefined}
            aria-busy={loading || undefined}
            onClick={() => startDemo(view)}
          >
            Try the real demo
          </button>
          <a href={PROJECT_URL} target="_blank" rel="noopener noreferrer" className="home-nav-git">
            GitHub
            <ArrowUpRight size={14} aria-hidden="true" />
            <span className="sr-only"> (opens in a new tab)</span>
          </a>
        </nav>
        <span className="status home-status" title={view.status}>
          <i aria-hidden="true" />
          {statusLabel(view.mode, false, false, false, view.status, false, view.demo)}
        </span>
        <span className="home-version" title={`Soundpad Web ${APP_VERSION}`}>
          v{APP_VERSION_LABEL}
        </span>
      </div>
    </header>
  );
}
