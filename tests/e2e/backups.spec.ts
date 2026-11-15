/**
 * Backups and restore (Stage H, 2.44, WP1): the host chooses a folder the test made, backs up, restores a
 * removed recording from a backup's detail and takes it back with Undo; an older companion hides the
 * section; Back up now stays focusable and aria-disabled while it runs. Headless, against the mock
 * companion; the backup folder is a temporary one the test removes. Nothing here uses the clipboard.
 */
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect, host, sendCommand, TEMP_READY } from './helpers/harness';

let folder = '';
test.beforeEach(() => {
  folder = mkdtempSync(join(tmpdir(), 'soundpad-web-e2e-backup-folder-'));
});
test.afterEach(() => {
  for (let i = 0; i < 20; i++) {
    try {
      rmSync(folder, { recursive: true, force: true });
      return;
    } catch {
      // A virus scanner may hold a file just written for a moment.
    }
  }
});

/**
 * Reports this companion version to the page: 2.44.0 is what the harness reports since the release bump,
 * and an older one stands for a companion without backups.
 */
async function asCompanion(page: Page, version: string) {
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
async function openSettings(page: Page) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  const section = page
    .getByRole('dialog', { name: 'Session & access' })
    .getByRole('region', { name: 'Backups' });
  await expect(section).toBeVisible();
  return section;
}
async function chooseFolder(page: Page) {
  const section = await openSettings(page);
  await section.getByLabel('Backup folder').fill(folder);
  await section.getByRole('button', { name: 'Save backup settings' }).click();
  await expect(section.getByRole('status').first()).toHaveText('No backup yet.');
  return section;
}
/** Keeps the last ten seconds of the recording history in the voice library, as a draft's Keep in voice library does. */
async function keepRecentInLibrary(page: Page, name: string) {
  const draft = (await sendCommand(page, 'draftCreate', { action: 'recent', name })) as {
    data?: { id: string };
    error?: string;
  };
  expect(draft.error).toBeUndefined();
  const kept = (await sendCommand(page, 'draftKeep', {
    draftId: draft.data!.id,
    action: 'library',
  })) as {
    data?: { recordingId: string };
    error?: string;
  };
  expect(kept.error).toBeUndefined();
  return kept.data!.recordingId;
}
async function recordings(page: Page) {
  const list = (await sendCommand(page, 'libraryList', {})) as {
    data: { items: { id: string; title: string }[] };
  };
  return list.data.items;
}

test('the host chooses a folder, backs up, and sees the backup in the list', async ({
  page,
  harness,
}) => {
  await asCompanion(page, '2.44.0');
  await host(page, harness);
  const section = await chooseFolder(page);
  await section.getByRole('button', { name: 'Back up now' }).click();
  const list = section.getByRole('list', { name: 'Backups list' });
  await expect(list.getByRole('listitem')).toHaveCount(1, { timeout: 15000 });
  await expect(list.getByRole('listitem').first()).toContainText('Manual');
  await expect(section.getByRole('status').first()).toContainText(
    /^Last backup \d\d:\d\d, .+ in 1 backup/,
  );
  // Guests never see paths: the snapshot names no folder of the host's.
  const state = await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    return controller.getSnapshot().state.backups;
  });
  expect(state).toBeGreaterThan(0);
});

