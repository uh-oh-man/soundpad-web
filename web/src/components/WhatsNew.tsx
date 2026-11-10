import { useState } from 'react';
import { X } from 'lucide-react';
import { APP_VERSION, updatedSince } from '../version';

const SEEN_KEY = 'soundpad-web:last-version';

/**
 * One line after the website updated itself (2.18.2): "Updated to vX.Y.Z — See what's new", linking that version's
 * release notes. Shown once per new version; a first-ever visit only remembers the version.
 */
export function WhatsNew({ projectUrl }: { projectUrl: string }) {
  const [show, setShow] = useState(() => {
    try {
      const last = localStorage.getItem(SEEN_KEY);
      if (!last) localStorage.setItem(SEEN_KEY, APP_VERSION);
      return updatedSince(last, APP_VERSION);
    } catch {
      return false; // storage unavailable: never nag
    }
  });
  if (!show) return null;
  const dismiss = () => {
    try {
      localStorage.setItem(SEEN_KEY, APP_VERSION);
    } catch {
      /* shown again next time; harmless */
    }
    setShow(false);
  };
  return (
    <aside className="whats-new" role="status">
      <span>
        Soundpad Web updated to <strong>v{APP_VERSION}</strong>.{' '}
        <a
          href={`${projectUrl}/releases/tag/v${APP_VERSION}`}
          target="_blank"
          rel="noreferrer"
          onClick={dismiss}
        >
          See what's new
        </a>
      </span>
      <button className="icon-button" aria-label="Dismiss what's new" onClick={dismiss}>
        <X size={14} />
      </button>
    </aside>
  );
}
