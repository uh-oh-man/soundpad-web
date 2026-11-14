/**
 * Shell lookups for e2e (Stage G, spec 12.2). Each helper does exactly what the tests did before Stage
 * G; WP-A changes only the bodies when the shell changes (Voices and Sentences become main views, the
 * Mimic dialog becomes the editor panel). Owned by WP-A after Phase 0.
 */
import { expect, type Page } from '@playwright/test';

export type SectionName = 'Voices' | 'Sentences';

/** The Voices or Sentences view: a `main` region named after it (2.43; a dialog before). */
export function section(page: Page, name: SectionName) {
  return page.getByRole('main', { name });
}

/** Leaves the Voices or Sentences view: the sections' "Soundboard" (Escape does nothing at view level). */
export async function closeSection(page: Page, name: SectionName) {
  void name;
  await sectionNav(page).getByRole('button', { name: 'Soundboard', exact: true }).click();
}

/** The section navigation (Soundboard, Mimic, Voices, Sentences), wherever it is mounted. */
export function sectionNav(page: Page) {
  return page.getByRole('navigation', { name: 'Sections' });
}

/** The editor panel (Mimic, copy editor, sound editor, Activity): a non-modal dialog. */
export function editorPanel(page: Page) {
  return page.locator('#editor-panel');
}

/** Opens (or closes) the inline Temp list. Temp stays a `<details id="temp-manager">`. */
export async function openTemp(page: Page) {
  await page.locator('#temp-manager > summary').click();
}

/** Asserts that the Temp list is open. */
export async function isTempOpen(page: Page) {
  await expect(page.locator('#temp-manager')).toHaveAttribute('open', '');
}

/** The Edit copy form for a Temp source, looked up from the page (it is in the editor panel since 2.43). */
export function editCopyForm(page: Page, name: string) {
  return page.getByRole('form', { name: 'Edit a copy of ' + name });
}

/** Closes the editor panel (Mimic and the others): its "Close dialog". */
export async function closeEditor(page: Page) {
  await editorPanel(page).getByRole('button', { name: 'Close dialog' }).click();
}
