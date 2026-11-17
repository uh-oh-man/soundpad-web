/**
 * Feedback (2.45): the header's Feedback button sends a note that the host's companion adds to one file with a
 * fixed name. The harness writes it to a temporary folder (--feedback-dir), never to the desktop.
 */
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  test,
  expect,
  host,
  joinGuest,
  ORIGIN,
  startHarness,
  useLocalGuests,
} from './helpers/harness';

test('Feedback: the host and a guest send notes that land in one file, and an empty note is refused', async ({
  page,
}) => {
  const dir = mkdtempSync(join(tmpdir(), 'soundpad-web-feedback-e2e-'));
  const harness = await startHarness(ORIGIN, 4, ['--feedback-dir', dir]);
  try {
    await useLocalGuests(page);
    await host(page, harness);
    // The host's note.
    await page.getByRole('button', { name: 'Feedback' }).click();
    const dialog = page.getByRole('dialog', { name: 'Send feedback' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('textbox', { name: 'Your note' })).toBeFocused();
    // Nothing written yet: Send says so and keeps the dialog open.
    await dialog.getByRole('button', { name: 'Send' }).click();
    await expect(dialog.getByRole('alert')).toHaveText('Write a note first.');
    await dialog.getByRole('radio', { name: 'Bug' }).check();
    await dialog.getByRole('textbox', { name: 'Your note' }).fill('Search missed "Airhorn"');
    await dialog.getByRole('button', { name: 'Send' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(
      page.getByText('Saved to "Soundpad Web feedback.md" on your desktop.'),
    ).toBeVisible();
    // A guest's note, sent with Ctrl+Enter.
    const guest = await joinGuest(page, 'Ann');
    await guest.getByRole('button', { name: 'Feedback' }).click();
    const theirs = guest.getByRole('dialog', { name: 'Send feedback' });
    await theirs.getByRole('radio', { name: 'Idea' }).check();
    await theirs.getByRole('textbox', { name: 'Your note' }).fill('A bigger Kick button\nplease');
    await theirs.getByRole('textbox', { name: 'Your note' }).press('Control+Enter');
    await expect(theirs).toHaveCount(0);
    await expect(guest.getByText('Thanks! Your note was sent to the host.')).toBeVisible();
    const file = join(dir, 'Soundpad Web feedback.md');
    await expect
      .poll(() => existsSync(file) && readFileSync(file, 'utf8'))
      .toContain('Idea · Ann (guest)');
    const text = readFileSync(file, 'utf8');
    expect(text).toContain('Bug · Host');
    expect(text).toContain('> Search missed "Airhorn"');
    expect(text).toContain('> A bigger Kick button\n> please');
    expect(text).toContain('Where: Soundboard');
  } finally {
    await harness.stop();
    try {
      rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    } catch {
      // Cleaned up later.
    }
  }
});

test('Feedback: the example session has no Feedback button', async ({ page, browserName }) => {
  test.skip(browserName === 'webkit', "Playwright's Windows WebKit cannot start the example");
  await page.goto(ORIGIN + '/');
  await page.getByRole('button', { name: 'Mess with the demo' }).click();
  await expect(page.getByRole('button', { name: 'Leave example' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Feedback' })).toHaveCount(0);
});
