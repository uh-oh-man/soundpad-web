/**
 * Mimic, from the owner's 2.45 feedback (2.46): the live transcript follows the newest words, one-press
 * Delete with Undo for clips and drafts, and a Full screen toggle.
 */
import type { Locator, Page } from '@playwright/test';
import { test, expect, host, sendCommand } from './helpers/harness';
import { sectionNav, setMimicTranscription } from './helpers/shell';

type DraftReply = { drafts: { id: string; name: string | null }[] };

async function openMimic(page: Page) {
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('main', { name: 'Mimic', exact: true });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  return dialog;
}
/** How far the transcript is from showing its end, in px. */
const fromEnd = (list: Locator) =>
  list.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop);
/** The newest phrase's number ("Fixture tone N"). */
const newestPhrase = (list: Locator) =>
  list.evaluate((el) => {
    const words = [...el.querySelectorAll('.mimic-word')].map((w) => w.textContent ?? '');
    return Number(words.at(-1)) || 0;
  });

test('Mimic transcript follows the newest words; scrolling back pauses it, and Jump to latest or End brings them back', async ({
  page,
  harness,
}) => {
  test.slow(); // several phrases, one every 2 s
  await host(page, harness);
  const dialog = await openMimic(page);
  await setMimicTranscription(dialog);
  // A short list, so two phrases already scroll.
  await page.addStyleTag({
    content: '.mimic-transcript { max-height: 55px !important; min-height: 0 !important; }',
  });
  const list = dialog.getByRole('list', { name: 'Transcript' });
  await expect
    .poll(() => list.evaluate((el) => el.scrollHeight > el.clientHeight + 30), { timeout: 30000 })
    .toBe(true);
  const follow = dialog.getByRole('button', { name: /Following latest|Jump to latest/ });
  await expect(follow).toHaveText(/Following latest/);
  // New words arrive at the end, and the list stays there.
  const first = await newestPhrase(list);
  await expect.poll(() => newestPhrase(list), { timeout: 15000 }).toBeGreaterThan(first);
  expect(await fromEnd(list)).toBeLessThanOrEqual(24);
  // Scrolling back to read older words stops the following.
  await list.evaluate((el) => {
    el.scrollTop = 0;
    el.dispatchEvent(new Event('scroll'));
  });
  await expect(follow).toHaveText(/Jump to latest/);
  const seen = await newestPhrase(list);
  await expect.poll(() => newestPhrase(list), { timeout: 15000 }).toBeGreaterThan(seen);
  expect(await list.evaluate((el) => el.scrollTop)).toBeLessThan(10);
  // One press brings the newest word into view, and the button keeps the keyboard.
  await follow.focus();
  await follow.press('Enter');
  await expect(follow).toHaveText(/Following latest/);
  await expect(follow).toBeFocused();
  expect(await fromEnd(list)).toBeLessThanOrEqual(24);
  // End does the same from the keyboard.
  await list.evaluate((el) => {
    el.scrollTop = 0;
    el.dispatchEvent(new Event('scroll'));
  });
  await expect(follow).toHaveText(/Jump to latest/);
  await page.locator('#mimic-title').focus();
  await page.keyboard.press('End');
  await expect(follow).toHaveText(/Following latest/);
  expect(await fromEnd(list)).toBeLessThanOrEqual(24);
  // A clicked word holds only the waveform. The transcript keeps following live.
  await list.hover();
  await list
    .locator('.mimic-word')
    .last()
    .evaluate((word) => (word as HTMLButtonElement).click());
  await expect(dialog.getByRole('button', { name: 'Go live' })).toBeVisible();
  const held = await newestPhrase(list);
  await expect.poll(() => newestPhrase(list), { timeout: 15000 }).toBeGreaterThan(held);
  await dialog.getByRole('button', { name: 'Go live' }).click();
  await expect(dialog.getByRole('button', { name: 'Live', exact: true })).toBeVisible();
  await expect(follow).toHaveText(/Following latest/);
  await expect(dialog.locator('.mimic-range')).toBeVisible();
});

