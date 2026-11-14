/**
 * The example session (Stage G, WP-C, spec 12.4): honest copy, the virtual Mimic history with drafts and a
 * simulated guest's selection, voices and sentences from the example's own material, and the look of the
 * dialogs. Everything here runs in the browser alone: no companion, Soundpad or relay.
 */
import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/** Serious and critical axe findings, as readable lines. */
async function axe(page: Page, disable: string[] = []) {
  const { violations } = await new AxeBuilder({ page })
    // Artwork tiles are named "Play artwork <name>" while showing "<name>" (older than the example; see app.spec.ts).
    .disableRules(['label-content-name-mismatch', ...disable])
    .analyze();
  return violations
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
}

/**
 * Starts the example. Playwright's Windows WebKit has no Web Audio (real Safari has it): there the example must
 * say it cannot start, and the test ends early instead of skipping (spec 1.8: new tests add no skips).
 */
async function startExample(page: Page) {
  if (page.url() === 'about:blank') await page.goto('/');
  const audio = await page.evaluate(
    () => 'AudioContext' in window || 'webkitAudioContext' in window,
  );
  await page.getByRole('button', { name: 'Try the example' }).click();
  if (!audio) {
    await expect(page.getByText(/The example could not start/)).toBeVisible();
    return false;
  }
  await expect(page.getByRole('complementary', { name: 'Example session' })).toBeVisible();
  return true;
}

test('example copy: the start page says what can be tried, and the banner what leaving clears', async ({
  page,
}) => {
  await page.goto('/');
  await expect(page.getByText('See every feature')).toHaveCount(0);
  await expect(page.locator('#try-example-note')).toHaveText(
    'Try the example: sounds, Mimic history and drafts, voices, sentences and simulated guests. Nothing is sent to Soundpad; sounds play in this browser.',
  );
  expect(await axe(page)).toEqual([]);
  if (!(await startExample(page))) return;
  const banner = page.getByRole('complementary', { name: 'Example session' });
  await expect(banner.locator('span')).toHaveText(
    'Example session: nothing here reaches Soundpad. Sounds play in this browser, the guests are simulated, and leaving clears the example’s recordings, drafts and clips. Favourites and view settings stay on this browser.',
  );
  // The simulated guest says so wherever the page names them.
  await expect(page.locator('.connected-guests')).toContainText('Alex (simulated)', {
    timeout: 10000,
  });
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.getByRole('button', { name: 'Try the example' })).toBeVisible();
});
