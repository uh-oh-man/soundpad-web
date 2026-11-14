/**
 * Stage G, WP-B: tiles, the bottom player, Kick, compact Spotify and the header ribbon (spec 12.4,
 * WP-B list). Timing-free: states and counts, never milliseconds. Owned by WP-B.
 */
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect, host, playButton, startHarness } from './helpers/harness';
import { kickDetail, spotifyMore } from './helpers/playback';

type Seam = { frames: number; state: string; key: string };
const seam = (page: Page) =>
  page.evaluate(() => (window as unknown as { __soundpadRibbon?: Seam }).__soundpadRibbon ?? null);
const ribbon = (page: Page) => page.locator('.board-heading .header-ribbon');
/** What the companion last reported: the ribbon's key must be built from exactly this. */
const reportedKey = (page: Page) =>
  page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const { state } = controller.getSnapshot();
    const p = state.playback;
    return p.status === 'PLAYING' && p.epoch ? `${state.hostId}:${p.soundId}:${p.epoch}` : '';
  });
/** Records every ribbon state the page shows, in order. */
const recordStates = (page: Page) =>
  page.evaluate(() => {
    const element = document.querySelector('.board-heading .header-ribbon') as HTMLElement;
    const seen: string[] = [element.dataset.ribbonState ?? ''];
    (window as unknown as { ribbonStates: string[] }).ribbonStates = seen;
    new MutationObserver(() => {
      const state = element.dataset.ribbonState ?? '';
      if (seen[seen.length - 1] !== state) seen.push(state);
    }).observe(element, { attributes: true, attributeFilter: ['data-ribbon-state'] });
  });
const statesSeen = (page: Page) =>
  page.evaluate(() => (window as unknown as { ribbonStates: string[] }).ribbonStates);
/** Rewrites every snapshot the companion sends (an older companion, a native play, …). */
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
async function settings(page: Page, group: string, option: string) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('radiogroup', { name: group }).getByLabel(option, { exact: true }).check();
  await page.getByRole('button', { name: 'Close dialog' }).click();
}
/** A short tone as a WAV file, for a Temp upload. */
function tone(seconds = 1) {
  const frames = 48000 * seconds,
    data = Buffer.alloc(44 + frames * 2);
  data.write('RIFF', 0);
  data.writeUInt32LE(36 + frames * 2, 4);
  data.write('WAVEfmt ', 8);
  data.writeUInt32LE(16, 16);
  data.writeUInt16LE(1, 20);
  data.writeUInt16LE(1, 22);
  data.writeUInt32LE(48000, 24);
  data.writeUInt32LE(96000, 28);
  data.writeUInt16LE(2, 32);
  data.writeUInt16LE(16, 34);
  data.write('data', 36);
  data.writeUInt32LE(frames * 2, 40);
  for (let i = 0; i < frames; i++)
    data.writeInt16LE(Math.round(Math.sin((i / 48000) * 440 * 2 * Math.PI) * 9000), 44 + i * 2);
  return data;
}

