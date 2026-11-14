import { useEffect, useRef, type KeyboardEvent, type PointerEvent } from 'react';

/** The docked editor panel's width rules (2.43): at least 360 px, at most 900, and the board keeps 480. */
export const EDITOR_MIN = 360;
export const EDITOR_MAX = 900;
export const EDITOR_DEFAULT = 480;
export const BOARD_MIN = 480;
export const SPLITTER_WIDTH = 8;

/** The widest the panel may be in a `viewport`-wide window with a `sidebar`-wide sidebar. */
export function editorMaxWidth(viewport: number, sidebar: number) {
  return Math.max(
    EDITOR_MIN,
    Math.min(EDITOR_MAX, Math.floor(viewport - sidebar - BOARD_MIN - SPLITTER_WIDTH)),
  );
}

/** `width` kept within the panel's rules for this window (a stored width can come from a wider one). */
export function clampEditorWidth(width: number, viewport: number, sidebar: number) {
  const wanted = Number.isFinite(width) ? Math.round(width) : EDITOR_DEFAULT;
  return Math.min(editorMaxWidth(viewport, sidebar), Math.max(EDITOR_MIN, wanted));
}

/** The width a key asks for, or null for a key the splitter does not use. */
export function splitterKey(
  key: string,
  shift: boolean,
  width: number,
  max: number,
  previous: number,
): number | null {
  const step = shift ? 64 : 16;
  switch (key) {
    case 'ArrowLeft':
      return Math.min(max, width + step);
    case 'ArrowRight':
      return Math.max(EDITOR_MIN, width - step);
    case 'Home':
      return EDITOR_MIN;
    case 'End':
      return max;
    case 'Enter':
      // Between the narrowest width and the last other width.
      return width === EDITOR_MIN ? Math.min(max, Math.max(EDITOR_MIN, previous)) : EDITOR_MIN;
    default:
      return null;
  }
}

/**
 * The bar between the board and the docked editor panel. Drag it, or focus it and use the arrow keys
 * (Shift for bigger steps), Home, End, and Enter to switch between the narrowest and the last width.
 * A double-click goes back to the default width.
 */
export function Splitter({
  width,
  max,
  onChange,
  onCommit,
}: {
  width: number;
  max: number;
  /** While dragging or pressing keys. */
  onChange: (width: number) => void;
  /** When a drag ends or the keys pause: the width to remember. */
  onCommit: (width: number) => void;
}) {
  const drag = useRef<{ x: number; width: number } | null>(null);
  const previous = useRef(width === EDITOR_MIN ? EDITOR_DEFAULT : width);
  const latest = useRef(width);
  latest.current = width;
  const commit = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(commit.current), []);
  const keyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const next = splitterKey(e.key, e.shiftKey, width, max, previous.current);
    if (next === null) return;
    e.preventDefault();
    if (e.key === 'Enter' && width !== EDITOR_MIN) previous.current = width;
    onChange(next);
  };
  const keyUp = () => {
    window.clearTimeout(commit.current);
    commit.current = window.setTimeout(() => onCommit(latest.current), 300);
  };
  const down = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    e.currentTarget.setPointerCapture(e.pointerId);
    drag.current = { x: e.clientX, width };
  };
  const move = (e: PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    // The panel is on the right: moving left widens it.
    const next = drag.current.width + (drag.current.x - e.clientX);
    onChange(Math.min(max, Math.max(EDITOR_MIN, Math.round(next))));
  };
  const up = (e: PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    drag.current = null;
    if (e.currentTarget.hasPointerCapture(e.pointerId))
      e.currentTarget.releasePointerCapture(e.pointerId);
    if (latest.current !== EDITOR_MIN) previous.current = latest.current;
    onCommit(latest.current);
  };
  return (
    <div
      className="splitter"
      role="separator"
      aria-orientation="vertical"
      aria-controls="editor-panel"
      aria-label="Resize editor"
      aria-valuemin={EDITOR_MIN}
      aria-valuemax={max}
      aria-valuenow={width}
      aria-valuetext={`${width} pixels wide`}
      title="Drag, or use the arrow keys, to resize the editor"
      tabIndex={0}
      onKeyDown={keyDown}
      onKeyUp={keyUp}
      onPointerDown={down}
      onPointerMove={move}
      onPointerUp={up}
      onPointerCancel={up}
      onDoubleClick={() => {
        const next = Math.min(max, Math.max(EDITOR_MIN, EDITOR_DEFAULT));
        onChange(next);
        onCommit(next);
      }}
    >
      <span className="splitter-grip" aria-hidden="true" />
    </div>
  );
}
