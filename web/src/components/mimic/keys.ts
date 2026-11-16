import { dialogOpen } from '../Modal';

/** Mimic shortcuts apply inside its view, or when the page body has focus. */
export function mimicKeysApply(e: KeyboardEvent) {
  if (dialogOpen()) return false;
  const page = document.getElementById('mimic-page');
  const target = e.target;
  return (
    !!page && (!(target instanceof Element) || target === document.body || page.contains(target))
  );
}