test.describe('header ribbon', () => {
  test('is idle on load, follows the accepted play, settles after Stop, and never takes a pointer', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'idle');
    await expect(ribbon(page)).toHaveAttribute('aria-hidden', 'true');
    await expect(ribbon(page).locator('canvas')).toHaveCount(1);
    // Idle draws once, then nothing.
    const idle = (await seam(page))!.frames;
    await page.waitForTimeout(500);
    expect((await seam(page))!.frames).toBe(idle);

    await recordStates(page);
    await playButton(page, 'Airhorn').click();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'live');
    await expect
      .poll(
        async () =>
          (await ribbon(page).getAttribute('data-ribbon-key')) === (await reportedKey(page)),
      )
      .toBe(true);
    expect(await ribbon(page).getAttribute('data-ribbon-key')).toMatch(
      /^[a-f0-9]{64}:[^:]+:[0-9a-f-]{36}$/,
    );
    // Decorative: a point on the crest and one over Copy Invite land on the header, never the ribbon.
    const hits = await page.evaluate(() => {
      const element = document.querySelector('.board-heading .header-ribbon')!;
      const box = element.getBoundingClientRect();
      const invite = [...document.querySelectorAll('.board-actions button')].at(-1)!;
      const button = invite.getBoundingClientRect();
      return [
        document.elementFromPoint(box.left + box.width * 0.62, box.top + box.height * 0.55),
        document.elementFromPoint(button.left + button.width / 2, button.top + button.height / 2),
      ].map((hit) => !!hit && (hit === element || element.contains(hit)));
    });
    expect(hits).toEqual([false, false]);

    // The fixture is silent for its first quarter; stop once it is sounding, so there is a level to settle.
    await page.waitForTimeout(1000);
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'idle');
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-key', '');
    const states = await statesSeen(page);
    expect(states.indexOf('settling')).toBeGreaterThan(states.indexOf('live'));
    expect(states.at(-1)).toBe('idle');
  });

  test('rapid alternating clicks: the key is always the newest accepted play', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    for (const name of ['Airhorn', 'Laugh', 'Airhorn', 'Laugh', 'Airhorn'])
      await playButton(page, name).click();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', /live|waiting/);
    const airhorn = await page.evaluate(async () => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      const { state } = controller.getSnapshot();
      return state.library.sounds.find((s: { name: string }) => s.name === 'Airhorn').id;
    });
    // The key follows what the companion reports, and settles on the newest play (Airhorn): an earlier
    // play may be reported for a moment first (Stage G integration: that moment made this flaky).
    await expect
      .poll(async () => {
        const key = await ribbon(page).getAttribute('data-ribbon-key');
        return !!key && key === (await reportedKey(page)) && key.includes(airhorn);
      })
      .toBe(true);
  });

  test('waits for a sentence or a play the page did not start, with no key to animate', async ({
    page,
    harness,
  }) => {
    await rewriteSnapshots(page, (state) => {
      const playback = state.playback as Record<string, unknown>;
      if (playback.status === 'PLAYING') playback.soundId = 'sentence:fixture';
    });
    await host(page, harness);
    await playButton(page, 'Airhorn').click();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'waiting');
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-key', /:sentence:fixture:/);
    // Waiting is one still frame.
    const frames = (await seam(page))!.frames;
    await page.waitForTimeout(500);
    expect((await seam(page))!.frames).toBe(frames);
  });

  test('a native play (identity unknown) waits with no key', async ({ page, harness }) => {
    await rewriteSnapshots(page, (state) => {
      const playback = state.playback as Record<string, unknown>;
      if (playback.status === 'PLAYING') playback.identitySource = 'unknown';
    });
    await host(page, harness);
    await playButton(page, 'Airhorn').click();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'waiting');
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-key', '');
  });

  test('reduced motion (system): one still picture, no frames while playing', async ({
    page,
    harness,
  }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await host(page, harness);
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'still');
    await playButton(page, 'Airhorn').click();
    await expect(page.locator('.sound-tile.now-playing')).toHaveCount(1);
    await page.waitForTimeout(300);
    const frames = (await seam(page))!.frames;
    await page.waitForTimeout(1000);
    expect((await seam(page))!.frames).toBe(frames);
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'still');
    // No CSS animation keeps running either (the playing chip's bars are still).
    await page.waitForTimeout(500);
    expect(
      await page.evaluate(
        () =>
          document
            .getAnimations()
            .filter(
              (a) =>
                a.playState === 'running' &&
                (a as CSSAnimation).animationName !== undefined &&
                !!(a.effect as KeyframeEffect | null)?.target?.closest?.('.sound-entry, .player'),
            ).length,
      ),
    ).toBe(0);
  });

  test('Settings → Motion: Reduce gives the still picture without the system setting', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    await settings(page, 'Motion', 'Reduce');
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'still');
    await playButton(page, 'Airhorn').click();
    await expect(page.locator('.sound-tile.now-playing')).toHaveCount(1);
    await page.waitForTimeout(300);
    const frames = (await seam(page))!.frames;
    await page.waitForTimeout(1000);
    expect((await seam(page))!.frames).toBe(frames);
  });

  test('Header animation: Still and Off', async ({ page, harness }) => {
    await host(page, harness);
    await settings(page, 'Header animation', 'Still');
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'still');
    await settings(page, 'Header animation', 'Off');
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'off');
    await expect(ribbon(page).locator('canvas')).toHaveCount(0);
    await page.reload();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'off');
    await expect(ribbon(page).locator('canvas')).toHaveCount(0);
  });

  test('a hidden page draws nothing', async ({ page, harness }) => {
    await host(page, harness);
    await playButton(page, 'Airhorn').click();
    await expect(ribbon(page)).toHaveAttribute('data-ribbon-state', 'live');
    await page.evaluate(() => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await page.waitForTimeout(100);
    const frames = (await seam(page))!.frames;
    await page.waitForTimeout(600);
    expect((await seam(page))!.frames).toBe(frames);
  });
});