test('Mimic Delete: one press takes a clip and a draft away, Undo brings them back, and they go once Undo has gone', async ({
  page,
  harness,
}) => {
  test.slow(); // each delete waits for its Undo notice to go (10 s)
  await host(page, harness);
  const dialog = await openMimic(page);
  await page.waitForTimeout(2600);
  await dialog.getByRole('heading', { name: 'Mimic' }).focus();
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Gone soon');
  await page.keyboard.press('Enter');
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  await expect(recent.getByRole('button', { name: /Replay Gone soon/ })).toBeVisible();

  // The clip: gone from the list at once, back with Undo.
  await recent.getByRole('button', { name: 'Delete Gone soon' }).press('Enter');
  await expect(recent).toHaveCount(0);
  await expect(page.locator('#mimic-title')).toBeFocused();
  const clipNotice = page.getByRole('status').filter({ hasText: 'Deleted “Gone soon” from Temp.' });
  await clipNotice.getByRole('button', { name: 'Undo' }).click();
  await expect(recent.getByRole('button', { name: /Replay Gone soon/ })).toBeVisible();

  // Deleted for good once its notice has gone.
  await recent.getByRole('button', { name: 'Delete Gone soon' }).press('Enter');
  await page.mouse.move(0, 0);

  await expect(recent).toHaveCount(0);

  // The draft: Delete closes it at once, Undo opens it again.
  await draft.getByRole('button', { name: 'Delete', exact: true }).press('Enter');
  await expect(draft).toHaveCount(0);
  await expect(dialog.getByLabel('Draft history')).toHaveCount(0);
  await expect(page.locator('#mimic-title')).toBeFocused();
  const draftNotice = page
    .getByRole('status')
    .filter({ hasText: 'Deleted the draft “Gone soon”.' });
  await draftNotice.getByRole('button', { name: 'Undo' }).click();
  await expect(draft).toBeVisible();
  await expect(draft.getByLabel('Clip name')).toHaveValue('Gone soon');
  expect(((await sendCommand(page, 'draftList')).data as DraftReply).drafts).toHaveLength(1);
  // ...and the companion lets go of it once the notice has gone.
  await draft.getByRole('button', { name: 'Delete', exact: true }).press('Enter');
  await page.mouse.move(0, 0);
  await expect
    .poll(async () => ((await sendCommand(page, 'draftList')).data as DraftReply).drafts.length, {
      timeout: 30000,
    })
    .toBe(0);
  await expect(draft).toHaveCount(0);
});

test('Mimic is its own view and Fill window preserves the player', async ({ page, harness }) => {
  await host(page, harness);
  await sectionNav(page).getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
  const toggle = mimic.getByRole('button', { name: 'Fill window' });
  await expect(mimic.getByRole('heading', { name: 'Mimic' })).toBeVisible();
  await expect(mimic.getByRole('complementary', { name: 'Clips' })).toBeVisible();
  await toggle.focus();
  await toggle.press('Enter');
  await expect(toggle).toBeFocused();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Panic:/ })).toBeVisible();
  await page.reload();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await toggle.click();
  await expect(sectionNav(page).getByRole('button', { name: 'Mimic' })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await sectionNav(page).getByRole('button', { name: 'Soundboard' }).click();
  await expect(mimic).toHaveCount(0);
});

test('phone Mimic switches between the transcript and clips without hiding the player', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await host(page, harness);
  const mimic = await openMimic(page);
  await mimic.getByRole('button', { name: 'Clips', exact: true }).click();
  await expect(mimic.getByRole('complementary', { name: 'Clips' })).toBeVisible();
  await expect(mimic.getByRole('button', { name: 'Stop listening' })).toBeHidden();
  await expect(page.getByRole('button', { name: /Panic:/ })).toBeVisible();
  await mimic.getByRole('button', { name: 'Live transcript' }).click();
  await expect(mimic.getByRole('button', { name: 'Stop listening' })).toBeVisible();
});
