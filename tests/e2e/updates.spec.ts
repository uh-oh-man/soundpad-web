/**
 * Stage H WP2 (2.44): the updater and session continuity, with the harness only. Nothing is downloaded and no EXE
 * is replaced: --handoff-out makes harness A leave through the update path and write the hand-over, and
 * --resume-update makes harness B continue that session on the same data folder, as the new companion does.
 */
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import {
  expect,
  host,
  invite,
  joinGuest,
  ORIGIN,
  playButton,
  sendCommand,
  startHarness,
  test,
  useLocalGuests,
} from './helpers/harness';

const UPDATING = 'Soundpad Web is updating; reconnecting…';
const GUEST_UPDATING = 'The host is updating Soundpad Web; reconnecting…';

function dataFolder() {
  return mkdtempSync(join(tmpdir(), 'soundpad-web-e2e-update-'));
}
function remove(folder: string) {
  // The harness may hold a file for a moment after it ends.
  for (let i = 0; i < 20; i++) {
    try {
      rmSync(folder, { recursive: true, force: true });
      return;
    } catch {
      /* retried */
    }
  }
}
async function ok(page: Page, op: string, fields: Record<string, unknown> = {}) {
  const reply = (await sendCommand(page, op, fields)) as { data?: unknown; error?: string };
  expect(reply.error, `${op}: ${reply.error}`).toBeUndefined();
  return reply.data as Record<string, unknown>;
}
type Timeline = { sessionId: string | null; gaps: { reason: string }[]; recording: boolean };
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

test('an update continues the session: same address and invitation, history with an update gap, drafts and Temp', async ({
  page,
}) => {
  test.setTimeout(120000);
  const data = dataFolder();
  const a = await startHarness(ORIGIN, 4, ['--data-dir', data, '--handoff-out']);
  let b: Awaited<ReturnType<typeof startHarness>> | undefined;
  try {
    await useLocalGuests(page);
    await host(page, a);
    await ok(page, 'mimicStart', { sourceId: 'fixture:tone' });
    const guest = await joinGuest(page);
    const invitation = await invite(page);
    const draft = (await ok(page, 'draftCreate', { action: 'recent', name: 'Before the update' }))
      .id as string;
    const before = (await ok(page, 'historyTimeline')) as unknown as Timeline;
    expect(before.recording).toBe(true);
    // A second draft goes into Temp (a clip of this session).
    const second = (await ok(page, 'draftCreate', { action: 'recent', name: 'Kept in Temp' }))
      .id as string;
    const kept = await ok(page, 'draftKeep', { draftId: second });
    const assetId = kept.assetId as string;
    expect(assetId).toBeTruthy();

    // Harness A leaves through the update path (as Update now does) and writes the hand-over.
    a.send('update-exit');
    expect(await a.exited).toBe(0);
    await expect(page.locator('.connection-banner')).toContainText(UPDATING);
    await expect(guest.locator('.connection-banner')).toContainText(GUEST_UPDATING);
    // Temp and the journal's session stayed on disk (no quit cleanup ran).
    const journal = JSON.parse(readFileSync(join(data, 'managed-audio', 'journal.json'), 'utf8'));
    expect(journal.sessionOpen).toBe(true);
    expect(journal.assets.map((x: { id: string }) => x.id)).toContain(assetId);

    // The new process continues the session on the same port and token: the open pages reconnect by themselves.
    b = await startHarness(ORIGIN, 4, ['--data-dir', data, '--resume-update']);
    expect(b.launch).toBe(a.launch);
    await expect(page.locator('header .status')).toHaveText('Hosting', { timeout: 20000 });
    await expect(playButton(page, 'Airhorn')).toBeEnabled();
    await expect(page.locator('.connection-banner')).toHaveCount(0);
    expect(await invite(page)).toBe(invitation);
    // The guest was never asked to join again and can play.
    await expect(guest.locator('header .status')).toHaveText('Connected');
    await expect(guest.locator('.connection-banner')).toHaveCount(0);
    await expect(playButton(guest, 'Airhorn')).toBeEnabled();
    await expect(guest.getByRole('dialog', { name: /gamer name/i })).toHaveCount(0);
    // The same history, still recording, with the gap marked as an update.
    await expect
      .poll(async () => ((await ok(page, 'historyTimeline')) as unknown as Timeline).recording)
      .toBe(true);
    const after = (await ok(page, 'historyTimeline')) as unknown as Timeline;
    expect(after.sessionId).toBe(before.sessionId);
    expect(after.gaps.map((g) => g.reason)).toEqual(['update']);
    // Drafts and Temp, and no Recover or Discard question.
    const drafts = (await ok(page, 'draftList')).drafts as { id: string }[];
    expect(drafts.map((d) => d.id)).toContain(draft);
    const assets = (await ok(page, 'audioAssets')) as { manager: { assets: { id: string }[] } };
    expect(assets.manager.assets.map((x) => x.id)).toContain(assetId);
    expect(
      await page.evaluate(async () => {
        const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
        return controller.getSnapshot().state.tempRecovery ?? null;
      }),
    ).toBeNull();
    // Capture continued on the same output without anyone pressing Start.
    await expect(page.locator('.capture-chip')).toBeVisible();
  } finally {
    await a.stop();
    await b?.stop();
    remove(data);
  }
});