test.describe('tiles', () => {
  test('playing: a chip, a description, and the same name', async ({ page, harness }) => {
    await host(page, harness);
    await playButton(page, 'Airhorn').click();
    const tile = page.locator('.sound-entry', { has: playButton(page, 'Airhorn') });
    await expect(tile).toHaveClass(/now-playing/);
    await expect(tile.locator('.tile-chip-playing')).toHaveText(/Playing/);
    await expect(playButton(page, 'Airhorn')).toHaveAccessibleDescription('Now playing');
    await expect(
      page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toHaveCount(1);
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await expect(tile.locator('.tile-chip')).toHaveCount(0);
    await expect(playButton(page, 'Airhorn')).toHaveAccessibleDescription('');
  });

  test('queued: a chip while the click waits for the companion', async ({ page, harness }) => {
    await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
      const server = socket.connectToServer();
      socket.onMessage((message) => setTimeout(() => server.send(message), 600));
      server.onMessage((message) => socket.send(message));
    });
    await host(page, harness);
    const tile = page.locator('.sound-entry', { has: playButton(page, 'Laugh') }).first();
    await playButton(page, 'Laugh').click();
    await expect(tile.locator('.tile-chip-queued')).toHaveText('Queued');
    await expect(tile).toHaveClass(/intent-pending/);
    await expect(tile.locator('.tile-chip-playing')).toBeVisible();
    await expect(tile.locator('.tile-chip-queued')).toHaveCount(0);
  });

  test('quick actions: hidden at rest, shown on hover and focus, always on touch screens', async ({
    page,
    harness,
    browser,
  }) => {
    await host(page, harness);
    const quick = page
      .locator('.sound-entry', { has: playButton(page, 'Laugh') })
      .first()
      .locator('.tile-quick');
    const opacity = () => quick.evaluate((e) => getComputedStyle(e).opacity);
    await page.mouse.move(1, 1);
    await expect.poll(opacity).toBe('0');
    // Still focusable and counted visible while transparent.
    await expect(quick.getByRole('button', { name: /^Reverse and play/ })).toBeVisible();
    await quick.hover();
    await expect.poll(opacity).toBe('1');
    await page.mouse.move(1, 1);
    await expect.poll(opacity).toBe('0');
    await playButton(page, 'Laugh').focus();
    await expect.poll(opacity).toBe('1');

    const touch = await browser.newContext({
      hasTouch: true,
      isMobile: true,
      viewport: { width: 412, height: 860 },
    });
    const phone = await touch.newPage();
    try {
      await phone.goto(harness.launch);
      await expect(playButton(phone, 'Airhorn')).toBeEnabled();
      expect(await phone.evaluate(() => matchMedia('(hover: none)').matches)).toBe(true);
      await expect
        .poll(() =>
          phone
            .locator('.tile-quick')
            .first()
            .evaluate((e) => getComputedStyle(e).opacity),
        )
        .toBe('1');
    } finally {
      await touch.close();
    }
  });

  test('Speed menu: keyboard, Escape returns focus; Speed 1.5x on a Temp tile makes and plays the copy', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    await page
      .getByLabel('Add session audio', { exact: true })
      .setInputFiles({ name: 'Quick tone.wav', mimeType: 'audio/wav', buffer: tone() });
    await expect(playButton(page, 'Quick tone')).toBeVisible({ timeout: 25000 });
    const speed = page.getByRole('button', { name: 'Speed for Quick tone', exact: true });
    await speed.focus();
    await page.keyboard.press('Enter');
    const menu = page.getByRole('menu', { name: 'Speed for Quick tone' });
    await expect(menu).toBeVisible();
    await expect(speed).toHaveAttribute('aria-expanded', 'true');
    await expect(menu.getByRole('menuitem', { name: '0.5×' })).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(menu.getByRole('menuitem', { name: '0.75×' })).toBeFocused();
    await page.keyboard.press('End');
    await expect(menu.getByRole('menuitem', { name: 'More in editor…' })).toBeFocused();
    await page.keyboard.press('Home');
    await expect(menu.getByRole('menuitem', { name: '0.5×' })).toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(menu.getByRole('menuitem', { name: 'More in editor…' })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect(speed).toBeFocused();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const before = harness.plays.length;
    await speed.click();
    await menu.getByRole('menuitem', { name: '1.5×' }).click();
    await expect(
      page.getByRole('button', { name: /^Play from start: Quick tone \(1\.5x speed/ }),
    ).toHaveCount(1, { timeout: 25000 });
    await expect.poll(() => harness.plays.length).toBeGreaterThan(before);
    await expect(page.locator('.notification-stack')).toContainText('1.5× copy added to Temp');
  });

  test('Speed on a Soundpad sound (companion 2.43) makes a copy', async ({ page, harness }) => {
    await host(page, harness);
    await page.getByRole('button', { name: 'Speed for Airhorn', exact: true }).click();
    await page.getByRole('menuitem', { name: '1.5×' }).click();
    await expect(
      page.getByRole('button', {
        name: 'Play from start: Airhorn (1.5x speed, same pitch)',
        exact: true,
      }),
    ).toHaveCount(1, { timeout: 25000 });
  });

  test('older companion: no Speed on Soundpad sounds', async ({ page, harness }) => {
    await rewriteSnapshots(page, (state) => {
      if (state.companionVersion) state.companionVersion = '2.42.0';
    });
    await host(page, harness);
    await expect(page.getByRole('button', { name: 'Reverse and play Airhorn' })).toHaveCount(1);
    await expect(page.getByRole('button', { name: /^Speed for / })).toHaveCount(0);
  });

  test('a waveform tile: the first wheel scrolls the board, a wheel after resting zooms', async ({
    page,
  }) => {
    const many = await startHarness(undefined, 24);
    try {
      await page.setViewportSize({ width: 1280, height: 640 });
      await page.goto(many.launch);
      await expect(playButton(page, 'Airhorn')).toBeEnabled();
      await page.getByLabel('View mode').selectOption('waveforms');
      const board = page.locator('main.soundboard');
      const wave = page.getByRole('slider', { name: 'Seek Airhorn' });
      await expect(wave).toBeVisible();
      // Clear of the player at the foot of the window.
      await wave.evaluate((e) => e.scrollIntoView({ block: 'center' }));
      const box = (await wave.boundingBox())!;
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      const top = await board.evaluate((e) => e.scrollTop);
      await page.mouse.wheel(0, 120);
      await expect.poll(() => board.evaluate((e) => e.scrollTop)).toBeGreaterThan(top);
      await expect(page.getByRole('button', { name: /^Fit waveform/ })).toHaveCount(0);
      // Rest on a waveform for over a second, then the wheel zooms it instead.
      // Whichever waveform is now in the middle of the window.
      const at = await page.evaluate(() => {
        const middle = [...document.querySelectorAll('.wave-surface[role="slider"]')]
          .map((e) => e.getBoundingClientRect())
          .find((r) => r.top > 120 && r.bottom < innerHeight - 160)!;
        return { x: middle.left, y: middle.top, width: middle.width, height: middle.height };
      });
      await page.mouse.move(at.x + at.width / 2, at.y + at.height / 2, { steps: 2 });
      await page.waitForTimeout(1200);
      const scrolled = await board.evaluate((e) => e.scrollTop);
      await page.mouse.wheel(0, -240);
      await expect(page.getByRole('button', { name: /^Fit waveform/ })).toHaveCount(1);
      expect(await board.evaluate((e) => e.scrollTop)).toBe(scrolled);
    } finally {
      await many.stop();
    }
  });
});

