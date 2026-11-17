/**
 * The example session (Stage G, WP-C, spec 12.4): honest copy, the virtual Mimic history with drafts and a
 * local preview, voices and sentences from the example's own material, and the look of the
 * dialogs. Everything here runs in the browser alone: no companion, Soundpad or relay.
 */
import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { closeSection, section } from './helpers/shell';

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
  await page.getByRole('button', { name: 'Mess with the demo' }).click();
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
    'The demo is the real app with example sounds: the soundboard, Mimic, voices and sentences. Nothing is sent to Soundpad; it all plays in this browser.',
  );
  expect(await axe(page)).toEqual([]);
  test.skip(
    !(await startExample(page)),
    'This browser has no Web Audio, so the example cannot start.',
  );
  const banner = page.getByRole('complementary', { name: 'Example session' });
  await expect(banner.locator('span')).toHaveText(
    'Solo example. Sounds play only in this browser when you choose to play them. Leaving clears the example’s recordings, drafts and clips.',
  );
  await expect(page.locator('.connected-guests')).toHaveCount(0);
  // The restyled dialogs (Stage G C3): Session & access with the appearance choices, then the board again.
  await page.getByRole('button', { name: 'Access settings' }).click();
  const settings = page.getByRole('dialog', { name: 'Session & access' });
  await expect(settings.getByRole('radiogroup', { name: 'Navigation' })).toBeVisible();
  expect(await axe(page)).toEqual([]);
  await settings.getByRole('button', { name: 'Close dialog' }).click();
  expect(await axe(page)).toEqual([]);
  // Organize mode in the Host Layout: the example says why categories don't change (it has no companion).
  await page.getByRole('button', { name: 'Organize', exact: true }).click();
  await expect(page.locator('.organize-bar')).toContainText(
    "The example doesn't change Soundpad categories. My Layout works here.",
  );
  await page.getByRole('button', { name: 'Done organizing' }).click();
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.getByRole('button', { name: 'Mess with the demo' })).toBeVisible();
});

test('example Mimic: rolling history, a Latest phrase draft, preview and play, and Keep in Temp', async ({
  page,
}) => {
  test.skip(
    !(await startExample(page)),
    'This browser has no Web Audio, so the example cannot start.',
  );
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
  await mimic.getByRole('button', { name: 'Start listening' }).click();
  // The history view (2.39): words to click arrive a moment after they are said; Latest phrase takes the
  // newest finished phrase.
  await expect(
    mimic.getByRole('list', { name: 'Transcript' }).getByRole('button', { name: 'man,' }).first(),
  ).toBeVisible({ timeout: 15000 });
  const timed = mimic.getByRole('group', { name: 'Words on the waveform' });
  const first = timed.getByRole('button', { name: 'man,', exact: true }).first();
  // The live waveform glides between polls (2.47) and holds still under the pointer: aim first.
  await timed.hover();
  await first.scrollIntoViewIfNeeded();
  const last = timed.getByRole('button', { name: 'step', exact: true }).first();
  await expect(last).toBeVisible();
  // Drag through the words in their timed lane and leave the selection silent.
  const start = (await first.locator('.mimic-timed-label').boundingBox())!;
  const end = (await last.locator('.mimic-timed-label').boundingBox())!;
  await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
  await page.mouse.down();
  await page.mouse.move(end.x + end.width / 2, end.y + end.height / 2, { steps: 12 });
  await page.mouse.up();
  await expect(first).toHaveAttribute('aria-pressed', 'true');
  await expect(last).toHaveAttribute('aria-pressed', 'true');
  await expect(mimic.getByRole('button', { name: 'Stop playing' })).toHaveCount(0);
  await expect(mimic.getByRole('button', { name: 'Back 10 s' })).toBeVisible();
  await mimic.getByRole('button', { name: 'Latest phrase', exact: true }).click();
  const draft = mimic.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  const name = await draft.getByLabel('Clip name').inputValue();
  expect(name.length).toBeGreaterThan(0);
  // Preview is heard in this browser only: the player stays idle.
  await mimic
    .getByRole('group', { name: 'Selection playback' })
    .getByRole('button', { name: 'Preview', exact: true })
    .click();
  await expect(page.locator('.player-track')).not.toContainText('playing');
  await mimic
    .getByRole('group', { name: 'Selection playback' })
    .getByRole('button', { name: 'Play into game' })
    .click();
  await expect(page.locator('.player-track strong')).toHaveText(name);
  await expect(page.locator('.player-track')).toContainText('Requested by Host');
  await expect(mimic.getByRole('list', { name: "Other people's selections" })).toHaveCount(0);
  // Keep in Temp works; keeping permanently needs the host's PC, and says so.
  await mimic.getByRole('button', { name: 'Keep in Temp and close draft' }).click();
  await expect(page.locator('.notification-stack')).toContainText('is in Temp');
  await expect(draft).toHaveCount(0);
  await closeSection(page, 'Mimic');
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
  await expect(page.getByRole('button', { name: 'Mess with the demo' })).toBeVisible();
});