test('a removed recording is restored from the backup, and Undo takes it back out', async ({
  page,
  harness,
}) => {
  await asCompanion(page, '2.44.0');
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('dialog', { name: 'Mimic' });
  await mimic.getByRole('button', { name: 'Start listening' }).click();
  await expect(mimic.getByRole('status').first()).toContainText('Listening to Fixture');
  await page.keyboard.press('Escape');
  const id = await keepRecentInLibrary(page, 'Backed up line');
  let section = await chooseFolder(page);
  await section.getByRole('button', { name: 'Back up now' }).click();
  await expect(
    section.getByRole('list', { name: 'Backups list' }).getByRole('listitem'),
  ).toHaveCount(1, { timeout: TEMP_READY });
  await page.getByRole('button', { name: 'Close dialog' }).click();
  expect(
    (await sendCommand(page, 'libraryRemove', { recordingId: id })) as object,
  ).not.toHaveProperty('error');
  expect((await recordings(page)).map((r) => r.id)).not.toContain(id);
  section = await openSettings(page);
  await section.getByRole('list', { name: 'Backups list' }).getByRole('button').first().click();
  const detail = section.getByRole('region', { name: /^Manual backup/ });
  const item = detail.getByRole('listitem').filter({ hasText: 'Backed up line' });
  await expect(item).toContainText('missing now');
  // From the keyboard: the item's Restore goes once it is back, and focus stays in the backup.
  await item.getByRole('button', { name: 'Restore Backed up line' }).focus();
  await item.getByRole('button', { name: 'Restore Backed up line' }).press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog.getByText('Restored 1 recording.')).toBeVisible({ timeout: TEMP_READY });
  await expect.poll(async () => (await recordings(page)).map((r) => r.id)).toContain(id);
  await expect(item).toContainText('same');
  await expect
    .poll(() => page.evaluate(() => !!document.activeElement?.closest('.backup-detail')))
    .toBe(true);
  await dialog
    .locator('.toast')
    .filter({ hasText: 'Restored 1 recording.' })
    .getByRole('button', { name: 'Undo' })
    .click();
  await expect.poll(async () => (await recordings(page)).map((r) => r.id)).not.toContain(id);
  await expect(item).toContainText('missing now');
  // The whole-library confirmation takes focus when it opens, and gives it back on Cancel.
  await detail.getByRole('tab', { name: 'Whole library' }).click();
  const whole = detail.getByRole('button', { name: 'Restore the whole library…' });
  await whole.focus();
  await whole.press('Enter');
  const confirm = detail.getByRole('button', { name: 'Restore the whole library', exact: true });
  await expect(confirm).toBeFocused();
  await detail.getByRole('button', { name: 'Cancel' }).focus();
  await detail.getByRole('button', { name: 'Cancel' }).press('Enter');
  await expect(whole).toBeFocused();
});

test('an older companion: no Backups section', async ({ page, harness }) => {
  await asCompanion(page, '2.43.0');
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog.getByRole('region', { name: 'Appearance' })).toBeVisible();
  await expect(dialog.getByRole('region', { name: 'Backups' })).toHaveCount(0);
  await expect(dialog).toContainText('automatic backups to a folder you choose');
});

test.describe('a slow backup', () => {
  test.use({ harnessOptions: ['--backup-hold-ms', '4000'] });
  test('Back up now is aria-disabled while it runs and keeps focus', async ({ page, harness }) => {
    await asCompanion(page, '2.44.0');
    await host(page, harness);
    expect(harness.log()).toContain('every run waits 4000 ms first');
    const section = await chooseFolder(page);
    const button = section.locator('.backups-now');
    await button.focus();
    await page.keyboard.press('Enter');
    await expect(button).toHaveAttribute('aria-disabled', 'true');
    await expect(button).toHaveText(/Backing up/);
    await expect(button).toBeFocused();
    // aria-disabled, never disabled: a disabled control would drop keyboard focus (CLAUDE.md).
    await expect(button).not.toHaveAttribute('disabled');
    // Pressing it again while it runs starts nothing more.
    await page.waitForTimeout(500);
    await expect(button).toHaveAttribute('aria-disabled', 'true');
    await page.keyboard.press('Enter');
    await expect(button).not.toHaveAttribute('aria-disabled', 'true', { timeout: 15000 });
    await expect(button).toBeFocused();
    await expect(
      section.getByRole('list', { name: 'Backups list' }).getByRole('listitem'),
    ).toHaveCount(1);
  });
});