test.describe('player and Kick', () => {
  test('"+" increments with one click; details hold Decrease, Reset and the totals; keys work', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    const count = page.getByLabel('Kick count', { exact: true });
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(count).toHaveText('1');
    await expect(page.locator('.kick-session')).toHaveText('+1 this session');
    // The details: a group, never a dialog, and Escape returns focus to the disclosure.
    const more = page.getByRole('button', { name: 'Kick counter details' });
    await expect(more).toHaveAttribute('aria-expanded', 'false');
    await more.click();
    const details = page.getByRole('group', { name: 'Kick counter' });
    await expect(details).toContainText('Lifetime1');
    await expect(details).toContainText('This session1');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await details.getByRole('button', { name: 'Decrease Kick counter' }).focus();
    await page.keyboard.press('Escape');
    await expect(details).toHaveCount(0);
    await expect(more).toBeFocused();
    // + and - on a focused Kick control.
    await page.keyboard.press('+');
    await expect(count).toHaveText('2');
    await page.keyboard.press('-');
    await expect(count).toHaveText('1');
    await page.keyboard.press('=');
    await expect(count).toHaveText('2');
    await kickDetail(page, 'Decrease');
    await expect(count).toHaveText('1');
    // An outside press closes the details.
    await page.locator('.board-heading h1').click();
    await expect(details).toHaveCount(0);
    page.once('dialog', (d) => d.accept());
    await kickDetail(page, 'Reset');
    await expect(count).toHaveText('0');
    await expect(page.locator('.kick-session')).toHaveText('+0 this session');
  });

  test('the session count is hidden on an older companion', async ({ page, harness }) => {
    await rewriteSnapshots(page, (state) => {
      if (state.companionVersion) state.companionVersion = '2.42.0';
    });
    await host(page, harness);
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
    await expect(page.locator('.kick-session')).toHaveCount(0);
    await page.getByRole('button', { name: 'Kick counter details' }).click();
    await expect(page.getByRole('group', { name: 'Kick counter' })).not.toContainText(
      'This session',
    );
  });

  test('a restarted companion starts a new session; the lifetime count stays', async ({ page }) => {
    const folder = mkdtempSync(join(tmpdir(), 'soundpad-web-kick-'));
    try {
      const first = await startHarness(undefined, undefined, ['--data-dir', folder]);
      try {
        await page.goto(first.launch);
        await expect(playButton(page, 'Airhorn')).toBeEnabled();
        await page.getByRole('button', { name: 'Increase Kick counter' }).click();
        await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
        await page.getByRole('button', { name: 'Increase Kick counter' }).click();
        await expect(page.locator('.kick-session')).toHaveText('+2 this session');
      } finally {
        await first.stop();
      }
      const second = await startHarness(undefined, undefined, ['--data-dir', folder]);
      try {
        // A new page load (the launch links differ only after the #).
        await page.goto('about:blank');
        await page.goto(second.launch);
        await expect(playButton(page, 'Airhorn')).toBeEnabled();
        await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('2');
        await expect(page.locator('.kick-session')).toHaveText('+0 this session');
      } finally {
        await second.stop();
      }
    } finally {
      rmSync(folder, { recursive: true, force: true });
    }
  });

  test('at 412 x 860 the details stay on screen and clear of Stop and Panic', async ({
    page,
    harness,
  }) => {
    await page.setViewportSize({ width: 412, height: 860 });
    await host(page, harness);
    await page.getByRole('button', { name: 'Kick counter details' }).click();
    const details = (await page.locator('#kick-details').boundingBox())!;
    expect(details.x).toBeGreaterThanOrEqual(0);
    expect(details.y).toBeGreaterThanOrEqual(0);
    expect(details.x + details.width).toBeLessThanOrEqual(412);
    expect(details.y + details.height).toBeLessThanOrEqual(860);
    for (const name of ['Stop', 'Panic: stop audio and pause guest playback']) {
      const button = (await page.getByRole('button', { name, exact: true }).boundingBox())!;
      const apart =
        button.y >= details.y + details.height ||
        button.y + button.height <= details.y ||
        button.x >= details.x + details.width ||
        button.x + button.width <= details.x;
      expect(apart, name).toBe(true);
      expect(button.width).toBeGreaterThanOrEqual(40);
      expect(button.height).toBeGreaterThanOrEqual(40);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(
      true,
    );
  });

  test('"Details for X" asks for the sound editor at its statistics', async ({ page, harness }) => {
    await host(page, harness);
    await page.evaluate(() => {
      (window as unknown as { requests: unknown[] }).requests = [];
      window.addEventListener('soundpad-web:open-editor', (e) =>
        (window as unknown as { requests: unknown[] }).requests.push((e as CustomEvent).detail),
      );
    });
    await playButton(page, 'Airhorn').click();
    await page.getByRole('button', { name: 'Details for Airhorn', exact: true }).click();
    const id = await page.evaluate(async () => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      return controller
        .getSnapshot()
        .state.library.sounds.find((s: { name: string }) => s.name === 'Airhorn').id;
    });
    expect(
      await page.evaluate(() => (window as unknown as { requests: unknown[] }).requests),
    ).toEqual([{ kind: 'sound-edit', soundId: id, focus: 'statistics' }]);
    // "Open editor for X" on the tile asks for the same editor.
    await page.getByRole('button', { name: 'Open editor for Laugh' }).first().click();
    expect(
      await page.evaluate(
        () => (window as unknown as { requests: { kind: string }[] }).requests.length,
      ),
    ).toBe(2);
  });
});

