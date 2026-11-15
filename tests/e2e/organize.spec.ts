/**
 * Organize mode (2.44, Stage H WP3): selecting sounds without playing them, Review changes, guests'
 * proposals, Apply against the mock Soundpad, conflicts and recovery, Needs attention, the gate for older
 * companions, and the shared-layout copy preview. The harness reports companion 2.43 until the release
 * bump, so these tests show it as 2.44 (and as 2.43 for the gate test).
 */
import type { Page } from '@playwright/test';
import { test, expect, host, joinGuest, playButton, useLocalGuests } from './helpers/harness';

async function companionVersion(page: Page, version: string) {
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => server.send(message));
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state?.companionVersion) frame.state.companionVersion = version;
      socket.send(JSON.stringify(frame));
    });
  });
}
const organizeToggle = (page: Page) => page.getByRole('button', { name: 'Organize', exact: true });
const select = (page: Page, name: string, nth = 0) =>
  page.getByRole('checkbox', { name: `Select ${name}`, exact: true }).nth(nth);
const selectedCount = (page: Page) => page.locator('.organize-count');
/** A sidebar entry is named by its label and its count ("Favorites 2"). */
const named = (name: string) =>
  new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}( \\d+)?$`);
const category = (page: Page, name: string) =>
  page
    .getByRole('navigation', { name: 'Sound categories' })
    .getByRole('button', { name: named(name) });
const folderEntry = (page: Page, name: string) =>
  page.locator('.sidebar').getByRole('button', { name: named(name) });
const review = (page: Page) => page.locator('.review-changes');
async function addTo(page: Page, menu: string, item: string) {
  await page.getByRole('button', { name: menu, exact: true }).click();
  await page.getByRole('menuitem', { name: item, exact: true }).click();
}
async function openReview(page: Page) {
  await page.getByRole('button', { name: /^Review changes \(\d+\)$/ }).click();
  await expect(review(page)).toBeVisible();
}

test.describe('Organize mode on a 2.44 companion', () => {
  test.beforeEach(async ({ page }) => {
    await companionVersion(page, '2.44.0');
  });

  test('tiles select instead of playing: clicks, Space and Enter', async ({ page, harness }) => {
    await host(page, harness);
    await organizeToggle(page).click();
    await expect(organizeToggle(page)).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#board')).toHaveAttribute('data-organize', 'on');
    const before = harness.plays.length;
    // A click anywhere on the tile toggles it.
    await page.locator('.sound-tile', { has: select(page, 'Airhorn') }).click();
    await expect(select(page, 'Airhorn')).toBeChecked();
    await expect(selectedCount(page)).toHaveText('1 selected');
    // Space and Enter on the focused tile toggle it too; the board's Space shortcut stays out of it.
    await select(page, 'Airhorn').focus();
    await page.keyboard.press('Space');
    await expect(select(page, 'Airhorn')).not.toBeChecked();
    await page.keyboard.press('Enter');
    await expect(select(page, 'Airhorn')).toBeChecked();
    await page
      .locator('.sound-tile', { has: select(page, 'Laugh') })
      .first()
      .click();
    await expect(selectedCount(page)).toHaveText('2 selected');
    await page.waitForTimeout(400);
    expect(harness.plays.slice(before)).toEqual([]);
    // Leaving Organize mode, a click plays again.
    await page.getByRole('button', { name: 'Done organizing' }).click();
    await playButton(page, 'Airhorn').click();
    await expect.poll(() => harness.plays.length).toBeGreaterThan(before);
  });

  test('Shift-click selects a range, Ctrl+A everything shown, Escape leaves', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    await organizeToggle(page).click();
    await select(page, 'Airhorn').click();
    await select(
      page,
      'An extremely long sound title that should wrap gracefully on every single phone screen without breaking the grid',
    ).click({ modifiers: ['Shift'] });
    await expect(selectedCount(page)).toHaveText('4 selected');
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
    await expect(selectedCount(page)).toHaveText('0 selected');
    await select(page, 'Airhorn').focus();
    await page.keyboard.press('ControlOrMeta+a');
    await expect(selectedCount(page)).toHaveText('4 selected');
    await page.keyboard.press('Escape');
    await expect(organizeToggle(page)).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByRole('checkbox', { name: /^Select / })).toHaveCount(0);
  });

  test('a drop and the Add selected to… menu add steps to Review changes', async ({
    page,
    harness,
    browserName,
  }) => {
    await host(page, harness);
    await organizeToggle(page).click();
    await select(page, 'Airhorn').click();
    await addTo(page, 'Add selected to…', 'Empty category');
    await expect(page.getByRole('button', { name: 'Review changes (1)' })).toBeVisible();
    // Dragging the selection onto a category in the sidebar.
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
    await select(page, 'Laugh').click();
    const tile = page.locator('.sound-tile', { has: select(page, 'Laugh') }).first();
    if (browserName === 'webkit') {
      // Playwright's WebKit does not turn a mouse drag into HTML drag and drop: the same events, sent directly.
      const data = await page.evaluateHandle(() => new DataTransfer());
      await tile.dispatchEvent('dragstart', { dataTransfer: data });
      await category(page, 'Favorites').dispatchEvent('dragover', { dataTransfer: data });
      await category(page, 'Favorites').dispatchEvent('drop', { dataTransfer: data });
    } else await tile.dragTo(category(page, 'Favorites'));
    await expect(page.getByRole('button', { name: 'Review changes (2)' })).toBeVisible();
    await openReview(page);
    await expect(review(page).getByText('Add “Airhorn” to “Empty category”')).toBeVisible();
    await expect(review(page).getByText('Add “Laugh” to “Favorites”')).toBeVisible();
    await expect(review(page).getByText('Soundpad can do this').first()).toBeVisible();
    // Nothing reached Soundpad yet.
    expect(harness.soundpad.filter((c) => c.startsWith('DoAdd'))).toEqual([]);
  });

  test('a guest proposes, the host accepts and applies, and Soundpad shows it verified', async ({
    page,
    harness,
  }) => {
    await useLocalGuests(page);
    await host(page, harness);
    await page.getByRole('button', { name: 'Access settings' }).click();
    await page.getByRole('checkbox', { name: 'Guests may propose organization changes' }).click();
    await expect(
      page.getByRole('checkbox', { name: 'Guests may propose organization changes' }),
    ).toBeChecked();
    await page.getByRole('button', { name: 'Close dialog' }).click();
    const guest = await joinGuest(page, 'Proposer');
    await organizeToggle(guest).click();
    await select(guest, 'Airhorn').click();
    await addTo(guest, 'Add selected to…', 'Empty category');
    await openReview(guest);
    await expect(review(guest).getByText('Add “Airhorn” to “Empty category”')).toBeVisible();
    await review(guest).getByRole('button', { name: 'Propose to the host' }).click();
    await expect(review(guest).getByText('Waiting for the host')).toBeVisible();
    // The host sees it, accepts and applies.
    await organizeToggle(page).click();
    await openReview(page);
    await expect(review(page).getByText('Proposer proposes 1 change')).toBeVisible();
    await review(page).getByRole('button', { name: 'Accept', exact: true }).click();
    await expect(review(page).getByText('Proposed by Proposer')).toBeVisible();
    await review(page).getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(review(page).locator('[data-step-state="verified"]')).toHaveCount(1, {
      timeout: 20000,
    });
    await expect(review(page).getByText('✓ Verified in Soundpad')).toBeVisible();
    // The mock Soundpad's library changed: the category now holds it, on both pages.
    await expect(category(page, 'Empty category')).toContainText('1');
    await expect(category(guest, 'Empty category')).toContainText('1');
  });

  test('a target changed after the preview pauses the plan and keeps it; Rebind, then Apply', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    await organizeToggle(page).click();
    await select(page, 'Airhorn').click();
    await addTo(page, 'Add selected to…', 'Empty category');
    await openReview(page);
    harness.send('library-mutate {"op":"removeCategory","name":"Empty category"}');
    await expect(category(page, 'Empty category')).toHaveCount(0);
    await review(page).getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(review(page).locator('[data-step-state="conflict"]')).toHaveCount(1, {
      timeout: 20000,
    });
    await expect(review(page).getByRole('status')).toContainText('The plan is kept');
    await review(page)
      .getByRole('combobox', { name: /^Rebind / })
      .selectOption({ label: 'Reactions' });
    await review(page).getByRole('button', { name: 'Rebind', exact: true }).click();
    await expect(review(page).locator('[data-step-state="pending"]')).toHaveCount(1);
    await review(page).getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(review(page).locator('[data-step-state="verified"]')).toHaveCount(1, {
      timeout: 20000,
    });
    await expect(category(page, 'Reactions')).toContainText('2');
  });

  test('a partial failure offers to remove the empty categories the plan created', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    await organizeToggle(page).click();
    await select(page, 'Airhorn').click();
    await page.getByRole('button', { name: 'Add selected to…', exact: true }).click();
    await page.getByRole('menuitem', { name: 'New category…' }).click();
    await page.getByRole('textbox', { name: 'New category…' }).fill('Made by the plan');
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await openReview(page);
    await expect(review(page).getByText('New category “Made by the plan”')).toBeVisible();
    harness.send('reject-prefix DoAddSound(');
    await review(page).getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(review(page).locator('[data-step-state="failed"]')).toHaveCount(1, {
      timeout: 20000,
    });
    await expect(category(page, 'Made by the plan')).toHaveCount(1);
    harness.send('reject-prefix');
    await review(page)
      .getByRole('button', { name: /^Remove the empty categories this plan created/ })
      .click();
    await expect(category(page, 'Made by the plan')).toHaveCount(0, { timeout: 20000 });
  });

  test.describe('with a missing file', () => {
    test.use({ harnessOptions: ['--missing-fixture'] });
    test('Needs attention groups it at the end of its category and its play is refused', async ({
      page,
      harness,
    }) => {
      await host(page, harness);
      await category(page, 'Reactions').click();
      const group = page.getByRole('region', { name: 'Needs attention (1)' });
      await expect(group).toBeVisible();
      await expect(group).toContainText('Fixture missing file');
      const before = harness.plays.length;
      const play = group.getByRole('button', { name: 'Play from start: Fixture missing file' });
      await expect(play).toHaveAttribute('aria-disabled', 'true');
      // It still takes the press (aria-disabled keeps it focusable) and says why instead of playing.
      await play.click({ force: true });
      await expect(group.getByRole('status')).toContainText('The file is missing on the host’s PC');
      await page.waitForTimeout(300);
      expect(harness.plays.slice(before)).toEqual([]);
      // The other sounds of the category still play.
      await expect(playButton(page, 'Laugh')).toBeEnabled();
    });
  });

  test('Copy into My Layout previews a shared layout, copies only the chosen folder, and Undo restores', async ({
    page,
    harness,
  }) => {
    await useLocalGuests(page);
    await host(page, harness);
    const guest = await joinGuest(page, 'Sharer');
    await guest.getByRole('button', { name: 'My Layout', exact: true }).click();
    await organizeToggle(guest).click();
    for (const [sound, name] of [
      ['Airhorn', 'Loud ones'],
      ['Laugh', 'Quiet ones'],
    ]) {
      // One sound per folder: the first one is unselected before the second folder.
      if (sound !== 'Airhorn') await select(guest, 'Airhorn').click();
      await select(guest, sound).click();
      await guest.getByRole('button', { name: 'Add selected to…', exact: true }).click();
      await guest.getByRole('menuitem', { name: 'New folder…' }).click();
      await guest.getByRole('textbox', { name: 'New folder…' }).fill(name);
      await guest.getByRole('button', { name: 'Add', exact: true }).click();
    }
    const shared = page.getByRole('button', { name: /Sharer · 2 folders · Copy into My Layout/ });
    await expect(shared).toBeVisible();
    await shared.click();
    const dialog = page.getByRole('dialog', { name: /Copy Sharer.s layout/ });
    await dialog.getByRole('checkbox', { name: /Quiet ones/ }).uncheck();
    await dialog.getByRole('button', { name: 'Copy', exact: true }).click();
    await expect(folderEntry(page, 'Loud ones')).toBeVisible();
    await expect(folderEntry(page, 'Quiet ones')).toHaveCount(0);
    await page
      .getByRole('status')
      .filter({ hasText: 'Copied from Sharer' })
      .getByRole('button', { name: 'Undo' })
      .click();
    await expect(folderEntry(page, 'Loud ones')).toHaveCount(0);
  });
});

test('an older companion: no Host Layout plan, and My Layout organizing still works with Undo', async ({
  page,
  harness,
}) => {
  await companionVersion(page, '2.43.0');
  await host(page, harness);
  await organizeToggle(page).click();
  await expect(page.locator('.organize-bar')).toContainText('needs companion 2.44');
  await expect(page.getByRole('button', { name: /^Review changes/ })).toHaveCount(0);
  await page.getByRole('button', { name: 'My Layout', exact: true }).click();
  await select(page, 'Airhorn').click();
  await page.getByRole('button', { name: 'Add selected to…', exact: true }).click();
  await page.getByRole('menuitem', { name: 'New folder…' }).click();
  await page.getByRole('textbox', { name: 'New folder…' }).fill('Picks');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  const folder = folderEntry(page, 'Picks');
  await expect(folder).toContainText('1');
  await page.getByRole('button', { name: /^Undo \(1\)$/ }).click();
  await expect(folder).toHaveCount(0);
  expect(harness.soundpad.filter((c) => c.startsWith('DoAdd'))).toEqual([]);
});
