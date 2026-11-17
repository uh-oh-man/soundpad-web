/**
 * The start page (homepage): its own header, the pretend soundboard that really plays, the sections and
 * the AI credit, and a layout that never scrolls sideways. Browser only: no companion, Soundpad or relay.
 */
import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFile } from 'node:fs/promises';

/** Serious and critical axe findings, as readable lines. */
async function axe(page: Page) {
  const { violations } = await new AxeBuilder({ page }).analyze();
  return violations
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
}

test('the start page header: name with the version beside it, and links, only before connecting', async ({
  page,
}) => {
  const { version } = JSON.parse(await readFile('package.json', 'utf8')) as { version: string };
  await page.goto('/');
  const header = page.locator('header.home-bar');
  await expect(header.getByRole('link', { name: /^soundpad web\s*, home$/ })).toBeVisible();
  await expect(header.locator('.home-version')).toHaveText(
    `v${version.split('.').slice(0, 2).join('.')}`,
  );
  // The version sits right after the name, on the left (the owner's choice); no status or demo link here.
  const order = await header.evaluate((h) => {
    const name = h.querySelector('.home-brand')!.getBoundingClientRect();
    const version = h.querySelector('.home-version')!.getBoundingClientRect();
    return {
      gap: version.left - name.right,
      left: version.left < h.getBoundingClientRect().width / 2,
    };
  });
  expect(order.left).toBe(true);
  expect(order.gap).toBeGreaterThanOrEqual(0);
  expect(order.gap).toBeLessThan(40);
  await expect(header.locator('.status')).toHaveCount(0);
  await expect(header.getByRole('button', { name: 'Try the real demo' })).toHaveCount(0);
  const github = header.getByRole('link', { name: /^GitHub/ });
  await expect(github).toHaveAttribute('href', 'https://github.com/uh-oh-man/soundpad-web');
  await expect(github).toHaveAttribute('target', '_blank');
  await expect(github).toHaveAttribute('rel', /noopener/);
  // Host from your PC goes to the latest release, in a new tab.
  const hostLink = page.getByRole('link', { name: /^Host from your PC/ });
  await expect(hostLink).toHaveAttribute(
    'href',
    'https://github.com/uh-oh-man/soundpad-web/releases/latest',
  );
  await expect(hostLink).toHaveAttribute('rel', /noopener/);
  // How it works moves to its section without changing the address (its hash carries invitations).
  await header.getByRole('link', { name: 'How it works' }).click();
  await expect(page.locator('#how')).toBeFocused();
  await expect(page.getByRole('heading', { name: /Three steps/ })).toBeInViewport();
  expect(new URL(page.url()).hash).toBe('');
  // The credit says who made it, in the README's words.
  const credits = page.getByRole('region', { name: 'Built with AI. Credited like it.' });
  await expect(credits).toContainText('Codex (OpenAI)');
  await expect(credits).toContainText('Claude (Anthropic)');
  expect(await axe(page)).toEqual([]);
  // The demo starts the example; the app's own header takes over. Playwright's Windows WebKit has no Web
  // Audio, so there the example says it can't start (as the other example tests expect) and the rest is moot.
  const audio = await page.evaluate(
    () => 'AudioContext' in window || 'webkitAudioContext' in window,
  );
  await page.getByRole('button', { name: 'Mess with the demo' }).click();
  if (!audio) {
    await expect(page.getByText(/The example could not start/)).toBeVisible();
    return;
  }
  await expect(page.getByRole('complementary', { name: 'Example session' })).toBeVisible();
  await expect(page.locator('header.home-bar')).toHaveCount(0);
  await expect(page.locator('header.topbar .status')).toHaveText('Example session');
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.locator('header.home-bar')).toBeVisible();
});

