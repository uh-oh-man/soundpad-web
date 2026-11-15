/**
 * Stage H WP4 (2.44): the guided setup, the connection banner's Soundpad fix, browser shortcuts and the
 * host-wide hotkeys, against the mock companion. Host-wide keys are harness fixtures (Ctrl+Alt+F9 plays a
 * key another app owns); no real Windows hotkey, Soundpad or audio device is involved. WebKit runs the
 * same tests and skips only the clipboard check inside the first one.
 */
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect, host, ORIGIN, sendCommand, startHarness } from './helpers/harness';

const guide = (page: Page) => page.getByRole('dialog', { name: 'Set up Soundpad Web' });
const settings = (page: Page) => page.getByRole('dialog', { name: 'Session & access' });
const banner = (page: Page) => page.locator('.connection-banner');
const paused = (page: Page) => page.locator('.playback-suspended');
const tempDir = () => mkdtempSync(join(tmpdir(), 'soundpad-web-setup-e2e-'));
const removeDir = (dir: string) => {
  try {
    rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  } catch {
    // A file still held for a moment: the temp folder is cleaned up later.
  }
};
/**
 * Until the release raises the companion's version to 2.44 (the integrator's step), the harness reports
 * 2.43.0 and the Stage H gates would hide what these tests check: snapshots below 2.44 are raised to it.
 * A newer harness is left as it is.
 */
const raised = new WeakSet<Page>();
async function stageHCompanion(page: Page) {
  if (raised.has(page)) return;
  raised.add(page);
  await rewriteSnapshots(page, (state) => {
    const [major = 0, minor = 0] = String(state.companionVersion ?? '0.0')
      .split('.')
      .map(Number);
    if (state.companionVersion && (major < 2 || (major === 2 && minor < 44)))
      state.companionVersion = '2.44.0';
  });
}
/** The host page until its board is ready, with no dialog in the way (the guide may cover it). */
async function hostPage(page: Page, launch: string, raise = true) {
  if (raise) await stageHCompanion(page);
  // A new harness differs only in the hash, which would not load the page again.
  if (page.url() !== 'about:blank') await page.goto('about:blank');
  await page.goto(launch);
  await expect(page.locator('header .status')).toHaveAttribute('title', 'Host ready');
}
const snapshot = (page: Page) =>
  page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const { state } = controller.getSnapshot();
    return { setupDone: state.setupDone, mimic: !!state.mimic?.active, hotkeys: state.hotkeys };
  });
async function openSettings(page: Page) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(settings(page)).toBeVisible();
  return settings(page);
}
/** Rewrites every snapshot the companion sends, e.g. to stand in for an older companion. */
async function rewriteSnapshots(page: Page, change: (state: Record<string, unknown>) => void) {
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => server.send(message));
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state) change(frame.state);
      socket.send(JSON.stringify(frame));
    });
  });
}

