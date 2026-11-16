import type { ReactNode } from 'react';
import { BookAudio, LayoutGrid, MessageSquare, Mic2 } from 'lucide-react';
import type { NavStyle, ShellView } from '../../shell';

export type SectionNavProps = {
  /** The main view shown. */
  view: ShellView;
  /** Where the nav is mounted: a vertical list in the sidebar or a row of tabs in the topbar. */
  placement: 'sidebar' | 'topbar';
  canMimic: boolean;
  libraryReady: boolean;
  sentencesReady: boolean;
  showView: (view: ShellView) => void;
};

/**
 * The sections (2.43): Soundboard, Mimic, Voices and Sentences, mounted in exactly one place so every
 * name is unique on the page. Each entry opens a view and marks the current view with aria-current.
 */
export function SectionNav({
  view,
  placement,
  canMimic,
  libraryReady,
  sentencesReady,
  showView,
}: SectionNavProps) {
  const tab = (name: string, target: ShellView, icon: ReactNode) => (
    <li>
      <button
        type="button"
        className="nav-item"
        data-section={target}
        aria-current={view === target ? 'page' : undefined}
        title={placement === 'sidebar' ? name : undefined}
        onClick={() => showView(target)}
      >
        {icon}
        <span className="nav-label">{name}</span>
      </button>
    </li>
  );
  return (
    <nav aria-label="Sections" className={`section-nav nav-${placement}`}>
      {placement === 'sidebar' && (
        <div className="nav-heading" aria-hidden="true">
          SECTIONS
        </div>
      )}
      <ul>
        {tab('Soundboard', 'board', <LayoutGrid size={18} aria-hidden="true" />)}
        {canMimic && tab('Mimic', 'mimic', <Mic2 size={18} aria-hidden="true" />)}
        {libraryReady && tab('Voices', 'voices', <BookAudio size={18} aria-hidden="true" />)}
        {sentencesReady &&
          tab('Sentences', 'sentences', <MessageSquare size={18} aria-hidden="true" />)}
      </ul>
    </nav>
  );
}

/** Where the nav goes: the topbar in tabs style or on narrow screens, else the sidebar. */
export function navPlacement(style: NavStyle, narrow: boolean): 'sidebar' | 'topbar' {
  return style === 'tabs' || narrow ? 'topbar' : 'sidebar';
}
