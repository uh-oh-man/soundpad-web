/**
 * Shell lookups for e2e (Stage G, spec 12.2). Each helper does exactly what the tests did before Stage
 * G; WP-A changes only the bodies when the shell changes (Voices and Sentences become main views, the
 * Mimic dialog becomes the editor panel). Owned by WP-A after Phase 0.
 */
import { expect, type Page } from '@playwright/test';

export type SectionName = 'Voices' | 'Sentences';

/** The Voices or Sentences window: a dialog until WP-A step A2, then a `main` view. */
export function section(page: Page, name: SectionName) {
  return page.getByRole('dialog', { name });
}

/** Leaves the Voices or Sentences window: its Close button until A2, then the nav "Soundboard". */
export async function closeSection(page: Page, name: SectionName) {
  await section(page, name).getByRole('button', { name: 'Close dialog' }).click();
}

/** Opens (or closes) the inline Temp list. Temp stays a `<details id="temp-manager">`. */
export async function openTemp(page: Page) {
  await page.locator('#temp-manager > summary').click();
}

/** Asserts that the Temp list is open. */
export async function isTempOpen(page: Page) {
  await expect(page.locator('#temp-manager')).toHaveAttribute('open', '');
}

/** The Edit copy form for a Temp source, looked up from the page (it moves into the panel in A4). */
export function editCopyForm(page: Page, name: string) {
  return page.getByRole('form', { name: 'Edit a copy of ' + name });
}

/** Closes Mimic: "Close dialog" in the dialog "Mimic" (the editor panel keeps both names). */
export async function closeEditor(page: Page) {
  await page
    .getByRole('dialog', { name: 'Mimic' })
    .getByRole('button', { name: 'Close dialog' })
    .click();
}
