import { dialogOpen } from '../Modal';

/**
 * Whether Mimic's keyboard shortcuts (3/5/0, Enter, Escape) may take this key (2.43). Mimic lives in
 * the non-modal editor panel next to the board, so its shortcuts only apply to keys pressed inside the
 * panel or with focus nowhere (the page body): typing "5" on a board tile or in the board's search must
 * never grab audio. Never while a dialog is open over the page, or while the panel is folded to its bar.
 */
export function mimicKeysApply(e: KeyboardEvent) {
  if (dialogOpen()) return false;
  const panel = document.getElementById('editor-panel');
  if (!panel) return true; // not in the panel (an older layout): the page is Mimic's alone
  if (panel.dataset.panelMode === 'minimized') return false;
  const target = e.target;
  return !(target instanceof Element) || target === document.body || panel.contains(target);
}