test('compact Spotify: one row with art, title, Play/Pause and Next; more controls survive a reload', async ({
  page,
  harness,
}) => {
  const stub = () =>
    page.evaluate(async () => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      const original = controller.run.bind(controller);
      const c = document.createElement('canvas');
      c.width = c.height = 64;
      c.getContext('2d')!.fillRect(0, 0, 64, 64);
      controller.run = async (op: string, fields: Record<string, unknown>) =>
        op === 'spotify'
          ? {
              enabled: true,
              available: true,
              title: 'Fixture song with a title long enough to need an ellipsis in the sidebar',
              artist: 'Fixture artist',
              status: 'Playing',
              positionMs: 65000,
              durationMs: 180000,
              artwork: c.toDataURL(),
              canToggle: true,
              canNext: true,
              canPrevious: true,
            }
          : original(op, fields);
    });
  await host(page, harness);
  const player = page.getByRole('region', { name: 'Spotify player' });
  // Off: the switch is right there, no expanding needed.
  await expect(player.getByLabel('Enable Spotify desktop integration')).toBeVisible();
  await spotifyMore(page);
  await stub();
  await player.getByLabel('Enable Spotify desktop integration').check();
  await expect(player.getByAltText('Spotify album artwork')).toBeVisible();
  await expect(player.getByText(/^Fixture song/)).toBeVisible();
  await expect(player.getByText('Fixture artist', { exact: true })).toBeVisible();
  await expect(player.getByRole('button', { name: 'Play or pause Spotify' })).toBeVisible();
  await expect(player.getByRole('button', { name: 'Next Spotify track' })).toBeVisible();
  await expect(player.getByRole('button', { name: 'Previous Spotify track' })).toHaveCount(0);
  const row = (await player.locator('.spotify-row').boundingBox())!;
  expect(row.height).toBeLessThanOrEqual(56);
  await spotifyMore(page);
  await expect(player.locator('#spotify-more')).toBeVisible();
  await expect(player.getByRole('button', { name: 'Previous Spotify track' })).toBeVisible();
  await expect(player.getByText('1:05 / 3:00')).toBeVisible();
  await expect(player.getByLabel('Enable Spotify desktop integration')).toBeChecked();
  await page.reload();
  await expect(playButton(page, 'Airhorn')).toBeEnabled();
  await stub();
  await player.getByLabel('Enable Spotify desktop integration').check();
  await expect(player.getByRole('button', { name: 'More Spotify controls' })).toHaveAttribute(
    'aria-expanded',
    'true',
  );
  await expect(player.locator('#spotify-more')).toBeVisible();
});