test('first run: the guide checks Soundpad, remembers an output without listening, shares the invite and finishes; a restart does not show it again', async ({
  page,
  browserName,
}) => {
  const dir = tempDir();
  const first = await startHarness(ORIGIN, 4, ['--first-run', '--data-dir', dir]);
  try {
    if (browserName === 'chromium')
      await page
        .context()
        .grantPermissions(['clipboard-read', 'clipboard-write'], { origin: ORIGIN });
    await hostPage(page, first.launch);
    const dialog = guide(page);
    await expect(dialog).toBeVisible();
    // 1. Soundpad
    await expect(dialog.locator('.setup-status')).toContainText('Soundpad answers.');
    await dialog.getByRole('button', { name: 'Next' }).click();
    // 2. Capture output: remembered, and nothing starts listening.
    await expect(dialog).toContainText(
      'never a microphone. Nothing is recorded until you press Start listening.',
    );
    await dialog
      .getByRole('combobox', { name: 'Capture output' })
      .selectOption({ label: 'Fixture: tone every 2 s' });
    await dialog.getByRole('button', { name: 'Remember this output' }).click();
    await expect(dialog).toContainText('Remembered: Fixture: tone every 2 s.');
    expect((await snapshot(page)).mimic).toBe(false);
    await expect(page.locator('.mimic-badge')).toHaveCount(0);
    const settingsFile = JSON.parse(readFileSync(join(dir, 'mimic-settings.json'), 'utf8'));
    expect(settingsFile.sourceId).toBe('fixture:tone');
    // 3. Invitations: Copy Invite copies the link shown.
    await dialog.getByRole('button', { name: 'Next' }).click();
    const link = dialog.locator('#setup-invite');
    await expect(link).toHaveValue(/#join=/);
    await dialog.getByRole('button', { name: 'Copy Invite' }).click();
    if (browserName === 'chromium')
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(
        await link.inputValue(),
      );
    await expect(
      dialog.getByRole('checkbox', { name: /Guests can play and stop sounds/ }),
    ).toBeChecked();
    // 4. Shortcuts, 5. Launch, Finish.
    await dialog.getByRole('button', { name: 'Next' }).click();
    await expect(dialog).toContainText('F6 (save the last N seconds) stays in the Mimic window.');
    await dialog.getByRole('button', { name: 'Next' }).click();
    await expect(dialog).toContainText('Soundpad Web does not start with Windows.');
    await dialog.getByRole('button', { name: 'Finish' }).click();
    await expect(dialog).toHaveCount(0);
    await expect.poll(async () => (await snapshot(page)).setupDone).toBe(true);
    expect(JSON.parse(readFileSync(join(dir, 'setup.json'), 'utf8'))).toMatchObject({
      version: 1,
      dismissed: false,
    });
  } finally {
    await first.stop();
  }
  // The companion starts again on the same data: the guide stays closed.
  const second = await startHarness(ORIGIN, 4, ['--first-run', '--data-dir', dir]);
  try {
    await hostPage(page, second.launch);
    await expect.poll(async () => (await snapshot(page)).setupDone).toBe(true);
    await expect(guide(page)).toHaveCount(0);
    await expect(page.getByRole('complementary', { name: 'Check your setup' })).toHaveCount(0);
  } finally {
    await second.stop();
    removeDir(dir);
  }
});

test('an install with kept data gets the Check your setup card, not the dialog, and can dismiss it', async ({
  page,
}) => {
  const dir = tempDir();
  // The shared style file of 2.43: kept data, so this is an upgrade.
  writeFileSync(join(dir, 'ui.json'), JSON.stringify({ version: 1, theme: 'ocean' }));
  const upgraded = await startHarness(ORIGIN, 4, ['--first-run', '--data-dir', dir]);
  try {
    await hostPage(page, upgraded.launch);
    const card = page.getByRole('complementary', { name: 'Check your setup' });
    await expect(card).toBeVisible();
    await expect(guide(page)).toHaveCount(0);
    // Open setup shows the guide; closing it brings the card back.
    await card.getByRole('button', { name: 'Open setup' }).click();
    await expect(guide(page)).toBeVisible();
    await guide(page).getByRole('button', { name: 'Close dialog' }).click();
    await expect(card).toBeVisible();
    await card.getByRole('button', { name: 'Dismiss Check your setup' }).click();
    await expect(card).toHaveCount(0);
    await expect.poll(async () => (await snapshot(page)).setupDone).toBe(true);
    expect(JSON.parse(readFileSync(join(dir, 'setup.json'), 'utf8')).dismissed).toBe(true);
  } finally {
    await upgraded.stop();
    removeDir(dir);
  }
});

test('Soundpad going away shows the fix and Retry; when it is back the guide says it answers', async ({
  page,
  harness,
}) => {
  await stageHCompanion(page);
  await host(page, harness);
  harness.send('soundpad-offline');
  await expect(banner(page)).toContainText('Open Soundpad.');
  const retry = banner(page).getByRole('button', { name: 'Retry' });
  // Retry while it is still away (from the keyboard): the fix stays, and the button keeps focus.
  await retry.focus();
  await page.keyboard.press('Enter');
  await expect(banner(page)).toContainText('Open Soundpad.');
  await expect(retry).toBeFocused();
  harness.send('soundpad-online');
  await expect(banner(page)).toHaveCount(0);
  const dialog = await openSettings(page);
  await dialog.getByRole('button', { name: 'Run setup again' }).click();
  await expect(guide(page).locator('.setup-status')).toContainText('Soundpad answers.');
});

test('Panic on another key: Shift+Escape no longer panics, the new key does, a clash is refused, and it survives a reload until Reset', async ({
  page,
  harness,
}) => {
  await stageHCompanion(page);
  await host(page, harness);
  let dialog = await openSettings(page);
  const keyboard = dialog.getByRole('region', { name: 'Keyboard' });
  await keyboard.getByRole('button', { name: 'Record a key for Panic' }).click();
  await page.keyboard.press('Control+F8');
  await expect(keyboard.locator('[data-shortcut="panic"] .shortcut-key')).toHaveText('Ctrl+F8');
  // A clash: Stop can't take Panic's key, and nothing changes.
  await keyboard.getByRole('button', { name: 'Record a key for Stop' }).click();
  await page.keyboard.press('Control+F8');
  await expect(keyboard.locator('[data-shortcut="stop"]')).toContainText(
    'Ctrl+F8 is already Panic.',
  );
  await expect(keyboard.locator('[data-shortcut="stop"] .shortcut-key')).toHaveText('Off');
  await dialog.getByRole('button', { name: 'Close dialog' }).click();
  await expect(settings(page)).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Panic:/ })).toHaveAttribute(
    'aria-keyshortcuts',
    'Control+F8',
  );
  await page.locator('#board').focus();
  await page.keyboard.press('Shift+Escape');
  await page.waitForTimeout(400);
  await expect(paused(page)).toHaveCount(0);
  await page.keyboard.press('Control+F8');
  await expect(paused(page)).toContainText('Guest playback is paused.');
  await page.getByRole('button', { name: 'Resume guest playback' }).click();
  await expect(paused(page)).toHaveCount(0);
  // Kept by this browser.
  await page.reload();
  await expect(page.locator('header .status')).toHaveAttribute('title', 'Host ready');
  dialog = await openSettings(page);
  const after = dialog.getByRole('region', { name: 'Keyboard' });
  await expect(after.locator('[data-shortcut="panic"] .shortcut-key')).toHaveText('Ctrl+F8');
  await after.getByRole('button', { name: 'Reset Panic' }).click();
  await expect(after.locator('[data-shortcut="panic"] .shortcut-key')).toHaveText('Shift+Escape');
  await dialog.getByRole('button', { name: 'Close dialog' }).click();
  await page.locator('#board').focus();
  await page.keyboard.press('Shift+Escape');
  await expect(paused(page)).toContainText('Guest playback is paused.');
});

