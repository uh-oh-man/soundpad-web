/**
 * Player lookups for e2e (Stage G, spec 12.2). Each helper does what the tests mean, whatever the
 * player looks like: since B3 Decrease and Reset sit behind "Kick counter details", and since B4 the
 * compact Spotify row hides Previous and the progress behind "More Spotify controls". Owned by WP-B.
 */
import type { Page } from '@playwright/test';

/**
 * Presses "Decrease Kick counter" or "Reset Kick counter". An open modal that has the button (Session &
 * access has Reset) is used as it is; otherwise the Kick details popover is opened first.
 */
export async function kickDetail(page: Page, action: 'Decrease' | 'Reset') {
  const name = `${action} Kick counter`;
  const inDialog = page.locator('[role="dialog"][aria-modal="true"]').getByRole('button', { name });
  if (await inDialog.count()) return inDialog.first().click();
  const toggle = page.getByRole('button', { name: 'Kick counter details' });
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
  await page.locator('#kick-details').getByRole('button', { name }).click();
}

/**
 * Shows Spotify's extra controls (Previous, progress, the enable switch once on). Nothing to do while
 * the integration is off: the collapsed row shows the enable switch itself.
 */
export async function spotifyMore(page: Page) {
  const more = page
    .getByRole('region', { name: 'Spotify player' })
    .getByRole('button', { name: 'More Spotify controls' });
  if (!(await more.count())) return;
  if ((await more.getAttribute('aria-expanded')) !== 'true') await more.click();
}
