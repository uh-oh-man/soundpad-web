/**
 * The 2.47 review of the Mimic page and the player: Stop always reaches Soundpad, Space on a control is the
 * control's, Fill window keeps the section navigation, a dropped connection keeps the Mimic page, a guest
 * who loses Mimic is told why, and keyboard focus stays clear of the notices on a phone.
 */
import type { Page } from '@playwright/test';
import { setMimicTranscription, sectionNav } from './helpers/shell';
import {
  expect,
  host,
  joinGuest,
  ORIGIN,
  sendCommand,
  startHarness,
  test,
  useLocalGuests,
} from './helpers/harness';

async function listen(page: Page, transcripts = false) {
  await sectionNav(page).getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
  await mimic.getByRole('button', { name: 'Start listening' }).click();
  if (transcripts) await setMimicTranscription(mimic);
  return mimic;
}

test('the player’s Stop stops Soundpad playback too while a browser preview is paused', async ({
  page,
  harness,
  browserName,
}) => {
  // Preview is heard in the browser; headless WebKit often refuses to play audio.
  test.skip(browserName === 'webkit', 'Headless WebKit often refuses to play the preview.');
  await host(page, harness);
  const mimic = await listen(page);
  await page.waitForTimeout(2600);
  await page.locator('#mimic-title').focus();
  await page.keyboard.press('5');
  await mimic
    .getByRole('group', { name: 'Selection playback' })
    .getByRole('button', { name: 'Preview', exact: true })
    .click();
  const player = page.locator('.player');
  await expect(player.getByText('Browser preview')).toBeVisible();
  await player.getByRole('button', { name: 'Pause playback' }).click();
  await expect(player.getByRole('button', { name: 'Resume playback' })).toBeVisible();
  // Something plays into the game while the preview waits.
  await sendCommand(page, 'play');
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(1)');
  await expect(player.getByText('Browser preview')).toHaveCount(0);
  // Stop reaches Soundpad (a guest without Panic has no other way), and the preview goes with it.
  await player.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoStopSound()');
  await expect(mimic.getByRole('button', { name: 'Stop playing' })).toHaveCount(0);
});

test('Space on a focused transcript word selects it, even while Soundpad plays', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const mimic = await listen(page, true);
  const word = mimic
    .getByRole('list', { name: 'Transcript' })
    .getByRole('button', { name: 'Fixture', exact: true })
    .first();
  await expect(word).toBeVisible({ timeout: 20000 });
  await sendCommand(page, 'play');
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(1)');
  const before = harness.soundpad.length;
  await word.focus();
  await page.keyboard.press('Space');
  await expect(word).toHaveAttribute('aria-pressed', 'true');
  // The word's own press, not the Play/Pause shortcut: Soundpad was asked for nothing.
  await page.waitForTimeout(500);
  expect(harness.soundpad.slice(before)).toEqual([]);
});

test('Fill window keeps the section navigation, also after a reload', async ({ page, harness }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await sectionNav(page).getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
  const fill = mimic.getByRole('button', { name: 'Fill window' });
  await fill.click();
  await expect(fill).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await expect(sectionNav(page).getByRole('button', { name: 'Soundboard' })).toBeVisible();
  await page.reload();
  await expect(fill).toHaveAttribute('aria-pressed', 'true');
  const board = sectionNav(page).getByRole('button', { name: 'Soundboard' });
  await expect(board).toBeVisible();
  await board.click();
  await expect(mimic).toHaveCount(0);
  await expect(page.getByRole('main', { name: 'Soundboard' })).toBeVisible();
});

test('a dropped connection keeps the Mimic page, its selection and the name being typed', async ({
  page,
}) => {
  const harness = await startHarness(ORIGIN);
  try {
    await host(page, harness);
    const mimic = await listen(page);
    await page.waitForTimeout(2600);
    await page.locator('#mimic-title').focus();
    await page.keyboard.press('5');
    await mimic.getByLabel('Clip name').fill('Keep me');
    await harness.stop();
    await expect(page.locator('header .status')).not.toHaveAttribute('title', 'Host ready', {
      timeout: 20000,
    });
    await page.waitForTimeout(1500);
    await expect(mimic).toBeVisible();
    await expect(mimic.getByLabel('Clip name')).toHaveValue('Keep me');
    await expect(mimic.getByRole('status').first()).toContainText('Reconnecting');
  } finally {
    await harness.stop();
  }
});

test('a guest on a phone who loses Mimic while on Clips is told why, not shown a blank page', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await page.keyboard.press('Escape');
  const guest = await joinGuest(page, 'Phone guest');
  await guest.setViewportSize({ width: 390, height: 844 });
  await sectionNav(guest).getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = guest.getByRole('main', { name: 'Mimic', exact: true });
  await mimic.getByRole('button', { name: 'Clips', exact: true }).click();
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').uncheck();
  await page.keyboard.press('Escape');
  await expect(mimic.getByText('The host has not enabled Mimic for guests.')).toBeVisible();
});

test('on a phone, keyboard focus on the Mimic page never hides under the notices', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 412, height: 860 });
  await host(page, harness);
  await listen(page, true);
  await page.waitForTimeout(2600);
  await page.locator('#mimic-title').focus();
  await page.keyboard.press('5');
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('A fixture notice, long enough to need a second line on a phone.', 'info', {
      label: 'Undo',
      run: () => {},
    });
    controller.notify('A second fixture notice.', 'info', { label: 'Undo', run: () => {} });
  });
  await expect(page.locator('.notification-stack .toast')).toHaveCount(2);
  await page.locator('#mimic-title').focus();
  let checked = 0;
  for (let i = 0; i < 40; i++) {
    await page.keyboard.press('Tab');
    const where = () =>
      page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        const r = el?.getBoundingClientRect();
        const dock = document
          .querySelector('.notice-dock .notification-stack')
          ?.getBoundingClientRect();
        const top = document.querySelector('.app-shell > .topbar')?.getBoundingClientRect();
        if (!el?.closest('#mimic-page') || !r?.height) return null;
        const name = el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 30) ?? '';
        if (dock?.height && r.bottom > dock.top + 1) return `${name}: under the notices`;
        if (top && r.top < top.bottom - 1) return `${name}: under the header`;
        return 'clear';
      });
    const first = await where();
    if (first === null) continue;
    checked++;
    // The page moves focus clear of the notices and the header as it arrives (focus-clear.ts).
    await expect.poll(where, `Tab ${i}`).toBe('clear');
  }
  expect(checked).toBeGreaterThan(5);
});
