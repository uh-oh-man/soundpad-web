/**
 * Mimic, from the owner's 2.45 feedback (2.46): the live transcript follows the newest words, one-press
 * Delete with Undo for clips and drafts, and a Full screen toggle.
 */
import type { Locator, Page } from '@playwright/test';
import { test, expect, host, playButton, sendCommand, TEMP_READY } from './helpers/harness';
import { sectionNav } from './helpers/shell';

type DraftReply = { drafts: { id: string; name: string | null }[] };

async function openMimic(page: Page) {
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
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
  await dialog.getByLabel('Transcribe Mimic audio').check();
  // A short list, so two phrases already scroll.
  await page.addStyleTag({ content: '.mimic-transcript { max-height: 40px !important; }' });
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
  });
  await expect(follow).toHaveText(/Jump to latest/);
  const seen = await newestPhrase(list);
  await expect.poll(() => newestPhrase(list), { timeout: 15000 }).toBeGreaterThan(seen);
  expect(await list.evaluate((el) => el.scrollTop)).toBeLessThan(10);
  // One press brings the newest word into view, and the button keeps the keyboard.
  await follow.click();
  await expect(follow).toHaveText(/Following latest/);
  await expect(follow).toBeFocused();
  expect(await fromEnd(list)).toBeLessThanOrEqual(24);
  // End does the same from the keyboard.
  await list.evaluate((el) => {
    el.scrollTop = 0;
  });
  await expect(follow).toHaveText(/Jump to latest/);
  await page.locator('#editor-title').focus();
  await page.keyboard.press('End');
  await expect(follow).toHaveText(/Following latest/);
  expect(await fromEnd(list)).toBeLessThanOrEqual(24);
  // A clicked word holds the view still (the words stop coming); Jump to latest goes live again and
  // keeps the selection.
  // Aiming at a word holds the list still, so the word stays under the pointer as new phrases arrive.
  await list.hover();
  await list.locator('.mimic-word').last().click();
  await expect(dialog.getByRole('button', { name: 'Go live' })).toBeVisible();
  await expect(follow).toHaveText(/Jump to latest/);
  await follow.click();
  await expect(dialog.getByRole('button', { name: 'Live', exact: true })).toBeVisible();
  await expect(follow).toHaveText(/Following latest/);
  await expect(dialog.locator('.mimic-range')).toBeVisible();
  const held = await newestPhrase(list);
  await expect.poll(() => newestPhrase(list), { timeout: 15000 }).toBeGreaterThan(held);
});

test('Mimic Delete: one press takes a clip and a draft away, Undo brings them back, and they go once Undo has gone', async ({
  page,
  harness,
}) => {
  test.slow(); // each delete waits for its Undo notice to go (10 s)
  await host(page, harness);
  const dialog = await openMimic(page);
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Gone soon');
  await page.keyboard.press('Enter');
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  await expect(recent.getByRole('button', { name: /Replay Gone soon/ })).toBeVisible();
  await expect(playButton(page, 'Gone soon')).toBeEnabled({ timeout: TEMP_READY });

  // The clip: gone from the list at once, back with Undo.
  await recent.getByRole('button', { name: 'Delete Gone soon' }).click();
  await expect(recent).toHaveCount(0);
  await expect(page.locator('#editor-title')).toBeFocused();
  const clipNotice = page.getByRole('status').filter({ hasText: 'Deleted “Gone soon” from Temp.' });
  await clipNotice.getByRole('button', { name: 'Undo' }).click();
  await expect(recent.getByRole('button', { name: /Replay Gone soon/ })).toBeVisible();
  await expect(playButton(page, 'Gone soon')).toBeEnabled();
  // Deleted for good once its notice has gone.
  await recent.getByRole('button', { name: 'Delete Gone soon' }).click();
  await page.mouse.move(0, 0);
  await expect(playButton(page, 'Gone soon')).toHaveCount(0, { timeout: 30000 });
  await expect(recent).toHaveCount(0);

  // The draft: Delete closes it at once, Undo opens it again.
  await draft.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(draft).toHaveCount(0);
  await expect(dialog.getByLabel('Draft history')).toHaveCount(0);
  await expect(page.locator('#editor-title')).toBeFocused();
  const draftNotice = page
    .getByRole('status')
    .filter({ hasText: 'Deleted the draft “Gone soon”.' });
  await draftNotice.getByRole('button', { name: 'Undo' }).click();
  await expect(draft).toBeVisible();
  await expect(draft.getByLabel('Clip name')).toHaveValue('Gone soon');
  expect(((await sendCommand(page, 'draftList')).data as DraftReply).drafts).toHaveLength(1);
  // ...and the companion lets go of it once the notice has gone.
  await draft.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.mouse.move(0, 0);
  await expect
    .poll(async () => ((await sendCommand(page, 'draftList')).data as DraftReply).drafts.length, {
      timeout: 30000,
    })
    .toBe(0);
  await expect(draft).toHaveCount(0);
});

test('Mimic Full screen fills the main area, is remembered, and Escape or the toggle goes back', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  const shell = page.locator('.app-shell');
  const board = page.locator('#board');
  await expect(board).toBeVisible();
  const toggle = dialog.getByRole('button', { name: 'Full screen' });
  await toggle.click();
  await expect(shell).toHaveAttribute('data-panel-full', 'true');
  await expect(board).toBeHidden();
  // The same button, now the way back; it keeps the keyboard.
  const back = dialog.getByRole('button', { name: 'Exit full screen' });
  await expect(back).toBeFocused();
  // Mimic now spans everything right of the sidebar.
  const width = page.viewportSize()!.width;
  const panel = (await dialog.boundingBox())!;
  const sidebar = (await page.locator('.sidebar').boundingBox())!;
  expect(panel.x).toBeLessThanOrEqual(sidebar.x + sidebar.width + 1);
  expect(panel.x + panel.width).toBeGreaterThanOrEqual(width - 1);
  expect(await page.evaluate(() => localStorage.getItem('soundpad-web:mimic-full'))).toBe('true');
  // Remembered in this browser.
  await page.reload();
  await expect(dialog).toBeVisible();
  await expect(shell).toHaveAttribute('data-panel-full', 'true');
  // Choosing the Soundboard shows it, with Mimic beside it; Mimic in the nav fills the screen again.
  await sectionNav(page).getByRole('button', { name: 'Soundboard', exact: true }).click();
  await expect(board).toBeVisible();
  await expect(shell).not.toHaveAttribute('data-panel-full', 'true');
  await expect(dialog).toBeVisible();
  await sectionNav(page).getByRole('button', { name: 'Mimic', exact: true }).click();
  await expect(shell).toHaveAttribute('data-panel-full', 'true');
  // Escape leaves full screen first; Mimic stays open. A second Escape closes it as before.
  await page.locator('#editor-title').focus();
  await page.keyboard.press('Escape');
  await expect(shell).not.toHaveAttribute('data-panel-full', 'true');
  await expect(board).toBeVisible();
  await expect(dialog).toBeVisible();
  expect(await page.evaluate(() => localStorage.getItem('soundpad-web:mimic-full'))).toBe('false');
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  // The toggle also goes back.
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  await dialog.getByRole('button', { name: 'Full screen' }).click();
  await dialog.getByRole('button', { name: 'Exit full screen' }).click();
  await expect(shell).not.toHaveAttribute('data-panel-full', 'true');
});