test('the pretend soundboard plays in this browser, filters, and Stop ends it', async ({
  page,
}) => {
  // Count what the page asks Web Audio to play, without hearing it.
  await page.addInitScript(() => {
    // No Web Audio (Playwright's Windows WebKit): the pads still light up and Stop works, with no tone to count.
    if (typeof OscillatorNode === 'undefined') return;
    const start = OscillatorNode.prototype.start;
    (window as unknown as { tones: number[] }).tones = [];
    OscillatorNode.prototype.start = function (this: OscillatorNode, ...args: [number?]) {
      (window as unknown as { tones: number[] }).tones.push(this.frequency.value);
      return start.apply(this, args);
    };
  });
  const requests: string[] = [];
  page.on('request', (r) => requests.push(r.url()));
  await page.goto('/');
  const board = page.getByRole('region', { name: 'Go on. Press something.' });
  const now = board.locator('.home-now');
  await expect(now).toHaveText('Nothing yet. Go ahead.');
  // Pads are not the real board's "Play …" buttons.
  await expect(page.getByRole('button', { name: /Play / })).toHaveCount(0);
  const before = requests.length;
  await board.getByRole('button', { name: /^Airhorn/ }).click();
  await expect(now).toHaveText('Airhorn');
  await expect(board).toHaveClass(/is-live/);
  await expect(board.getByRole('button', { name: /^Airhorn/ })).toHaveClass(/is-playing/);
  const audio = await page.evaluate(() => typeof OscillatorNode !== 'undefined');
  if (audio)
    expect(await page.evaluate(() => (window as unknown as { tones: number[] }).tones)).toEqual([
      260,
    ]);
  await board.getByRole('button', { name: 'STOP' }).click();
  await expect(now).toHaveText('Nothing yet. Go ahead.');
  // It lights up for a moment, then lets go by itself.
  await board.getByRole('button', { name: /^Bruh/ }).click();
  await expect(now).toHaveText('Bruh');
  await expect(now).toHaveText('Nothing yet. Go ahead.', { timeout: 3000 });
  await expect(board).not.toHaveClass(/is-live/);
  // The keys 1 to 6 press the pads while the keyboard is in the board.
  await board.getByRole('button', { name: /^Vine boom/ }).focus();
  await page.keyboard.press('3');
  await expect(now).toHaveText('Victory');
  // Filters.
  await board.getByRole('button', { name: 'Game clips' }).click();
  await expect(board.getByRole('button', { name: 'Game clips' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await expect(board.locator('.home-pad')).toHaveCount(2);
  await expect(board.getByText('02 SOUNDS')).toBeVisible();
  await board.getByRole('button', { name: 'Everything' }).click();
  await expect(board.locator('.home-pad')).toHaveCount(6);
  // It fetched nothing to play.
  expect(requests.slice(before)).toEqual([]);
});

test('the start page never scrolls sideways, and reduced motion keeps it a still picture', async ({
  page,
}) => {
  for (const [width, height] of [
    [1440, 900],
    [1280, 720],
    [1000, 800],
    [412, 860],
    [375, 812],
  ]) {
    await page.setViewportSize({ width, height });
    await page.goto('/');
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
      `width ${width}`,
    ).toBeLessThanOrEqual(width);
    await expect(page.locator('header.home-bar .home-version')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Mess with the demo' })).toBeVisible();
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/');
  // The card and its slab stand straight.
  for (const selector of ['.home-board', '.home-board-slab']) {
    expect(
      await page.locator(selector).evaluate((el) => getComputedStyle(el).transform),
      selector,
    ).toBe('none');
  }
  await page.getByRole('button', { name: /^Wow/ }).click();
  await expect(page.locator('.home-board')).toHaveClass(/is-live/);
  // Nothing moves: the waveform's bars, the pad's ripple, the sound-wave ring and the light pools stay still.
  for (const selector of ['.home-wave i', '.home-pad-ripple', '.home-rings-wave', '.home-aurora']) {
    expect(
      await page
        .locator(selector)
        .first()
        .evaluate((el) => getComputedStyle(el).animationName),
      selector,
    ).toBe('none');
  }
  // The ribbon is still drawn (a still frame), and stays out of the accessibility tree.
  await expect(page.locator('canvas.home-ribbon')).toHaveAttribute('aria-hidden', 'true');
});
