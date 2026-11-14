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

test('example Mimic: rolling history, a Latest phrase draft, preview and play, the simulated guest’s selection, Keep in Temp', async ({
  page,
}) => {
  if (!(await startExample(page))) return;
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('dialog', { name: 'Mimic' });
  await mimic.getByRole('button', { name: 'Start listening' }).click();
  // The history view (2.39): words to click arrive a moment after they are said.
  await expect(
    mimic.getByRole('list', { name: 'Transcript' }).getByRole('button', { name: 'small' }).first(),
  ).toBeVisible({ timeout: 15000 });
  await expect(mimic.getByRole('button', { name: 'Back 10 s' })).toBeVisible();
  await mimic.getByRole('button', { name: 'Latest phrase' }).click();
  const draft = mimic.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  const name = await draft.getByLabel('Clip name').inputValue();
  expect(name.length).toBeGreaterThan(0);
  // Preview is heard in this browser only: the player stays idle.
  await draft.getByRole('button', { name: 'Preview' }).click();
  await expect(page.locator('.player-track')).not.toContainText('playing');
  await draft.getByRole('button', { name: 'Play into game' }).click();
  await expect(page.locator('.player-track strong')).toHaveText(name);
  await expect(page.locator('.player-track')).toContainText('Requested by Host');
  // A few seconds after the first draft, the simulated guest captures an older phrase.
  const bands = mimic.getByRole('list', { name: "Other people's selections" });
  await expect(bands).toContainText('Alex (simulated)’s selection', { timeout: 15000 });
  await expect(page.locator('.notification-stack')).toContainText(
    'Alex (simulated) captured a clip',
  );
  await bands.getByRole('button', { name: /^Copy Alex \(simulated\)’s selection/ }).click();
  await expect(mimic.getByLabel('Draft history')).toBeVisible();
  // Keep in Temp works; keeping permanently needs the host's PC, and says so.
  await mimic.getByRole('button', { name: 'Keep in Temp and close draft' }).click();
  await expect(page.locator('.notification-stack')).toContainText('is in Temp');
  await expect(draft).toHaveCount(0);
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.locator('#temp-manager > summary').click();
  const source = page.locator('.temp-source').first();
  await source
    .getByRole('button', { name: /^Keep .*permanently/ })
    .first()
    .click();
  await page.getByRole('button', { name: 'Confirm permanent save' }).click();
  await expect(page.locator('#temp-manager [role="alert"]')).toContainText(
    'Not available in the example',
  );
  // Leaving works with notices on screen, and clears the history and drafts.
  await expect(page.locator('.notification-stack .toast').first()).toBeVisible();
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.getByRole('button', { name: 'Try the example' })).toBeVisible();
});
