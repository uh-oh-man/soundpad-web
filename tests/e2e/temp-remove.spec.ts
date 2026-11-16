/**
 * Removing Temp audio (2.46, from the 2.45 feedback): a guest removes their own clips (one, or all of them) and
 * the host removes one; each leaves the list at once with no "pending" left behind, and Undo brings it back. The
 * harness runs with --real-removal: like the real Soundpad, it cannot remove one entry, so the files wait for the
 * Temp category to go at quit. (Until 2.46 such a source stayed on the list as "cleanup-pending", with a warning
 * to remove its entry in Soundpad by hand.)
 */
import type { Page } from '@playwright/test';
import {
  test,
  expect,
  host,
  joinGuest,
  ORIGIN,
  startHarness,
  useLocalGuests,
} from './helpers/harness';
import { openTemp } from './helpers/shell';

function wave(pitch: number) {
  const frames = 48000,
    data = Buffer.alloc(44 + frames * 4);
  data.write('RIFF', 0);
  data.writeUInt32LE(data.length - 8, 4);
  data.write('WAVEfmt ', 8);
  data.writeUInt32LE(16, 16);
  data.writeUInt16LE(1, 20);
  data.writeUInt16LE(2, 22);
  data.writeUInt32LE(48000, 24);
  data.writeUInt32LE(192000, 28);
  data.writeUInt16LE(4, 32);
  data.writeUInt16LE(16, 34);
  data.write('data', 36);
  data.writeUInt32LE(frames * 4, 40);
  for (let i = 0; i < frames; i++) {
    const sample = Math.round(Math.sin((i * Math.PI * 2 * pitch) / 48000) * 12000);
    data.writeInt16LE(sample, 44 + i * 4);
    data.writeInt16LE(-sample, 46 + i * 4);
  }
  return data;
}
async function add(page: Page, name: string, pitch: number) {
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: `${name}.wav`, mimeType: 'audio/wav', buffer: wave(pitch) });
  await expect(page.locator('.notification-stack')).toContainText('Added to Temp for this session');
  await expect(page.getByLabel('Add session audio', { exact: true })).toBeEnabled();
}
const row = (page: Page, name: string) =>
  page.locator('#temp-manager .temp-source').filter({ has: page.getByText(name, { exact: true }) });
const notice = (page: Page, text: string) =>
  page.locator('.notification-stack').getByRole('status').filter({ hasText: text });

test('Temp removal: a guest removes their own clips (one or all) and the host one, at once and with Undo', async ({
  page,
}) => {
  const harness = await startHarness(ORIGIN, 4, ['--real-removal']);
  try {
    await useLocalGuests(page);
    await host(page, harness);
    const guest = await joinGuest(page, 'Ann');
    await add(guest, 'Ann one', 440);
    await add(guest, 'Ann two', 550);
    await add(page, 'Host tone', 660);
    await openTemp(guest);
    for (const name of ['Ann one', 'Ann two', 'Host tone'])
      await expect(row(guest, name)).toHaveCount(1);
    // Remove my clip is offered on her own clips only.
    await expect(
      row(guest, 'Ann one').getByRole('button', { name: 'Remove my clip' }),
    ).toBeVisible();
    await expect(
      row(guest, 'Host tone').getByRole('button', { name: 'Remove my clip' }),
    ).toHaveCount(0);
    // No question first (Undo instead), and it is gone from the list at once, with nothing left "pending".
    const asked: string[] = [];
    for (const p of [page, guest])
      p.on('dialog', (d) => {
        asked.push(d.message());
        void d.accept();
      });
    await row(guest, 'Ann one').getByRole('button', { name: 'Remove my clip' }).click();
    await expect(row(guest, 'Ann one')).toHaveCount(0);
    await expect(notice(guest, 'Removed “Ann one” from Temp.')).toBeVisible();
    await openTemp(page);
    await expect(row(page, 'Ann one')).toHaveCount(0);
    for (const p of [guest, page]) {
      await expect(p.locator('#temp-manager')).not.toContainText('pending');
      await expect(p.locator('#temp-manager')).not.toContainText('remove only the listed');
    }
    // Undo brings it back for everyone.
    await notice(guest, 'Removed “Ann one” from Temp.')
      .getByRole('button', { name: 'Undo' })
      .click();
    await expect(notice(guest, '“Ann one” is back in Temp.')).toBeVisible();
    await expect(row(guest, 'Ann one')).toHaveCount(1);
    await expect(row(page, 'Ann one')).toHaveCount(1);
    await expect(
      row(guest, 'Ann one').getByRole('button', { name: 'Remove my clip' }),
    ).toBeVisible();
    // Remove all my Temp clips: one question, her two clips go, the host's stays.
    expect(asked).toEqual([]);
    await guest.getByRole('button', { name: 'Remove all my Temp clips' }).click();
    await expect(row(guest, 'Ann one')).toHaveCount(0);
    expect(asked).toEqual(['Remove all 2 of your Temp clips? Clips you kept permanently stay.']);
    asked.length = 0;
    await expect(row(guest, 'Ann two')).toHaveCount(0);
    await expect(notice(guest, 'Removed your 2 clips from Temp.')).toBeVisible();
    await expect(row(guest, 'Host tone')).toHaveCount(1);
    await expect(row(page, 'Ann two')).toHaveCount(0);
    await expect(guest.getByRole('button', { name: 'Remove all my Temp clips' })).toHaveCount(0);
    await expect(guest.locator('#temp-manager')).not.toContainText('pending');
    await notice(guest, 'Removed your 2 clips from Temp.')
      .getByRole('button', { name: 'Undo' })
      .click();
    await expect(notice(guest, 'Your 2 clips are back in Temp.')).toBeVisible();
    await expect(row(guest, 'Ann one')).toHaveCount(1);
    await expect(row(guest, 'Ann two')).toHaveCount(1);
    // The host's Remove temporary audio: also at once, no question, no pending, no warning.
    await row(page, 'Host tone').getByRole('button', { name: 'Remove temporary audio' }).click();
    await expect(row(page, 'Host tone')).toHaveCount(0);
    await expect(notice(page, 'Removed “Host tone” from Temp.')).toBeVisible();
    await expect(row(guest, 'Host tone')).toHaveCount(0);
    await expect(page.locator('#temp-manager')).not.toContainText('pending');
    await expect(page.locator('#temp-manager')).not.toContainText('remove only the listed');
    expect(asked).toEqual([]);
    // The library no longer offers it either.
    await expect(
      page.getByRole('button', { name: 'Play from start: Host tone', exact: true }),
    ).toHaveCount(0);
  } finally {
    await harness.stop();
  }
});