test('an ordinary restart still gets a new address and invitation, and capture stays off', async ({
  page,
}) => {
  const data = dataFolder();
  const a = await startHarness(ORIGIN, 4, ['--data-dir', data]);
  let b: Awaited<ReturnType<typeof startHarness>> | undefined;
  try {
    await host(page, a);
    await ok(page, 'mimicStart', { sourceId: 'fixture:tone' });
    const invitation = await invite(page);
    await a.stop();
    b = await startHarness(ORIGIN, 4, ['--data-dir', data]);
    expect(b.launch).not.toBe(a.launch);
    // A new page load (the launch links differ only after the #).
    await page.goto('about:blank');
    await host(page, b);
    expect(await invite(page)).not.toBe(invitation);
    await expect(page.locator('.capture-chip')).toHaveCount(0);
  } finally {
    await a.stop();
    await b?.stop();
    remove(data);
  }
});

test('Update now and Later: the launch offer, the header chip and Settings, and Update now stays focusable while busy', async ({
  page,
  harness,
}) => {
  // The gate is 2.44, which the harness reports since the release bump; shown as 2.44 whatever comes next.
  await rewriteSnapshots(page, (state) => {
    if (state.companionVersion) state.companionVersion = '2.44.0';
  });
  await host(page, harness);
  // Builds without a release repository (the harness) say so in Settings.
  await page.getByRole('button', { name: 'Access settings' }).click();
  const updates = page.getByRole('region', { name: 'Updates' });
  await expect(updates).toContainText("This build doesn't update itself.");
  await page.getByRole('button', { name: 'Close dialog' }).click();

  harness.send('update-state available 2.44.1');
  const offer = page.getByRole('region', { name: 'Update available' });
  await expect(offer).toContainText('Soundpad Web 2.44.1 is available.');
  await expect(page.locator('.update-chip')).toHaveCount(0);
  // From the keyboard: the offer goes, and the header chip that carries it takes focus (not the page).
  await offer.getByRole('button', { name: 'Later' }).focus();
  await offer.getByRole('button', { name: 'Later' }).press('Enter');
  await expect(offer).toHaveCount(0);
  await expect(page.locator('.update-chip')).toBeFocused();
  // Later reached the companion: a reload does not bring the offer back, the chip stays.
  await page.reload();
  await expect(playButton(page, 'Airhorn')).toBeEnabled();
  await expect(page.getByRole('region', { name: 'Update available' })).toHaveCount(0);
  const chip = page.getByRole('button', { name: 'Update', exact: true });
  await expect(chip).toBeVisible();
  // Settings carries it too, with Check again and the launch setting.
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(updates.getByRole('button', { name: 'Update to v2.44.1' })).toBeVisible();
  await expect(updates.getByRole('button', { name: 'Check again' })).toBeVisible();
  await expect(updates.getByLabel('Check for updates when the companion starts')).toBeChecked();
  await page.getByRole('button', { name: 'Close dialog' }).click();

  await chip.click();
  const dialog = page.getByRole('dialog', { name: 'Update Soundpad Web to 2.44.1' });
  await expect(dialog).toContainText('No backup folder is set');
  await expect(dialog).toContainText('Guests reconnect by themselves with the same invitation.');
  await expect(dialog).toContainText(
    "If the new version doesn't start, the current one comes back.",
  );
  const now = dialog.getByRole('button', { name: 'Update now' });
  // From the keyboard, so the button has focus in Safari too (it does not focus a clicked button).
  await now.focus();
  await now.press('Enter');
  // This harness has nothing to install with: it stays "installing", and the button stays focusable but busy.
  await expect(dialog.locator('.update-status')).toContainText('Installing v2.44.1');
  await expect(now).toHaveAttribute('aria-disabled', 'true');
  await expect(now).toBeFocused();
  // aria-disabled, never disabled: Chrome would drop focus from a disabled control.
  await expect(now).not.toHaveAttribute('disabled');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();

  // A failure says why in plain words and offers the download by hand.
  harness.send('update-state failed 2.44.1 hashMismatch');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(updates).toContainText('does not match the hash published with the release');
  await expect(updates.getByRole('link', { name: 'Download it yourself' })).toHaveAttribute(
    'href',
    'https://example.invalid/releases/tag/v2.44.1',
  );
});