test('example voices and sentences: search finds a word; "hello [laugh]" with a bleep plays, is kept and reopens', async ({
  page,
}) => {
  test.skip(
    !(await startExample(page)),
    'This browser has no Web Audio, so the example cannot start.',
  );
  // Voices (2.41): the example's own clips, with simulated voices.
  await page.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = section(page, 'Voices');
  await expect(voices.getByRole('list', { name: 'Recordings' })).toContainText(
    'One small step (Apollo 11)',
  );
  await voices.getByLabel('Search the voice library').fill('eagle');
  const result = voices
    .getByRole('list', { name: 'Search results' })
    .getByRole('listitem')
    .filter({ hasText: 'The Eagle has landed (Apollo 11)' });
  await expect(result.getByText('Voice clip', { exact: true })).toBeVisible();
  await expect(result.locator('mark').first()).toHaveText('Eagle');
  expect(await axe(page)).toEqual([]);
  await result
    .getByRole('button', { name: 'The Eagle has landed (Apollo 11)', exact: true })
    .click();
  await voices.getByRole('button', { name: 'Preview all of it' }).click();
  const player = page.locator('.player');
  await expect(player.getByText('Browser preview')).toBeVisible();
  // Space is the Play/Pause shortcut away from controls; on a focused button it presses that button (2.47).
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.keyboard.press('Space');
  await expect(player.getByRole('button', { name: 'Resume playback' })).toBeEnabled();
  await player.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(player.getByText('Browser preview')).toHaveCount(0);
  await voices.getByRole('button', { name: 'All recordings' }).click();
  await expect(voices.getByLabel('Search the voice library')).toHaveValue('eagle');
  // Section memory: the tab shown comes back when Voices is shown again.
  await voices.getByRole('tab', { name: 'Words' }).click();
  await closeSection(page, 'Voices');
  await page.getByRole('button', { name: 'Voices', exact: true }).click();
  await expect(voices.getByRole('tab', { name: 'Words' })).toHaveAttribute('aria-selected', 'true');
  // What needs the host's PC says so, and a press says why.
  await voices.getByRole('tab', { name: 'Word index' }).click();
  await expect(voices.getByText('Host’s PC only')).toBeVisible();
  // aria-disabled (focusable, explained): Playwright will not click it, so press it from the keyboard.
  await voices.getByLabel('Group speakers (experimental)').focus();
  await page.keyboard.press('Space');
  await expect(page.locator('.notification-stack')).toContainText('need the host’s PC');
  await voices.getByRole('tab', { name: 'Library' }).click();
  await closeSection(page, 'Voices');
  // Sentences (2.42): a missing word and the named laugh.
  await page.getByRole('button', { name: 'Sentences', exact: true }).click();
  const sentences = section(page, 'Sentences');
  await sentences.getByLabel('New sentence').fill('hello [laugh]');
  await sentences.getByRole('button', { name: 'New sentence' }).click();
  const builder = sentences.getByRole('region', { name: /^Sentence / });
  const timeline = builder.getByRole('group', { name: 'Timeline' });
  const hello = timeline.getByRole('button', { name: /^Missing word “hello”/ });
  await expect(hello).toBeVisible();
  await expect(timeline.getByRole('button', { name: /\[laugh\]/ })).toBeVisible();
  await hello.click();
  const missing = builder.getByRole('region', { name: /^Piece Missing “hello”/ });
  await missing.getByRole('radio', { name: 'Bleep' }).click();
  await expect(
    timeline.getByRole('button', { name: /^Missing word “hello”.*plays as a bleep/ }),
  ).toBeVisible();
  await builder.getByRole('button', { name: 'Play into game' }).click();
  await expect(page.getByText('Sentence: hello [laugh]', { exact: true })).toBeVisible();
  await builder.getByRole('button', { name: 'Keep', exact: true }).click();
  await expect(builder.getByText('Kept · version 1')).toBeVisible();
  expect(await axe(page)).toEqual([]);
  // Section memory (Stage G): showing Sentences again brings back the sentence that was open.
  await closeSection(page, 'Sentences');
  await page.getByRole('button', { name: 'Sentences', exact: true }).click();
  await expect(builder.getByText('Kept · version 1')).toBeVisible();
  // Reopened from the list, it is the kept sentence with its version.
  await sentences.getByRole('button', { name: 'Back to sentences' }).click();
  await sentences
    .getByRole('list', { name: 'Kept sentences' })
    .getByRole('button', { name: /hello \[laugh\]/ })
    .click();
  await expect(builder.getByText('Kept · version 1')).toBeVisible();
  await expect(
    builder.getByRole('region', { name: 'Versions' }).getByText(/^Version 1 ·/),
  ).toBeVisible();
});
