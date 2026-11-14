import { useSyncExternalStore, type CSSProperties, type MouseEvent, type ReactNode } from 'react';
import { FOCUSABLE } from '../../focus-return';
import type { NavStyle, PanelMode, ShellView } from '../../shell';

/** True while `query` matches; follows the window as it changes. */
export function useMediaQuery(query: string) {
  return useSyncExternalStore(
    (listener) => {
      let media: MediaQueryList;
      try {
        media = matchMedia(query);
      } catch {
        return () => {};
      }
      media.addEventListener('change', listener);
      return () => media.removeEventListener('change', listener);
    },
    () => {
      try {
        return matchMedia(query).matches;
      } catch {
        return false;
      }
    },
    () => false,
  );
}

/** The window's inner width, kept current (the panel's width is clamped to it). */
export function useViewportWidth() {
  return useSyncExternalStore(
    (listener) => {
      window.addEventListener('resize', listener);
      return () => window.removeEventListener('resize', listener);
    },
    () => window.innerWidth,
    () => 1440,
  );
}

function skipTo(e: MouseEvent<HTMLAnchorElement>, find: () => HTMLElement | null | undefined) {
  const target = find();
  if (!target) return;
  e.preventDefault();
  target.focus();
  target.scrollIntoView?.({ block: 'nearest' });
}

/**
 * The player's first control that is shown. FOCUSABLE is a list of selectors, so it is matched inside the
 * player rather than prefixed with it (".player a, b, c" matched the skip links themselves first).
 */
function firstPlayerControl() {
  const player = document.querySelector('.player');
  if (!player) return null;
  return [...player.querySelectorAll<HTMLElement>(FOCUSABLE)].find(
    (el) => el.getClientRects().length > 0 && !el.closest('[hidden], [inert]'),
  );
}

/**
 * The page's regions in their reading order (2.43, spec 4.2): skip links, the topbar, the example
 * banner, the sidebar and the board column (the current view and the notice dock), the splitter and
 * the editor panel, then the player. `.workspace` stays as an element (scripts and tests look for it);
 * on a wide screen its children join the shell's grid. The state the layout depends on is put on
 * `.app-shell` as data attributes, because the grid there reads it.
 */
export function AppShell({
  className,
  style,
  landing,
  view,
  nav,
  panelMode,
  rail,
  topbar,
  banner,
  sidebar,
  board,
  dock,
  splitter,
  panel,
  player,
  children,
}: {
  className: string;
  style?: CSSProperties;
  landing: boolean;
  view: ShellView;
  nav: NavStyle;
  panelMode: PanelMode | 'closed';
  rail: boolean;
  topbar: ReactNode;
  banner?: ReactNode;
  sidebar?: ReactNode;
  /** The `main#board` element. */
  board?: ReactNode;
  dock?: ReactNode;
  splitter?: ReactNode;
  panel?: ReactNode;
  player?: ReactNode;
  /** Everything else: the landing page, dialogs, the guest name prompt. */
  children?: ReactNode;
}) {
  const open = panelMode !== 'closed';
  return (
    <div
      className={className}
      style={style}
      data-view={landing ? undefined : view}
      data-nav={landing ? undefined : nav}
      data-panel={landing ? undefined : panelMode}
      data-rail={landing ? undefined : String(rail)}
    >
      {!landing && (
        <div className="skip-links">
          <a
            className="skip-link"
            href="#board"
            onClick={(e) => skipTo(e, () => document.getElementById('board'))}
          >
            Skip to sounds
          </a>
          {open && (
            <a
              className="skip-link"
              href="#editor-title"
              onClick={(e) => skipTo(e, () => document.getElementById('editor-title'))}
            >
              Skip to editor
            </a>
          )}
          <a className="skip-link" href="#player" onClick={(e) => skipTo(e, firstPlayerControl)}>
            Skip to player
          </a>
        </div>
      )}
      {topbar}
      {banner}
      {!landing && (
        <div className="workspace">
          {sidebar}
          <div className="board-column">
            {board}
            {dock}
          </div>
          {splitter}
          {panel}
        </div>
      )}
      {player}
      {children}
      {landing && dock}
    </div>
  );
}