test('an older companion shows no update offer, chip or Updates section', async ({
  page,
  harness,
}) => {
  await rewriteSnapshots(page, (state) => {
    if (state.companionVersion) state.companionVersion = '2.43.0';
  });
  harness.send('update-state available 2.44.1');
  await host(page, harness);
  await expect(playButton(page, 'Airhorn')).toBeEnabled();
  await expect(page.getByRole('region', { name: 'Update available' })).toHaveCount(0);
  await expect(page.locator('.update-chip')).toHaveCount(0);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('region', { name: 'Updates' })).toHaveCount(0);
});

test('after an update that happened while hosting was ended, the new owner page offers Resume hosting and starts nothing by itself', async ({
  page,
  context,
}) => {
  const data = dataFolder();
  const a = await startHarness(ORIGIN, 4, ['--data-dir', data, '--handoff-out']);
  let b: Awaited<ReturnType<typeof startHarness>> | undefined;
  try {
    await host(page, a);
    const invitation = await invite(page);
    await page.getByRole('button', { name: 'Access settings' }).click();
    await page.getByRole('button', { name: 'End hosting', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Resume hosting', exact: true })).toBeVisible();
    a.send('update-exit');
    expect(await a.exited).toBe(0);
    b = await startHarness(ORIGIN, 4, ['--data-dir', data, '--resume-update']);
    // A fresh page on the launch link, as the new companion window opens one.
    const fresh = await context.newPage();
    await fresh.goto(b.launch);
    const resume = fresh.getByRole('button', { name: 'Resume hosting', exact: true });
    await expect(resume).toBeVisible();
    // The start page (2.48 has no status line there): nothing has started by itself.
    await expect(fresh.locator('header.home-bar')).toBeVisible();
    await resume.click();
    await expect(playButton(fresh, 'Airhorn')).toBeEnabled();
    // A new invitation, as Resume hosting always gives after End hosting.
    expect(await invite(fresh)).not.toBe(invitation);
  } finally {
    await a.stop();
    await b?.stop();
    remove(data);
  }
});
