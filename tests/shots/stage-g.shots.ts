/**
 * Stage G before/after screenshots (spec 12.5). Review only: nothing is compared here. Uses only names
 * that exist both before and after Stage G (spec 12.1), so the same file runs at both ends. Run through
 * playwright.shots.config.ts with SHOTS_LABEL=before or after; the images (and the first tile's position
 * on the board, for the "tiles higher" test) go to artifacts/screenshots/stage-g/<label>/.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import type { Page } from '@playwright/test';
import { test, expect, host, useLocalGuests, joinGuest } from '../e2e/helpers/harness';
import { section, closeSection, closeEditor } from '../e2e/helpers/shell';

const LABEL = process.env.SHOTS_LABEL ?? 'before';
const DIR = `artifacts/screenshots/stage-g/${LABEL}`;
mkdirSync(DIR, { recursive: true });

type Size = { width: number; height: number; scheme: 'light' | 'dark' };
const MATRIX: Size[] = [
  { width: 1280, height: 720, scheme: 'light' },
  { width: 1280, height: 720, scheme: 'dark' },
  { width: 1440, height: 900, scheme: 'light' },
  { width: 1440, height: 900, scheme: 'dark' },
  { width: 412, height: 860, scheme: 'light' },
  { width: 412, height: 860, scheme: 'dark' },
  // The window the host can open in VR.
  { width: 1100, height: 800, scheme: 'dark' },
];
const name = (scene: string, s: Size) => `${scene}-${s.width}x${s.height}-${s.scheme}`;

async function shot(page: Page, scene: string, s: Size) {
  // Let fonts, artwork and waveforms settle; animations are frozen by the screenshot itself.
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${DIR}/${name(scene, s)}.png`, animations: 'disabled' });
}
/** Records where the first sound tile starts (spec 12.4-A11 compares "after" with "before"). */
async function firstTile(page: Page, scene: string, s: Size) {
  const box = await page
    .getByRole('button', { name: /^Play from start: / })
    .first()
    .boundingBox();
  writeFileSync(`${DIR}/${name(scene, s)}.json`, JSON.stringify({ firstTileY: box?.y ?? null }));
}
async function example(page: Page) {
  await page.goto('/');
  await page.getByRole('button', { name: 'Mess with the demo' }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Air Horn', exact: true }),
  ).toBeEnabled({ timeout: 20000 });
}