test('a host-wide key another app owns is reported and the previous key keeps working', async ({
  page,
  harness,
}) => {
  await stageHCompanion(page);
  await host(page, harness);
  const dialog = await openSettings(page);
  const setup = dialog.getByRole('region', { name: 'Setup' });
  const panic = setup.locator('[data-hotkey="panic"]');
  await expect(panic.locator('.shortcut-key')).toHaveText('Off');
  await setup.getByRole('button', { name: 'Record a host-wide key for Panic' }).click();
  await page.keyboard.press('Control+Alt+F7');
  await expect(panic.locator('.shortcut-key')).toHaveText('Ctrl+Alt+F7');
  await expect(panic.locator('[data-state]')).toHaveText('On');
  await setup.getByRole('button', { name: 'Record a host-wide key for Panic' }).click();
  await page.keyboard.press('Control+Alt+F9');
  await expect(panic.locator('[data-state]')).toContainText(
    'Taken by another app: Ctrl+Alt+F9 is already used by another app. Your previous key, Ctrl+Alt+F7, still works.',
  );
  await expect(panic.locator('.shortcut-key')).toHaveText('Ctrl+Alt+F7');
  // The Mimic key can't be a host-wide key.
  await setup.getByRole('button', { name: 'Record a host-wide key for Stop playback' }).click();
  await page.keyboard.press('F6');
  await expect(setup.locator('[data-hotkey="stop"]')).toContainText("F6 is the Mimic hotkey's key");
  // The browser shortcut list warns about a key the companion takes.
  const keyboard = dialog.getByRole('region', { name: 'Keyboard' });
  await keyboard.getByRole('button', { name: 'Record a key for Stop' }).click();
  await page.keyboard.press('Control+Alt+F7');
  await expect(keyboard.locator('[data-shortcut="stop"]')).toContainText(
    'the companion takes this key on this PC; this page never receives it.',
  );
});

test('host-wide Panic and Capture keys act from anywhere on the PC', async ({ page, harness }) => {
  await stageHCompanion(page);
  await host(page, harness);
  harness.send('press-hotkey panic');
  await expect(paused(page)).toContainText('Guest playback is paused.');
  // Capture needs a remembered output first; then it starts and stops listening.
  expect(
    (await sendCommand(page, 'mimicSources', { action: 'remember', sourceId: 'fixture:tone' }))
      .error,
  ).toBeUndefined();
  await expect(page.locator('.mimic-badge')).toHaveCount(0);
  harness.send('press-hotkey capture');
  await expect(page.locator('.mimic-badge')).toBeVisible();
  await page.waitForTimeout(400); // past the 300 ms repeat guard
  harness.send('press-hotkey capture');
  await expect(page.locator('.mimic-badge')).toHaveCount(0);
});

test('an older companion: no guide, no Setup section and no host-wide hotkeys; the Keyboard section stays', async ({
  page,
}) => {
  const dir = tempDir();
  const older = await startHarness(ORIGIN, 4, ['--first-run', '--data-dir', dir]);
  try {
    await rewriteSnapshots(page, (state) => {
      if (state.companionVersion) state.companionVersion = '2.43.0';
    });
    await hostPage(page, older.launch, false);
    await expect.poll(async () => (await snapshot(page)).setupDone).toBe(false);
    await page.waitForTimeout(500);
    await expect(guide(page)).toHaveCount(0);
    await expect(page.getByRole('complementary', { name: 'Check your setup' })).toHaveCount(0);
    const dialog = await openSettings(page);
    await expect(dialog.getByRole('region', { name: 'Keyboard' })).toBeVisible();
    await expect(dialog.getByRole('region', { name: 'Setup' })).toHaveCount(0);
    await expect(dialog.getByRole('group', { name: 'Host-wide hotkeys' })).toHaveCount(0);
  } finally {
    await older.stop();
    removeDir(dir);
  }
});
