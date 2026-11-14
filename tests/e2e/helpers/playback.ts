/**
 * Player lookups for e2e (Stage G, spec 12.2). Each helper does exactly what the tests did before Stage
 * G; WP-B changes only the bodies (Kick's details popover in B3, compact Spotify in B4). Owned by WP-B
 * after Phase 0.
 */
import type { Page } from '@playwright/test';

/** Presses "Decrease Kick counter" or "Reset Kick counter" (behind "Kick counter details" from B3). */
export async function kickDetail(page: Page, action: 'Decrease' | 'Reset') {
  await page.getByRole('button', { name: `${action} Kick counter` }).click();
}

/** Shows Spotify's extra controls (Previous, progress). Nothing to do until B4's compact row. */
export async function spotifyMore(page: Page) {
  void page;
}
