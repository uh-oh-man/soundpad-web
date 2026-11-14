import { useEffect, useRef, type ReactNode } from 'react';
import { Maximize2, Minimize2, X } from 'lucide-react';
import type { EditorKind, PanelMode } from '../../shell';
import { dialogOpen } from '../Modal';

/**
 * The right editor panel (2.43): Mimic, the copy editor, a sound's editor or Activity, one at a time.
 * It is a non-modal dialog, so the board beside it stays playable: no focus trap, and Tab goes on to
 * the player. Below 1000 px it is a sheet over the board, which "Show board" shrinks to a bar above
 * the player without closing anything. Closing unmounts it.
 */
export function EditorPanel({
  kind,
  mode,
  title,
  summary,
  focusKey,
  onClose,
  onMinimize,
  onRestore,
  children,
}: {
  kind: EditorKind;
  mode: PanelMode;
  /** The heading, which also names the dialog ("Mimic", "Copy editor", "Edit X", "Activity"). */
  title: string;
  /** What the minimized bar adds after the title ("draft open"). */
  summary?: string;
  /** Changes whenever focus should go to the heading again (opening, switching kinds, the nav). */
  focusKey: string | number;
  onClose: () => void;
  onMinimize: () => void;
  onRestore: () => void;
  children: ReactNode;
}) {
  const heading = useRef<HTMLHeadingElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    heading.current?.focus({ preventScroll: true });
  }, [focusKey]);
  useEffect(() => {
    // Escape closes the panel when it is meant for it: no dialog is open over it, nothing inside used
    // the key first (Mimic clears a selection or puts a draft away before), and focus is in the panel or
    // nowhere. Escape in the board's search or on a tile keeps its own meaning. Bubble phase, so the
    // capture-phase handlers inside the panel always see the key first.
    function escape(e: KeyboardEvent) {
      if (e.key !== 'Escape' || e.defaultPrevented || e.shiftKey || dialogOpen()) return;
      const target = e.target;
      const inside =
        target === null ||
        target === document.body ||
        (target instanceof Node && !!document.getElementById('editor-panel')?.contains(target));
      if (!inside) return;
      e.preventDefault();
      close.current();
    }
    document.addEventListener('keydown', escape);
    return () => document.removeEventListener('keydown', escape);
  }, []);
  return (
    <section
      id="editor-panel"
      className="editor-panel"
      role="dialog"
      aria-modal="false"
      aria-labelledby="editor-title"
      data-panel-kind={kind}
      data-panel-mode={mode}
    >
      <header className="editor-head">
        <h2 id="editor-title" tabIndex={-1} ref={heading}>
          {title}
          {mode === 'minimized' && summary && <span className="editor-summary"> · {summary}</span>}
        </h2>
        {mode === 'sheet' && (
          <button type="button" className="btn editor-mode" onClick={onMinimize}>
            <Minimize2 size={16} aria-hidden="true" /> Show board
          </button>
        )}
        {mode === 'minimized' && (
          <button type="button" className="btn editor-mode" onClick={onRestore}>
            <Maximize2 size={16} aria-hidden="true" /> Show editor
          </button>
        )}
        <button type="button" className="icon-button" aria-label="Close dialog" onClick={onClose}>
          <X size={19} />
        </button>
      </header>
      <div className="editor-body" hidden={mode === 'minimized'}>
        {children}
      </div>
    </section>
  );
}