for (const s of MATRIX)
  test.describe(`${s.width}x${s.height} ${s.scheme}`, () => {
    test.use({ viewport: { width: s.width, height: s.height }, colorScheme: s.scheme });

    test('landing', async ({ page }) => {
      await page.goto('/');
      await expect(page.getByText('Not connected', { exact: true })).toBeVisible();
      await shot(page, 'landing', s);
    });

    test('example board and waveforms', async ({ page }) => {
      await example(page);
      await firstTile(page, 'example-board', s);
      await shot(page, 'example-board', s);
      await page.getByLabel('View mode').selectOption('waveforms');
      await shot(page, 'example-waveforms', s);
    });

    test('example Mimic with notices', async ({ page, browserName }) => {
      test.skip(browserName === 'webkit', 'Playwright’s Windows WebKit cannot start the example');
      await example(page);
      await page.getByRole('button', { name: 'Mimic', exact: true }).click();
      const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
      await mimic.getByRole('button', { name: 'Start listening' }).click();
      await expect(mimic.getByRole('button', { name: 'Last 3 s' })).toBeVisible({
        timeout: 15000,
      });
      await page.waitForTimeout(3200);
      for (let i = 0; i < 2; i++) {
        await mimic.getByRole('button', { name: 'Last 3 s' }).click();
        // "Mimic it" before Stage G; the example's 2.40 history (after) makes the clip with Mimic & play.
        await mimic.getByRole('button', { name: /^(Mimic it|Mimic & play)$/ }).click();
      }
      await expect(page.getByRole('button', { name: 'Undo' }).first()).toBeVisible();
      await shot(page, 'example-mimic-notices', s);
    });

    test('host board, playing', async ({ page, harness }) => {
      await host(page, harness);
      await firstTile(page, 'host-board', s);
      await page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
      await expect(page.locator('.player')).toContainText('Airhorn');
      // The ribbon lifted by the sound (2.43 review): past the fixture's silent lead-in, at a loud part.
      // Shot without the usual settle pause, which would let it fall again; before Stage G there is no
      // ribbon, and the old wait applies.
      const ribbon = page.locator('.board-heading .header-ribbon');
      if (await ribbon.count()) {
        // On a phone the click scrolled the header out of view, where the ribbon does not draw.
        await page.evaluate(() => scrollTo(0, 0));
        await expect(ribbon).toHaveAttribute('data-ribbon-state', 'live');
        await expect
          .poll(
            () =>
              page.evaluate(
                () =>
                  (window as unknown as { __soundpadRibbon?: { level?: number } }).__soundpadRibbon
                    ?.level ?? 0,
              ),
            { timeout: 8000 },
          )
          .toBeGreaterThan(0.5);
        await page.screenshot({ path: `${DIR}/${name('host-playing', s)}.png` });
        // Stopped, it settles back to the flat idle line.
        await page.getByRole('button', { name: 'Stop', exact: true }).click();
        await expect(ribbon).toHaveAttribute('data-ribbon-state', 'idle', { timeout: 8000 });
        await shot(page, 'host-stopped', s);
      } else await shot(page, 'host-playing', s);
    });

    test('host Mimic, Voices, Sentences, Settings', async ({ page, harness }) => {
      await host(page, harness);
      await page.getByRole('button', { name: 'Mimic', exact: true }).click();
      const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
      await expect(mimic.getByRole('button', { name: 'Start listening' })).toBeVisible();
      await shot(page, 'host-mimic', s);
      // Before Stage G, Mimic is modal and Session & access can only open alone; the scene name says which.
      const modal = (await mimic.getAttribute('aria-modal')) === 'true';
      if (modal) await page.keyboard.press('Escape');
      await page.getByRole('button', { name: 'Access settings' }).click();
      await expect(page.getByRole('dialog', { name: 'Session & access' })).toBeVisible();
      await shot(page, modal ? 'session-access-alone' : 'session-access-over-mimic', s);
      await page.keyboard.press('Escape');
      if (!modal) await closeEditor(page);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await page.getByRole('button', { name: 'Voices', exact: true }).click();
      await expect(section(page, 'Voices')).toBeVisible();
      await shot(page, 'voices', s);
      await closeSection(page, 'Voices');
      await page.getByRole('button', { name: 'Sentences', exact: true }).click();
      await expect(section(page, 'Sentences')).toBeVisible();
      await shot(page, 'sentences', s);
    });

    test('settings', async ({ page, harness }) => {
      await host(page, harness);
      await page.getByRole('button', { name: 'Access settings' }).click();
      await expect(page.getByRole('dialog', { name: 'Session & access' })).toBeVisible();
      await shot(page, 'settings', s);
    });

    test('guest view', async ({ page, harness }) => {
      await useLocalGuests(page);
      await host(page, harness);
      const guest = await joinGuest(page);
      await shot(guest, 'guest-board', s);
    });

    if (s.width === 1440 && s.scheme === 'dark') {
      test('reduced motion, playing', async ({ page }) => {
        await page.emulateMedia({ reducedMotion: 'reduce' });
        await example(page);
        await page.getByRole('button', { name: 'Play from start: Air Horn', exact: true }).click();
        await shot(page, 'example-reduced-motion-playing', s);
      });
      for (const theme of ['purple', 'ocean'])
        test(`theme ${theme}`, async ({ page }) => {
          await page.addInitScript((t) => localStorage.setItem('soundpad-appearance', t), theme);
          await example(page);
          await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
          await shot(page, `example-theme-${theme}`, s);
        });
    }
  });
