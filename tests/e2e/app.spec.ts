import { test as base, expect, type Locator, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
type Harness = {
  launch: string;
  /** 2.41: replaces the recording history with a new one at once, as when Mimic restarts. */
  restartHistory: () => void;
  /** What reached Soundpad to play, seek or pause (TEST_PLAY lines), in order. */
  plays: string[];
  /** The same plus every stop (TEST_STOP lines, 2.35), in order: what Soundpad was told last. */
  soundpad: string[];
  log: () => string;
};
// How long to wait for an upload, reverse or edited copy to be ready in Temp. On a slow CI runner one
// took over 25 s (v2.29.0's tag run), and a failed tag run publishes no EXE, so CI gets more room.
const TEMP_READY = process.env.CI ? 45000 : 25000;
// The dev server's port: 5173 unless E2E_PORT says otherwise (a second checkout running the suite at the same time).
const PORT = process.env.E2E_PORT ?? '5173';
const ORIGIN = `http://127.0.0.1:${PORT}`;
async function startHarness(origin = ORIGIN, count = 4, options: string[] = []) {
  const proc = spawn(
    resolve(
      'companion/harness/bin/Release/net10.0-windows10.0.19041.0/win-x64/SoundpadWeb.Harness.exe',
    ),
    [origin, String(count), ...options],
    { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] },
  );
  const plays: string[] = [];
  const soundpad: string[] = [];
  let output = '';
  const address = await new Promise<{ port: number; token: string }>((resolve, reject) => {
    const timeout = setTimeout(
      () => reject(Error('Test harness startup timed out. Build companion/harness first.')),
      15000,
    );
    proc.on('error', reject);
    proc.on('exit', (code) => {
      if (code) reject(Error('Test harness exited: ' + code));
    });
    proc.stdout.on('data', (chunk) => {
      output = (output + chunk.toString()).slice(-20000);
      const ready = /TEST_READY (\{[^\n]+\})/.exec(output);
      if (ready) {
        clearTimeout(timeout);
        resolve(JSON.parse(ready[1]));
      }
      for (const match of chunk.toString().matchAll(/TEST_(PLAY|STOP) ([^\r\n]+)/g)) {
        if (match[1] === 'PLAY') plays.push(match[2]);
        soundpad.push(match[2]);
      }
    });
    proc.stderr.on('data', (chunk) => {
      if (chunk.toString().includes('Unhandled')) reject(Error('Test harness error.'));
    });
  });
  return {
    launch: `${origin}/#host=${address.port}.${address.token}`,
    plays,
    soundpad,
    // The companion's own console log (it notes refusals and errors), for failure diagnostics.
    log: () =>
      output
        .split(/\r?\n/)
        .filter((l) => l && !l.startsWith('TEST_'))
        .slice(-15)
        .join(' | '),
    /** 2.41: the companion replaces its recording history with a new one at once (as when Mimic restarts). */
    restartHistory: () => proc.stdin.write('restart-history\n'),
    stop: async () => {
      proc.stdin.write('exit\n');
      await new Promise<void>((r) => {
        if (proc.exitCode !== null) r();
        else {
          proc.once('exit', () => r());
          setTimeout(() => {
            if (proc.exitCode === null) proc.kill();
            r();
          }, 5000);
        }
      });
    },
  };
}
const test = base.extend<{ harness: Harness; diagnostics: void; harnessOptions: string[] }>({
  // Extra harness arguments for a group of tests (test.use), e.g. ['--latency-ms', '40'] for a
  // Soundpad that takes 40 ms per request; see the end of companion/harness/Program.cs.
  harnessOptions: [[], { option: true }],
  harness: async ({ harnessOptions }, use) => {
    const harness = await startHarness(undefined, undefined, harnessOptions);
    try {
      await use(harness);
    } finally {
      await harness.stop();
    }
  },
  // When a test fails, add what the page and companion were saying to the failure. CI's traces and
  // logs need a login to read, but failure messages are public, so a flaky run explains itself.
  diagnostics: [
    async ({ page, harness }, use, testInfo) => {
      const consoleErrors: string[] = [];
      page.on('console', (m) => {
        if (m.type() === 'error') consoleErrors.push(m.text().slice(0, 200));
      });
      page.on('pageerror', (e) => consoleErrors.push(e.message.slice(0, 200)));
      await use();
      if (testInfo.status === testInfo.expectedStatus) return;
      const visible = await page
        .evaluate(() => {
          const text = (selector: string) =>
            (document.querySelector(selector) as HTMLElement | null)?.innerText.trim() ?? '';
          return {
            notices: text('.notification-stack'),
            status: text('header'),
            url: location.pathname + location.search,
          };
        })
        .catch(() => null);
      throw Error(
        [
          'Diagnostics for the failure above:',
          `page notices: ${JSON.stringify(visible?.notices ?? '(page closed)')}`,
          `page header: ${JSON.stringify(visible?.status.slice(0, 300) ?? '')}`,
          `console errors: ${JSON.stringify(consoleErrors.slice(-5))}`,
          `companion log: ${harness.log()}`,
        ].join('\n'),
      );
    },
    { auto: true },
  ],
});
async function host(page: Page, harness: Harness) {
  await page.goto(harness.launch);
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeEnabled();
}
async function invite(page: Page) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  const url = await page.locator('#invite').inputValue();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  return url;
}
/**
 * Capture (2.40) opens the selection as a draft; Keep in Temp makes the clip (what "Mimic it" did at once
 * until 2.39). Returns the draft region, which stays open.
 */
async function captureAndKeep(dialog: Locator) {
  await dialog.getByRole('button', { name: 'Capture', exact: true }).click();
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await draft.getByRole('button', { name: 'Keep in Temp', exact: true }).click();
  return draft;
}
async function finishGuestName(page: Page, name = 'Fixture Guest') {
  const dialog = page.getByRole('dialog', { name: /gamer name/i });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Gamer name').fill(name);
  await dialog.getByRole('button', { name: 'Done', exact: true }).click();
}

test('landing page has an honest disconnected state', async ({ page }) => {
  await page.goto('/');
  await expect(
    page.getByRole('heading', { name: 'A soundboard. For the whole party.' }),
  ).toBeVisible();
  await expect(page.getByText('Not connected', { exact: true })).toBeVisible();
  expect(await page.getByRole('button', { name: /Play / }).count()).toBe(0);
});

test('after the website updates, a one-time line links the new version notes', async ({
  page,
  harness,
}) => {
  const version = JSON.parse(await readFile('package.json', 'utf8')).version as string;
  // First visit: nothing to announce.
  await host(page, harness);
  await expect(page.getByText('Soundpad Web updated to')).toHaveCount(0);
  // As if this browser last used an older version.
  await page.evaluate(() => localStorage.setItem('soundpad-web:last-version', '2.0.0'));
  await page.reload();
  const line = page.getByRole('status').filter({ hasText: 'Soundpad Web updated to' });
  await expect(line).toContainText(`v${version}`);
  await expect(line.getByRole('link', { name: "See what's new" })).toHaveAttribute(
    'href',
    'https://github.com/uh-oh-man/soundpad-web/blob/main/CHANGELOG.md',
  );
  await line.getByRole('button', { name: "Dismiss what's new" }).click();
  await expect(line).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('Soundpad Web updated to')).toHaveCount(0);
});

test('the host sees connected guests at the top of the board, above the sounds', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const guests = page
    .locator('.board-heading')
    .getByRole('status')
    .filter({ hasText: 'connected guest' });
  await expect(guests).toHaveText(/0 connected guests/);
  const heading = (await guests.boundingBox())!;
  const firstSound = (await page
    .getByRole('button', { name: /^Play from start: / })
    .first()
    .boundingBox())!;
  expect(heading.y).toBeLessThan(firstSound.y);
  await expect(page.locator('.board-footer')).not.toContainText('connected guest');
  await page.screenshot({ path: 'test-results/guests-top.png' });
});
test('real loopback bridge: nested library, search, correct play command, counter and icons', async ({
  page,
  harness,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await host(page, harness);
  await expect(
    page.getByRole('button', { name: 'Play from start: Laugh', exact: true }),
  ).toHaveCount(2);
  const categories = page.getByRole('navigation', { name: 'Sound categories' }).getByRole('button');
  await expect(categories).toHaveText([
    'Reactions1',
    'Deep cuts1',
    'Empty category0',
    'Favorites2',
  ]);
  await page.getByRole('button', { name: /^Empty category/ }).click();
  await expect(page.getByRole('heading', { name: 'Nothing in this category yet' })).toBeVisible();
  await page.getByRole('textbox', { name: 'Search sounds' }).fill('Airhorn');
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toHaveCount(1);
  await page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.getByRole('button', { name: 'Increase Kick counter' }).click();
  await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
  await page.reload();
  await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
  const png = await page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 16;
    c.height = 16;
    const ctx = c.getContext('2d')!;
    ctx.fillStyle = '#a99af4';
    ctx.fillRect(0, 0, 16, 16);
    return c.toDataURL('image/png').split(',')[1];
  });
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page
    .getByLabel('Choose sound icon')
    .setInputFiles({ name: 'icon.png', mimeType: 'image/png', buffer: Buffer.from(png, 'base64') });
  await page.getByRole('button', { name: 'Save icon', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Play artwork Airhorn', exact: true }).locator('img'),
  ).toBeVisible();
  await page.reload();
  await expect(
    page.getByRole('button', { name: 'Play artwork Airhorn', exact: true }).locator('img'),
  ).toBeVisible();
  await page.screenshot({ path: 'artifacts/screenshots/desktop-fixture.png', fullPage: true });
  expect(errors).toEqual([]);
});
for (const size of [
  { name: 'phone', width: 390, height: 844 },
  { name: 'tablet', width: 820, height: 1180 },
])
  test(`responsive ${size.name} and touch controls`, async ({ page, harness }) => {
    await page.setViewportSize(size);
    await host(page, harness);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    if (size.name === 'phone') {
      await page.getByLabel('Category', { exact: true }).selectOption('5');
      await expect(
        page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
      ).toHaveCount(1);
    }
    await page.screenshot({
      path: `artifacts/screenshots/${size.name}-fixture.png`,
      fullPage: true,
    });
  });
test('host controls persist permissions and reset requires confirmation', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Increase Kick counter' }).click();
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow remote playback').uncheck();
  await page.getByLabel('Allow guest counter changes').check();
  // The checkboxes update at once; give the companion a moment to store the change before reloading
  // (a slow CI runner once reloaded first).
  await page.waitForTimeout(1000);
  await page.reload();
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByLabel('Allow remote playback')).not.toBeChecked();
  await expect(page.getByLabel('Allow guest counter changes')).toBeChecked();
  page.once('dialog', (d) => d.dismiss());
  await page.getByRole('button', { name: 'Reset Kick counter' }).click();
  await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
});
test('production static bundle on a synthetic HTTPS origin reaches only its authenticated loopback bridge', async ({
  page,
  browserName,
}) => {
  // Only the host's browser on the Windows PC talks to the companion, and Safari doesn't run on Windows;
  // Playwright's Windows WebKit also lacks the loopback permission this check relies on.
  test.skip(browserName === 'webkit', 'Hosts never use Safari: it has no Windows version.');
  const harness = await startHarness('https://soundpad-web.test');
  try {
    if (browserName === 'chromium') await page.context().grantPermissions(['local-network-access']);
    await page.route('https://soundpad-web.test/**', async (route) => {
      const path = new URL(route.request().url()).pathname;
      const file = resolve('dist', path === '/' ? 'index.html' : path.slice(1));
      const type = path.endsWith('.js')
        ? 'text/javascript'
        : path.endsWith('.css')
          ? 'text/css'
          : 'text/html';
      await route.fulfill({ body: await readFile(file), contentType: type });
    });
    await host(page, harness);
    expect(await page.evaluate(() => isSecureContext)).toBe(true);
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
  } finally {
    await harness.stop();
  }
});
test('live Nostr/WebRTC: two guests, playback, synchronized counter and invitation revocation', async ({
  browser,
  page,
  harness,
}) => {
  test.skip(
    process.env.LIVE_RELAY !== '1',
    'Optional real-public-relay test. Deterministic protocol tests run separately.',
  );
  test.setTimeout(150000);
  await host(page, harness);
  const url = await invite(page);
  const c1 = await browser.newContext(),
    c2 = await browser.newContext();
  const g1 = await c1.newPage(),
    g2 = await c2.newPage();
  try {
    await Promise.all([g1.goto(url), g2.goto(url)]);
    await Promise.all([finishGuestName(g1, 'First Guest'), finishGuestName(g2, 'Second Guest')]);
    await expect(
      g1.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled({
      timeout: 60000,
    });
    await expect(
      g2.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled({
      timeout: 60000,
    });
    await g1.getByLabel('Your display name').fill('Relay Alice');
    await g1.getByLabel('Your display name').blur();
    await g1.getByLabel('Search sounds').fill('Airhorn');
    await g1.getByLabel('View mode').selectOption('waveforms');
    await expect(g1.getByRole('slider', { name: 'Seek Airhorn', exact: true })).toBeVisible();
    await g1.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
    await g1.getByRole('button', { name: 'Pause playback', exact: true }).click();
    await expect(g1.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
    const remoteWave = g1
      .locator('.sound-entry')
      .getByRole('slider', { name: 'Seek Airhorn', exact: true });
    await remoteWave.press('Home');
    await remoteWave.press('ArrowRight');
    await remoteWave.press('Enter');
    await expect.poll(() => harness.plays).toContain('DoSeekMs(10)');
    await page.getByRole('button', { name: 'Statistics for Airhorn', exact: true }).click();
    await expect(page.getByRole('dialog').getByText('Relay Alice', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Close dialog' }).click();
    await page.getByRole('button', { name: 'Access settings' }).click();
    await page.getByLabel('Allow guest counter changes').check();
    await expect(g1.getByRole('button', { name: 'Increase Kick counter' })).toBeEnabled();
    await g1.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(g2.getByLabel('Kick count', { exact: true })).toHaveText('1');
    await g1.reload();
    await expect(g1.getByLabel('Kick count', { exact: true })).toHaveText('1', { timeout: 60000 });
    await page.reload();
    await expect(page.getByText('2 connected guests', { exact: true })).toBeVisible({
      timeout: 60000,
    });
    await g2.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('2');
    await expect(g1.getByLabel('Kick count', { exact: true })).toHaveText('2');
    const icon = await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = 16;
      canvas.getContext('2d')!.fillRect(0, 0, 16, 16);
      return canvas.toDataURL('image/png').split(',')[1];
    });
    await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
    await page.getByLabel('Choose sound icon').setInputFiles({
      name: 'icon.png',
      mimeType: 'image/png',
      buffer: Buffer.from(icon, 'base64'),
    });
    await page.getByRole('button', { name: 'Save icon', exact: true }).click();
    await expect(
      g1.getByRole('button', { name: 'Play artwork Airhorn', exact: true }).locator('img'),
    ).toBeVisible();
    await expect(
      g2.getByRole('button', { name: 'Play artwork Airhorn', exact: true }).locator('img'),
    ).toBeVisible();
    await page.getByRole('button', { name: 'Access settings' }).click();
    page.once('dialog', (d) => d.accept());
    await page.getByRole('button', { name: 'Replace invite' }).click();
    await expect(g2.getByText('Access revoked', { exact: true })).toBeVisible();
  } finally {
    await c1.close();
    await c2.close();
  }
});
test('live Nostr/WebRTC: independent hosts keep guests and counters isolated', async ({
  browser,
  page,
  harness,
}) => {
  test.skip(process.env.LIVE_RELAY !== '1', 'Optional real-public-relay isolation test.');
  test.setTimeout(150000);
  const second = await startHarness();
  const hc = await browser.newContext(),
    gc1 = await browser.newContext(),
    gc2 = await browser.newContext();
  const hp = await hc.newPage(),
    g1 = await gc1.newPage(),
    g2 = await gc2.newPage();
  try {
    await host(page, harness);
    await host(hp, second);
    const url1 = await invite(page),
      url2 = await invite(hp);
    expect(url1).not.toBe(url2);
    await Promise.all([g1.goto(url1), g2.goto(url2)]);
    await Promise.all([finishGuestName(g1), finishGuestName(g2)]);
    await expect(
      g1.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled({
      timeout: 60000,
    });
    await expect(
      g2.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled({
      timeout: 60000,
    });
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(g1.getByLabel('Kick count', { exact: true })).toHaveText('1');
    await expect(g2.getByLabel('Kick count', { exact: true })).toHaveText('0');
    await g2.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
    await expect.poll(() => second.plays).toContain('DoPlaySound(1)');
    expect(harness.plays).toHaveLength(0);
  } finally {
    await hc.close();
    await gc1.close();
    await gc2.close();
    await second.stop();
  }
});
test('deployed Pages bundle connects to the authenticated Windows loopback companion', async ({
  page,
  browserName,
  browser,
}) => {
  test.skip(!process.env.DEPLOYED_URL, 'Requires an actual deployed website URL.');
  const website = new URL(process.env.DEPLOYED_URL!);
  const harness = await startHarness(website.origin);
  const guestContext = await browser.newContext();
  try {
    if (browserName === 'chromium') await page.context().grantPermissions(['local-network-access']);
    await host(page, { ...harness, launch: website.href + '#' + harness.launch.split('#')[1] });
    const url = await invite(page);
    const guest = await guestContext.newPage();
    await guest.goto(url);
    await finishGuestName(guest);
    await expect(
      guest.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled({
      timeout: 60000,
    });
    await guest.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
    await expect(guest.getByLabel('Kick count', { exact: true })).toHaveText('1');
  } finally {
    await guestContext.close();
    await harness.stop();
  }
});

test('V2: all five views persist and remain usable at phone width', async ({ page, harness }) => {
  await host(page, harness);
  for (const mode of ['icons', 'large', 'list', 'details', 'waveforms']) {
    await page.getByLabel('View mode', { exact: true }).selectOption(mode);
    await expect(
      page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled();
    if (mode === 'waveforms')
      await expect(page.getByRole('slider', { name: 'Seek Airhorn', exact: true })).toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(
      true,
    );
    await page.setViewportSize({ width: 1280, height: 900 });
  }
  await page.getByRole('button', { name: 'My Layout', exact: true }).click();
  await expect(page.getByText(/Saved on this browser/).first()).toBeVisible();
  await page.reload();
  await expect(page.getByLabel('View mode', { exact: true })).toHaveValue('waveforms');
  await page.screenshot({ path: 'artifacts/screenshots/v2-waveforms-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: 'artifacts/screenshots/v2-waveforms-phone.png', fullPage: true });
});

test('V2: real waveform, pause, epoch seek, cue and region metadata, named statistics', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByLabel('Your display name').fill('Fixture Alice');
  await page.getByLabel('Your display name').blur();
  await page.getByLabel('Search sounds').fill('Airhorn');
  await page.getByLabel('View mode').selectOption('waveforms');
  const wave = page
    .locator('.sound-entry')
    .filter({ has: page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }) })
    .locator('.wave-block');
  await expect(wave.getByRole('slider', { name: 'Seek Airhorn', exact: true })).toBeVisible();
  await wave.getByRole('button', { name: 'Cues & regions', exact: true }).click();
  await wave.getByLabel('New cue name').fill('Opening');
  await wave.getByLabel('Cue time seconds', { exact: true }).fill('0.5');
  await wave.getByRole('button', { name: 'Add cue', exact: true }).click();
  await expect(wave.getByLabel('Move cue Opening seconds')).toHaveValue('0.5');
  await wave.getByLabel('Move cue Opening seconds').fill('0.7');
  await wave.getByLabel('Region name', { exact: true }).fill('Middle');
  await wave.getByLabel('Region start seconds', { exact: true }).fill('0.4');
  await wave.getByLabel('Region end seconds', { exact: true }).fill('1.2');
  await wave.getByRole('button', { name: 'Save region', exact: true }).click();
  await expect(wave.getByLabel('Region Middle start seconds')).toHaveValue('0.4');
  await wave.getByLabel('Repeat region Middle', { exact: true }).check();
  const detailSurface = wave.getByRole('slider', { name: 'Seek Airhorn', exact: true });
  await detailSurface.hover();
  await page.waitForTimeout(1100); // wheel zoom starts after resting on the waveform for 1 s
  await page.mouse.wheel(0, -240);
  await expect(
    wave.getByRole('button', { name: 'Fit waveform Airhorn', exact: true }),
  ).toBeVisible();
  await wave.getByRole('button', { name: 'Play cue Opening', exact: true }).click();
  await expect.poll(() => harness.plays).toContain('DoSeekMs(700)');
  await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
  await wave.getByRole('slider', { name: 'Seek Airhorn', exact: true }).press('Home');
  await wave.getByRole('slider', { name: 'Seek Airhorn', exact: true }).press('ArrowRight');
  await wave.getByRole('slider', { name: 'Seek Airhorn', exact: true }).press('Enter');
  await expect.poll(() => harness.plays).toContain('DoSeekMs(10)');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.getByRole('button', { name: 'Statistics for Airhorn', exact: true }).click();
  await expect(page.getByRole('dialog').getByText('Fixture Alice', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.reload();
  await expect(page.getByLabel('View mode')).toHaveValue('waveforms');
  await wave.getByRole('button', { name: 'Cues & regions', exact: true }).click();
  await expect(page.getByLabel('Move cue Opening seconds')).toHaveValue('0.7');
  await expect(page.getByLabel('Repeat region Middle', { exact: true })).toBeChecked();
});

test('V2: nested personal categories, multiple membership, export, end and resume', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Manage layout', exact: true }).click();
  await page.getByLabel('New personal category name').fill('Parent');
  await page.getByRole('button', { name: 'Create category', exact: true }).click();
  await page.getByLabel('Parent category', { exact: true }).selectOption({ label: 'Parent' });
  await page.getByLabel('New personal category name').fill('Child');
  await page.getByRole('button', { name: 'Create category', exact: true }).click();
  await expect(page.getByLabel('Parent of Child')).not.toHaveValue('');
  // The exported text is read from the file the page hands the browser, not from the saved download:
  // on the owner's PC Windows' download check cancels or locks Chromium's saved downloads (EPERM,
  // also for a plain test page; WebKit is not affected), which says nothing about this app (2.37).
  await page.evaluate(() => {
    const page = window as unknown as { exported?: Promise<string> };
    const make = URL.createObjectURL.bind(URL);
    URL.createObjectURL = (blob: Blob | MediaSource) => {
      if (blob instanceof Blob) page.exported = blob.text();
      return make(blob);
    };
  });
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export layout', exact: true }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe('soundpad-personal-layout.json');
  const exported = JSON.parse(
    await page.evaluate(() => (window as unknown as { exported?: Promise<string> }).exported ?? ''),
  );
  expect(exported.folders.map((f: { name: string }) => f.name)).toEqual(['Parent', 'Child']);
  expect(JSON.stringify(exported)).not.toMatch(/token|invite|secret/i);
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.getByRole('button', { name: 'Organize Airhorn', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Parent', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Child', exact: true }).check();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.getByRole('button', { name: 'My Layout', exact: true }).click();
  await page.getByRole('button', { name: /^Child\s*1$/ }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toHaveCount(1);
  const oldInvite = await invite(page);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('button', { name: 'End hosting', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Resume hosting', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Resume hosting', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeEnabled();
  expect(await invite(page)).not.toBe(oldInvite);
  await expect(page.getByRole('button', { name: /^Child\s*1$/ })).toBeVisible();
  await page.getByRole('button', { name: 'Manage layout', exact: true }).click();
  exported.favorites.push('f'.repeat(64));
  await page.getByLabel('Import personal layout').setInputFiles({
    name: 'layout.json',
    mimeType: 'application/json',
    buffer: Buffer.from(JSON.stringify(exported)),
  });
  await expect(page.getByText(/1 referenced sounds are unavailable/)).toBeVisible();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.getByRole('button', { name: 'Host Layout', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeVisible();
});

test('V2: large-photo crop, cancel, failed save retention and remove', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  await host(page, harness);
  const photo = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 2400;
    canvas.height = 1200;
    const context = canvas.getContext('2d')!;
    context.fillStyle = '#f08050';
    context.fillRect(0, 0, 1200, 1200);
    context.fillStyle = '#6050a0';
    context.fillRect(1200, 0, 1200, 1200);
    return canvas.toDataURL('image/jpeg', 0.9).split(',')[1];
  });
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page.getByLabel('Choose sound icon').setInputFiles({
    name: 'wide.jpg',
    mimeType: 'image/jpeg',
    buffer: Buffer.from(photo, 'base64'),
  });
  await expect(page.getByLabel('Crop zoom')).toBeVisible();
  await page.getByLabel('Crop zoom').fill('2');
  await page.getByLabel('Crop horizontal position').fill('100');
  await page.screenshot({ path: 'artifacts/screenshots/v2-crop.png' });
  await page.getByRole('button', { name: 'Save icon', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const image = page
    .getByRole('button', { name: 'Play artwork Airhorn', exact: true })
    .locator('img');
  await expect(image).toBeVisible();
  expect(
    await image.evaluate((img: HTMLImageElement) => [img.naturalWidth, img.naturalHeight]),
  ).toEqual([96, 96]);
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(image).toBeVisible();
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page.getByRole('button', { name: 'Remove custom icon', exact: true }).click();
  await expect(image).toHaveCount(0);
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page.getByLabel('Choose sound icon').setInputFiles({
    name: 'wide.jpg',
    mimeType: 'image/jpeg',
    buffer: Buffer.from(photo, 'base64'),
  });
  await expect(page.getByRole('button', { name: 'Save icon', exact: true })).toBeEnabled();
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.run = async () => {
      throw Error('Fixture: host refused save.');
    };
  });
  await page.getByRole('button', { name: 'Save icon', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Fixture: host refused save.');
  await expect(page.getByLabel('Crop zoom')).toBeVisible();
});

test('V2: 10000-sound library keeps rendering bounded and search responsive', async ({
  page,
}, testInfo) => {
  const large = await startHarness(ORIGIN, 10000);
  try {
    const start = performance.now();
    await host(page, large);
    const loadMs = performance.now() - start;
    await expect(page.locator('.sound-entry')).toHaveCount(120);
    const search = performance.now();
    await page.getByLabel('Search sounds').fill('Benchmark fixture 10000');
    await expect(
      page.getByRole('button', { name: 'Play from start: Benchmark fixture 10000', exact: true }),
    ).toBeVisible();
    await expect(page.locator('.sound-entry')).toHaveCount(1);
    const searchMs = performance.now() - search;
    const controls = await page.evaluate(async () => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      const state = controller.getSnapshot().state;
      const start = performance.now();
      await controller.run('play', {
        soundId: state.library.sounds[0].id,
        libraryVersion: state.library.version,
      });
      const playAckMs = performance.now() - start;
      await new Promise<void>((resolve, reject) => {
        if (controller.getSnapshot().state.playback.epoch) {
          resolve();
          return;
        }
        const timer = setTimeout(() => {
          unsubscribe();
          reject(Error('Playback snapshot timed out.'));
        }, 3000);
        const unsubscribe = controller.subscribe(() => {
          if (controller.getSnapshot().state.playback.epoch) {
            clearTimeout(timer);
            unsubscribe();
            resolve();
          }
        });
      });
      const current = controller.getSnapshot().state.playback;
      await controller.run('pause', { soundId: current.soundId, epoch: current.epoch });
      const seekAckMs = [];
      for (let i = 0; i < 5; i++) {
        const started = performance.now();
        await controller.run('seek', {
          soundId: current.soundId,
          epoch: current.epoch,
          positionMs: i * 100,
        });
        seekAckMs.push(performance.now() - started);
      }
      await controller.run('stop');
      const memory = (
        performance as Performance & {
          memory?: { usedJSHeapSize: number; totalJSHeapSize: number };
        }
      ).memory;
      return {
        playAckMs,
        seekAckMs,
        browserJsHeapBytes: memory?.usedJSHeapSize ?? null,
        browserJsHeapAllocatedBytes: memory?.totalJSHeapSize ?? null,
      };
    });
    await page.getByLabel('Search sounds').fill('');
    await page.getByLabel('View mode').selectOption('waveforms');
    await expect(page.locator('.sound-entry')).toHaveCount(24);
    const metrics = JSON.stringify(
      {
        ...controls,
        sounds: 10000,
        loadMs,
        searchMs,
        renderedGridRows: 120,
        renderedWaveRows: 24,
        scope: `${testInfo.project.name} on this PC, synthetic native library`,
      },
      null,
      2,
    );
    await mkdir('artifacts/v2', { recursive: true });
    await writeFile('artifacts/v2/large-library-metrics.json', metrics);
    await testInfo.attach('large-library-metrics.json', {
      body: metrics,
      contentType: 'application/json',
    });
    expect(searchMs).toBeLessThan(3000);
  } finally {
    await large.stop();
  }
});

test('normal update explains the companion upgrade while preserving legacy playback', async ({
  page,
  harness,
}) => {
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.token) frame.protocol = 1;
      server.send(JSON.stringify(frame));
    });
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state) {
        delete frame.state.protocol;
        delete frame.state.hostId;
      }
      socket.send(JSON.stringify(frame));
    });
  });
  await host(page, harness);
  await expect(page.locator('.release-version')).toHaveText(/^v\d+\.\d+$/);
  await expect(
    page.getByText('Update your Windows companion for V2', { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'Download the updated companion' })).toHaveAttribute(
    'href',
    /releases\/latest\/download\/SoundpadWeb\.exe$/,
  );
  await expect(page.getByLabel('View mode')).toBeDisabled();
  await page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
});

test('session waveforms stay mounted through rapid seeks, view changes and scrolling', async ({
  page,
  harness,
}) => {
  const requests: string[] = [];
  page.on('websocket', (socket) =>
    socket.on('framesent', (frame) => {
      try {
        const data = JSON.parse(String(frame.payload));
        if (data.command?.op === 'waveform') requests.push(data.command.soundId);
      } catch {
        /* Ignore non-JSON transport frames. */
      }
    }),
  );
  await host(page, harness);
  await expect(page.locator('.tile-wave-surface svg[viewBox="0 0 800 64"]')).toHaveCount(4);
  await page.getByLabel('View mode').selectOption('waveforms');
  const before = requests.length;
  await page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
  await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
  const wave = page.locator('.player-wave .wave-surface');
  await expect(wave).toBeVisible();
  await wave.evaluate((el) => el.setAttribute('data-retained', 'yes'));
  const box = (await wave.boundingBox())!;
  for (let i = 0; i < 14; i++)
    await wave.click({ position: { x: Math.max(2, (box.width * ((i % 8) + 1)) / 10), y: 20 } });
  await expect(page.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
  await expect(wave).toHaveAttribute('data-retained', 'yes');
  await expect(wave.locator('svg path')).toHaveAttribute('d', /^M/);
  await page.locator('.sound-entry').last().scrollIntoViewIfNeeded();
  await page.getByLabel('View mode').scrollIntoViewIfNeeded();
  await page.getByLabel('View mode').selectOption('icons');
  await page.getByLabel('View mode').selectOption('waveforms');
  expect(requests.length).toBe(before);
  expect(new Set(requests).size).toBe(requests.length);
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(page.locator('.player-wave .wave-surface svg path')).toHaveAttribute('d', /^M/);
  await page.screenshot({ path: 'artifacts/screenshots/session-waveforms.png', fullPage: true });
});

test('artwork fills the play button and iconless buttons have cached mini waveforms', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const png = await page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = c.height = 96;
    const context = c.getContext('2d')!;
    context.fillStyle = '#bb6622';
    context.fillRect(0, 0, 96, 96);
    return c.toDataURL().split(',')[1];
  });
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page.getByLabel('Choose sound icon').setInputFiles({
    name: 'cover.png',
    mimeType: 'image/png',
    buffer: Buffer.from(png, 'base64'),
  });
  await page.getByRole('button', { name: 'Save icon', exact: true }).click();
  const button = page.getByRole('button', { name: 'Play from start: Airhorn', exact: true });
  const art = page.getByRole('button', { name: 'Play artwork Airhorn', exact: true });
  const image = art.locator('img');
  await expect(image).toBeVisible();
  const a = (await art.boundingBox())!,
    b = (await image.boundingBox())!;
  expect(Math.abs(a.width - b.width)).toBeLessThan(3);
  expect(Math.abs(a.height - b.height)).toBeLessThan(3);
  await button.click({ position: { x: 5, y: 5 } });
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  await expect(page.locator('.tile-wave-surface svg[viewBox="0 0 800 64"]')).toHaveCount(3);
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.screenshot({ path: 'artifacts/screenshots/artwork-buttons.png', fullPage: true });
});

test('first invite asks for a gamer name with only Done and Random, then remembers it', async ({
  page,
}) => {
  const { createIdentity } = await import('../../web/src/network/identity');
  const identity = await createIdentity();
  const invitation = {
    v: identity.v,
    room: identity.room,
    secret: identity.secret,
    publicKey: identity.publicKey,
  };
  await page.routeWebSocket(/wss:\/\//, (socket) => socket.close());
  await page.goto('/#join=' + Buffer.from(JSON.stringify(invitation)).toString('base64url'));
  const dialog = page.getByRole('dialog', { name: /gamer name/i });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button')).toHaveCount(2);
  await expect(dialog.getByRole('button', { name: 'Done', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Random', exact: true }).click();
  await expect(dialog.getByLabel('Gamer name')).toHaveValue(/^[A-Za-z]+\d{3}$/);
  await dialog.getByLabel('Gamer name').fill('Night Owl');
  await page.screenshot({ path: 'artifacts/screenshots/gamer-name.png' });
  await dialog.getByRole('button', { name: 'Done', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByLabel('Your display name')).toHaveValue('Night Owl');
  await page.reload();
  await expect(page.getByLabel('Your display name')).toHaveValue('Night Owl');
  await expect(dialog).toHaveCount(0);
});

test('Spotify has one dedicated sidebar player with track, artist and artwork', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const original = controller.run.bind(controller);
    const c = document.createElement('canvas');
    c.width = c.height = 128;
    const context = c.getContext('2d')!;
    context.fillStyle = '#408360';
    context.fillRect(0, 0, 128, 128);
    controller.run = async (op: string, fields: Record<string, unknown>) =>
      op === 'spotify'
        ? {
            enabled: true,
            available: true,
            title: 'Fixture song',
            artist: 'Fixture artist',
            status: 'Paused',
            positionMs: 20000,
            durationMs: 180000,
            artwork: c.toDataURL(),
            canToggle: true,
            canNext: true,
            canPrevious: true,
          }
        : original(op, fields);
  });
  const player = page.getByRole('region', { name: 'Spotify player' });
  await player.getByLabel('Enable Spotify desktop integration').check();
  await expect(player.getByText('Fixture song', { exact: true })).toBeVisible();
  await expect(player.getByText('Fixture artist', { exact: true })).toBeVisible();
  await expect(player.getByAltText('Spotify album artwork')).toBeVisible();
  const library = page.locator('.sidebar-heading');
  expect((await player.boundingBox())!.y).toBeLessThan((await library.boundingBox())!.y);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(player).toHaveCount(1);
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.screenshot({ path: 'artifacts/screenshots/spotify-sidebar.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(player).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(
    true,
  );
  await page.screenshot({ path: 'artifacts/screenshots/spotify-phone.png', fullPage: true });
});
function sessionWave(seconds = 1) {
  const frames = 48000 * seconds,
    wave = Buffer.alloc(44 + frames * 4);
  wave.write('RIFF', 0);
  wave.writeUInt32LE(wave.length - 8, 4);
  wave.write('WAVEfmt ', 8);
  wave.writeUInt32LE(16, 16);
  wave.writeUInt16LE(1, 20);
  wave.writeUInt16LE(2, 22);
  wave.writeUInt32LE(48000, 24);
  wave.writeUInt32LE(192000, 28);
  wave.writeUInt16LE(4, 32);
  wave.writeUInt16LE(16, 34);
  wave.write('data', 36);
  wave.writeUInt32LE(frames * 4, 40);
  for (let i = 0; i < frames; i++) {
    const sample = Math.round(Math.sin((i * Math.PI * 2 * 440) / 48000) * 12000);
    wave.writeInt16LE(sample, 44 + i * 4);
    wave.writeInt16LE(-sample, 46 + i * 4);
  }
  return wave;
}

test('session uploads use temp, reverse immediately, and End hosting keeps Temp until the companion quits', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Session tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  const sound = page.getByRole('button', { name: 'Play from start: Session tone', exact: true });
  // Decoding the upload in the browser can take over 10 s on a slow CI runner (seen in WebKit).
  await expect(sound).toBeEnabled({ timeout: TEMP_READY });
  await expect(
    page
      .getByRole('navigation', { name: 'Sound categories' })
      .getByRole('button', { name: /^Temp - Soundpad Web.*1$/ }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Reverse and play Session tone', exact: true }).click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(6)');
  await expect(page.locator('.player-track')).toContainText('Session tone (reversed)');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.locator('#temp-manager > summary').click();
  await page.getByRole('button', { name: 'Keep normal permanently', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm permanent save', exact: true }).click();
  await expect(page.locator('.temp-save')).toHaveCount(0);
  await page.getByRole('button', { name: 'Access settings' }).click();
  page.once('dialog', (d) => d.accept());
  await page.getByRole('button', { name: 'End hosting', exact: true }).click();
  await page.getByRole('button', { name: 'Resume hosting', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Session tone', exact: true }),
  ).toBeEnabled();
  // 2.40: End hosting no longer clears Temp (until 2.39 the reversed copy and the Temp category went
  // here); it stays while the companion runs, and only quitting the companion removes it.
  await expect(
    page
      .getByRole('navigation', { name: 'Sound categories' })
      .getByRole('button', { name: /^Temp - Soundpad Web/ }),
  ).toBeVisible();
  // Temp opens as it was left (2.40: each section remembers its open item).
  await expect(page.locator('#temp-manager')).toHaveAttribute('open', '');
  await expect(page.locator('#temp-manager')).toContainText('reversed · ready');
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeEnabled();
});

test('full tile scrubbing and bottom-player precision zoom work at desktop and phone widths', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const button = page.getByRole('button', { name: 'Play from start: Airhorn', exact: true });
  const cue = page.getByRole('slider', { name: 'Cue Airhorn waveform', exact: true });
  await expect(cue.locator('svg[viewBox="0 0 800 64"]')).toBeVisible();
  const geometry = await cue.evaluate((el) => {
    const a = el.getBoundingClientRect(),
      b = el.querySelector('.tile-shade')!.getBoundingClientRect();
    return { w: Math.abs(a.width - b.width), h: Math.abs(a.height - b.height) };
  });
  expect(geometry.w).toBeLessThan(2);
  expect(geometry.h).toBeLessThan(2);
  await button.click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  expect(harness.plays.filter((p) => p.startsWith('DoSeekMs'))).toHaveLength(0);
  await cue.press('Home');
  await cue.press('ArrowRight');
  await cue.press('Enter');
  await expect.poll(() => harness.plays).toContain('DoSeekMs(10)');
  await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
  const footer = page.locator('.player');
  const surface = footer.getByRole('slider', { name: 'Seek Airhorn', exact: true });
  await surface.hover();
  await page.waitForTimeout(1100); // wheel zoom starts after resting on the waveform for 1 s
  await page.mouse.wheel(0, -240);
  await expect(footer.getByRole('button', { name: 'Fit waveform Airhorn' })).toBeVisible();
  await footer.getByRole('button', { name: 'Edit', exact: true }).click();
  await footer.getByLabel('Cue time seconds', { exact: true }).fill('0.123');
  await footer.getByRole('button', { name: 'Seek here: selected position Airhorn' }).click();
  await expect.poll(() => harness.plays).toContain('DoSeekMs(123)');
  await footer.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.screenshot({
    path: 'artifacts/screenshots/v22-precision-desktop.png',
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Expand player waveform' }).click();
  await expect(footer.getByRole('slider', { name: 'Seek Airhorn', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(
    true,
  );
  await page.screenshot({ path: 'artifacts/screenshots/v22-precision-phone.png', fullPage: true });
});

test('live Nostr/WebRTC: compact invites, guest upload, shared organization and guest Spotify panel', async ({
  browser,
  page,
  harness,
}) => {
  test.skip(process.env.LIVE_RELAY !== '1', 'Optional public-relay integration test.');
  test.setTimeout(150000);
  await host(page, harness);
  const url = await invite(page);
  expect(new URL(url).hash.slice('#join='.length)).toHaveLength(44);
  const c1 = await browser.newContext(),
    c2 = await browser.newContext();
  try {
    const g1 = await c1.newPage(),
      g2 = await c2.newPage();
    await Promise.all([g1.goto(url), g2.goto(url)]);
    await Promise.all([finishGuestName(g1, 'Upload Alice'), finishGuestName(g2, 'Layout Bob')]);
    for (const guest of [g1, g2])
      await expect(
        guest.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
      ).toBeEnabled({
        timeout: 60000,
      });
    await expect(g1.getByRole('region', { name: 'Spotify player' })).toBeVisible();
    await expect(g1.getByLabel('Enable Spotify desktop integration')).toHaveCount(0);
    await g1.getByRole('button', { name: 'Favorite Airhorn', exact: true }).click();
    const shared = g2.getByRole('button', { name: /Upload Alice.*Copy into My Layout/ });
    await expect(shared).toBeVisible();
    await shared.click();
    await expect(g2.getByRole('button', { name: 'Favorite Airhorn', exact: true })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await g1
      .getByLabel('Add session audio', { exact: true })
      .setInputFiles({ name: 'Guest tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
    await expect(g1.locator('.notification-stack')).toContainText('Added to Temp for this session');
    // The guest gets Undo for their own upload (2.33): the new source's id reaches them over the relay.
    await expect(
      g1
        .getByRole('status')
        .filter({ hasText: 'Added to Temp for this session' })
        .getByRole('button', { name: 'Undo' }),
    ).toBeVisible();
    await expect(g1.getByLabel('Add session audio', { exact: true })).toBeEnabled();
    for (const participant of [page, g1, g2])
      await expect(
        participant.getByRole('button', { name: 'Play from start: Guest tone', exact: true }),
      ).toBeEnabled();
    await expect(g1.getByRole('button', { name: 'Keep Guest tone permanently' })).toHaveCount(0);
    await g2.getByRole('button', { name: 'Reverse and play Guest tone', exact: true }).click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(6)');
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await page.locator('#temp-manager > summary').click();
    await page.getByRole('button', { name: 'Keep normal permanently', exact: true }).click();
    await page.getByRole('button', { name: 'Confirm permanent save', exact: true }).click();
    await expect(page.locator('.temp-save')).toHaveCount(0);
    await expect(
      g1
        .locator('.sound-entry')
        .filter({
          has: g1.getByRole('button', { name: 'Play from start: Guest tone', exact: true }),
        })
        .locator('.temp-badge'),
    ).toHaveCount(0);
  } finally {
    await c1.close();
    await c2.close();
  }
});

test('Space toggles playback from the media surface but leaves typing and dialogs alone', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const play = page.getByRole('button', { name: 'Play from start: Airhorn', exact: true });
  await play.click();
  await expect(page.getByRole('button', { name: 'Pause playback', exact: true })).toBeEnabled();
  await play.press('Space');
  await expect(page.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
  await play.press('Space');
  await expect(page.getByRole('button', { name: 'Pause playback', exact: true })).toBeEnabled();
  const count = () => harness.plays.filter((c) => c === 'DoTogglePause()').length;
  expect(count()).toBe(2);
  const input = page.getByRole('textbox', { name: 'Search sounds' });
  await input.fill('Air');
  await input.press('Space');
  expect(await input.inputValue()).toBe('Air ');
  expect(count()).toBe(2);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('combobox', { name: 'Theme', exact: true }).press('Space');
  expect(count()).toBe(2);
});

test('dialogs trap focus, restore it on close and block the search shortcut', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const opener = page.getByRole('button', { name: 'Access settings' });
  await opener.click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog.getByRole('button', { name: 'Close dialog' })).toBeFocused();
  // Shift+Tab from the first control wraps to the last one inside the dialog.
  await page.keyboard.press('Shift+Tab');
  await expect(dialog.getByRole('button', { name: /End hosting/ })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Close dialog' })).toBeFocused();
  // "/" must not move focus behind an open dialog.
  await page.keyboard.press('/');
  await expect(page.getByRole('textbox', { name: 'Search sounds' })).not.toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(opener).toBeFocused();
  // Dragging from inside the dialog onto the backdrop must not close it.
  await opener.click();
  const box = (await dialog.boundingBox())!;
  await page.mouse.move(box.x + 20, box.y + 20);
  await page.mouse.down();
  await page.mouse.move(5, 5);
  await page.mouse.up();
  await expect(dialog).toBeVisible();
  await page.mouse.click(5, 5);
  await expect(dialog).toHaveCount(0);
  await page.keyboard.press('/');
  const search = page.getByRole('textbox', { name: 'Search sounds' });
  await expect(search).toBeFocused();
  await search.fill('Air');
  await search.press('Escape');
  await expect(search).toHaveValue('');
});

test('themes persist locally, notifications deduplicate and expire, upload errors remain inline', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('combobox', { name: 'Theme', exact: true }).selectOption('purple');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'purple');
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'purple');
  await page.evaluate(async () => {
    const path = '/src/notifications.ts';
    const { notify } = await import(path);
    notify('Fixture notification');
    notify('Fixture notification');
  });
  await expect(page.locator('.notification-stack .toast')).toHaveCount(1);
  await expect(page.locator('.notification-stack .toast')).toHaveCount(0, { timeout: 6000 });
  await page.getByLabel('Add session audio', { exact: true }).setInputFiles({
    name: 'bad.exe',
    mimeType: 'application/octet-stream',
    buffer: Buffer.from([1, 2, 3]),
  });
  await expect(page.locator('.session-upload [role="alert"]')).toBeVisible();
  await expect(page.locator('.notification-stack .toast')).toHaveCount(0, { timeout: 10000 });
  await expect(page.locator('.session-upload [role="alert"]')).toBeVisible();
  await page.screenshot({ path: 'artifacts/screenshots/v23-purple-errors.png', fullPage: true });
});

test('desktop panels scroll independently and layouts remain usable at narrow widths', async ({
  page,
}) => {
  const harness = await startHarness(ORIGIN, 300);
  try {
    await page.setViewportSize({ width: 1440, height: 900 });
    await host(page, harness);
    const panels = await page.evaluate(() => {
      const main = document.querySelector('main')!,
        side = document.querySelector('aside')!;
      const before = side.scrollTop;
      main.scrollTop = 400;
      return {
        main: main.scrollTop,
        side: side.scrollTop - before,
        body: document.documentElement.scrollHeight <= innerHeight + 1,
      };
    });
    expect(panels.main).toBeGreaterThan(0);
    expect(panels.side).toBe(0);
    expect(panels.body).toBe(true);
    await page.locator('main').evaluate((el) => {
      el.scrollTop = 0;
    });
    for (const [width, height] of [
      [1440, 900],
      [1024, 768],
      [820, 1180],
      [390, 844],
    ]) {
      await page.setViewportSize({ width, height });
      await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1),
      ).toBe(true);
      await page.screenshot({
        path: `artifacts/screenshots/v23-layout-${width}.png`,
        fullPage: false,
      });
    }
  } finally {
    await harness.stop();
  }
});

test('polished tile and Temp review screenshots in both themes and mobile', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await host(page, harness);
  const image = await page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 128;
    c.height = 128;
    const ctx = c.getContext('2d')!;
    const g = ctx.createLinearGradient(0, 0, 128, 128);
    g.addColorStop(0, '#104d99');
    g.addColorStop(1, '#d75670');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, 128, 128);
    ctx.fillStyle = '#fff';
    ctx.font = '64px sans-serif';
    ctx.fillText('♪', 38, 88);
    return c.toDataURL('image/png').split(',')[1];
  });
  await page.getByRole('button', { name: 'Edit icon for Airhorn', exact: true }).click();
  await page.getByLabel('Choose sound icon').setInputFiles({
    name: 'art.png',
    mimeType: 'image/png',
    buffer: Buffer.from(image, 'base64'),
  });
  await page.getByRole('button', { name: 'Save icon', exact: true }).click();
  await page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
  await expect(page.getByRole('slider', { name: 'Seek Airhorn', exact: true })).toBeVisible();
  await page.screenshot({ path: 'artifacts/screenshots/v23-red-blue.png' });
  await page.getByRole('combobox', { name: 'View mode' }).selectOption('large');
  await page.screenshot({ path: 'artifacts/screenshots/v23-large-artwork.png' });
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Session review.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  await expect(
    page.getByRole('button', { name: 'Play from start: Session review', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY }); // decoding an upload can be slow on CI
  await page.getByRole('button', { name: 'Reverse and play Session review', exact: true }).click();
  await expect(page.locator('.player-track')).toContainText('Session review (reversed)');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.locator('#temp-manager > summary').click();
  await expect(page.locator('.temp-source')).toContainText('Session review');
  await page.screenshot({ path: 'artifacts/screenshots/v23-temp.png' });
  await page.getByRole('button', { name: 'Keep normal permanently', exact: true }).click();
  await page.screenshot({ path: 'artifacts/screenshots/v23-permanent-confirmation.png' });
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.locator('#temp-manager > summary').click();
  await page.getByRole('combobox', { name: 'View mode' }).selectOption('icons');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('combobox', { name: 'Theme', exact: true }).selectOption('purple');
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.screenshot({ path: 'artifacts/screenshots/v23-purple.png' });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('.sound-collection').scrollIntoViewIfNeeded();
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
  await page.screenshot({ path: 'artifacts/screenshots/v23-phone.png' });
});

test('Mimic: host listens, everyone sees it, and a selection becomes a playable Temp sound', async ({
  page,
  harness,
  browserName,
}) => {
  await host(page, harness);
  await expect(page.locator('.mimic-badge')).toHaveCount(0);
  const opener = page.getByRole('button', { name: 'Mimic', exact: true });
  await opener.click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await expect(dialog.getByLabel('Mimic source', { exact: true })).toHaveValue('fixture:tone');
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText(
    'Listening to Fixture: tone every 2 s',
  );
  await expect(page.getByRole('button', { name: 'Mimic on' })).toBeVisible();
  await expect(
    page.getByText('Mimic is listening to Fixture: tone every 2 s.', { exact: false }),
  ).toBeVisible();
  await expect(dialog.getByRole('slider', { name: /Mimic buffer/ })).toBeVisible();
  // Let the fixture produce at least one tone burst, then grab it with a quick select.
  await page.waitForTimeout(2600);
  await dialog.getByRole('button', { name: 'Last 3 s' }).click();
  await expect(dialog.locator('.mimic-range')).toContainText('3.0 s');
  // Private preview: plays in this tab only, saves nothing, and Soundpad plays nothing. Preview is
  // host-only and hosts run on Windows, where Safari doesn't exist; headless WebKit often refuses to play.
  if (browserName !== 'webkit') {
    const playsBefore = harness.plays.length;
    await dialog.getByRole('button', { name: 'Preview' }).click();
    await expect(dialog.getByRole('button', { name: 'Stop preview' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Preview' })).toBeVisible({ timeout: 8000 });
    expect(harness.plays.length).toBe(playsBefore);
  }
  await dialog.getByLabel('Clip name').fill('Fixture mimic');
  await dialog.getByRole('button', { name: 'Mimic & play' }).click();
  const sound = page.getByRole('button', { name: 'Play from start: Fixture mimic', exact: true });
  await expect(sound).toBeEnabled();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(5)');
  // The clip can be replayed from the window without hunting for it on the board.
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  await recent.getByRole('button', { name: 'Replay Fixture mimic' }).click();
  await expect.poll(() => harness.plays.filter((p) => p === 'DoPlaySound(5)').length).toBe(2);
  // ...or heard backwards: a reversed variant is prepared and played.
  const before = harness.plays.length;
  await recent.getByRole('button', { name: 'Play Fixture mimic reversed' }).click();
  await expect
    .poll(() => harness.plays.slice(before).some((p) => /^DoPlaySound\((?!5\))\d+\)$/.test(p)))
    .toBe(true);
  await expect(page.locator('.player-track')).toContainText('Fixture mimic (reversed)');
  // Trim saves an exact copy as a new Temp sound and keeps the original.
  await page.keyboard.press('Escape');
  await page.locator('#temp-manager > summary').click();
  const source = page
    .locator('.temp-source')
    .filter({ has: page.getByText('Fixture mimic', { exact: true }) });
  await source.getByRole('button', { name: 'Edit copy', exact: true }).click();
  const trim = page.getByRole('form', { name: 'Edit a copy of Fixture mimic' });
  // Starts as the whole clip: the last 3 s, or a little less if listening began under 3 s earlier
  // (a slow machine takes longer to get here).
  await expect(trim.getByLabel('End (s)')).toHaveValue(/^(2\.\d+|3)$/);
  await trim.getByLabel('Start (s)').fill('0.5');
  await trim.getByLabel('End (s)').fill('2');
  await expect(trim).toContainText(/1\.50 s of (2\.\d\d|3\.00) s/); // clip length as above
  await trim.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture mimic (trimmed)', exact: true }),
  ).toBeEnabled();
  // A voice effect on a copy: Chipmunk plays the same audio 1.5x faster and higher.
  await source.getByRole('button', { name: 'Edit copy', exact: true }).click();
  await trim.getByLabel('Effect').selectOption('chipmunk');
  await expect(trim.getByLabel('Name')).toHaveAttribute('placeholder', 'Fixture mimic (chipmunk)');
  await trim.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture mimic (chipmunk)', exact: true }),
  ).toBeEnabled();
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture mimic', exact: true }),
  ).toBeEnabled();
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  // The recording history view (2.39) starts at the newest 10 s; these drags measure the 30 s view.
  await dialog
    .getByRole('group', { name: 'Waveform zoom' })
    .getByRole('button', { name: '30 s', exact: true })
    .click();
  // Dragging selects an exact range; Clear returns to the live view.
  const canvas = dialog.locator('.mimic-wave canvas');
  // The window scrolls as the selection's actions come and go (2.39 has more in it), so measure the
  // waveform again before each drag rather than reusing where it was.
  const measure = async () => {
    await canvas.scrollIntoViewIfNeeded();
    return (await canvas.boundingBox())!;
  };
  let box = await measure();
  await page.mouse.move(box.x + box.width * 0.8, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.9, box.y + box.height / 2);
  await page.mouse.up();
  await expect(dialog.locator('.mimic-range')).toContainText('3.0 s');
  // Dragging the right edge handle extends the same selection instead of starting a new one.
  await page.mouse.move(box.x + box.width * 0.9, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.95, box.y + box.height / 2);
  await page.mouse.up();
  await expect(dialog.locator('.mimic-range')).toContainText('· 4.5 s');
  // The waveform is keyboard-operable: arrows move the end, Shift+arrows the start.
  await canvas.focus();
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('Shift+ArrowLeft');
  await expect(dialog.locator('.mimic-range')).toContainText('· 4.5 s');
  await page.keyboard.press('Control+ArrowRight');
  await expect(dialog.locator('.mimic-range')).toContainText('· 5.5 s');
  await dialog.getByRole('button', { name: 'Clear' }).click();
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  // Zoomed to the last 10 s, the same drag covers a third of the time, ending at the same point.
  const zoom = dialog.getByRole('group', { name: 'Waveform zoom' });
  await zoom.getByRole('button', { name: '10 s', exact: true }).click();
  await expect(zoom.getByRole('button', { name: '10 s', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  box = await measure();
  await page.mouse.move(box.x + box.width * 0.6, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.9, box.y + box.height / 2);
  await page.mouse.up();
  await expect(dialog.locator('.mimic-range')).toContainText('−4.0 s → −1.0 s · 3.0 s');
  await dialog.getByRole('button', { name: 'Clear' }).click();
  // A selection that starts before the zoomed view widens it to the full buffer.
  await dialog.getByRole('button', { name: 'Last 10 s', exact: true }).click();
  await expect(canvas).toHaveAccessibleName(/showing the last 10 seconds/);
  await canvas.focus();
  await page.keyboard.press('Shift+ArrowLeft');
  await expect(canvas).toHaveAccessibleName(/showing the last 30 seconds/);
  // A new drag in that widened view stays in it until release: 80%–90% of 30 s is −6 s to −3 s.
  box = await measure();
  await page.mouse.move(box.x + box.width * 0.8, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.85, box.y + box.height / 2);
  await page.mouse.move(box.x + box.width * 0.9, box.y + box.height / 2);
  await page.mouse.up();
  await expect(dialog.locator('.mimic-range')).toContainText('−6.0 s → −3.0 s · 3.0 s');
  await dialog.getByRole('button', { name: 'Clear' }).click();
  await zoom.getByRole('button', { name: '30 s', exact: true }).click();
  // Picking a key another app owns is refused even while the hotkey is off (the harness pretends F9 is taken).
  await dialog.getByLabel('Hotkey key', { exact: true }).selectOption('F9');
  await expect(page.getByText('F9 is already used by another app', { exact: false })).toBeVisible();
  await expect(dialog.getByLabel('Hotkey key', { exact: true })).toHaveValue('F6');
  await expect(
    page.getByText('Pick another key next to Hotkey', { exact: false }).first(),
  ).toBeVisible();
  // The F6 hotkey is host-only and reports its state (the harness uses a fixture key, never real F6).
  await dialog.getByLabel('F6 hotkey').check();
  await expect(dialog.getByText('Press F6 in any app to save the last 10 s')).toBeVisible();
  await dialog.getByLabel('F6 saves the last').selectOption('5');
  await expect(dialog.getByText('Press F6 in any app to save the last 5 s')).toBeVisible();
  // When a game already uses F6, the host picks another key; the hotkey moves while it stays on.
  await dialog.getByLabel('Hotkey key', { exact: true }).selectOption('F8');
  await expect(dialog.getByLabel('F8 hotkey')).toBeChecked();
  await expect(dialog.getByText('Press F8 in any app to save the last 5 s')).toBeVisible();
  // A key another app owns (the harness pretends F9 is) is refused, and F8 keeps working.
  await dialog.getByLabel('Hotkey key', { exact: true }).selectOption('F9');
  await expect(page.getByText('F9 is already used by another app', { exact: false })).toBeVisible();
  await expect(dialog.getByLabel('Hotkey key', { exact: true })).toHaveValue('F8');
  await expect(dialog.getByText('Press F8 in any app to save the last 5 s')).toBeVisible();
  await dialog.getByLabel('F8 hotkey').uncheck();
  await expect(dialog.getByLabel('F8 hotkey')).not.toBeChecked();
  await dialog.getByRole('button', { name: 'Stop listening' }).click();
  await expect(page.locator('.mimic-badge')).toHaveCount(0);
  await expect(page.getByText('Mimic stopped listening.')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByLabel('Allow guest Mimic')).not.toBeChecked();
});

test('Mimic transcript: words appear, a click and a Shift-click select them, and they name the clip', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await dialog.getByLabel('Transcribe Mimic audio').check();
  const transcript = dialog.getByRole('list', { name: 'Transcript' });
  // Each sentence is a row of word buttons (2.39): "Fixture" "tone" "N".
  const sentence = transcript
    .getByRole('listitem')
    .filter({ has: page.getByRole('button', { name: 'Fixture', exact: true }) })
    .first();
  await expect(sentence).toBeVisible({ timeout: 15000 });
  const buttons = sentence.getByRole('button');
  await expect(buttons).toHaveCount(3);
  const words = (await buttons.allInnerTexts()).map((w) => w.trim()).join(' ');
  expect(words).toMatch(/^Fixture tone \d+$/);
  // A click selects one word; Shift-click on another selects everything from the first to it.
  await buttons.first().click();
  await expect(buttons.first()).toHaveAttribute('aria-pressed', 'true');
  await expect(buttons.last()).toHaveAttribute('aria-pressed', 'false');
  await expect(dialog.getByLabel('Clip name')).toHaveAttribute('placeholder', 'Fixture');
  await buttons.last().click({ modifiers: ['Shift'] });
  for (const button of await buttons.all())
    await expect(button).toHaveAttribute('aria-pressed', 'true');
  await expect(dialog.getByLabel('Clip name')).toHaveAttribute('placeholder', words);
  await captureAndKeep(dialog);
  await expect(
    page.getByRole('button', { name: `Play from start: ${words}`, exact: true }),
  ).toBeEnabled();
  // Turning transcripts off stops recognising; the words already recognised stay with the history.
  await dialog.getByLabel('Transcribe Mimic audio').uncheck();
  await expect(
    transcript.getByRole('button', { name: 'Fixture', exact: true }).first(),
  ).toBeVisible();
});

test('Mimic keyboard: number keys grab recent audio, Enter captures and keeps, Escape clears before closing', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  // Shortcuts still work right after ticking a checkbox (focus stays on it). Safari doesn't focus a
  // checkbox on click, so focus it the way a keyboard user would.
  await dialog.getByLabel('F6 hotkey').focus();
  await page.keyboard.press('Space');
  await expect(dialog.getByLabel('F6 hotkey')).toBeFocused();
  await page.keyboard.press('5');
  await expect(dialog.locator('.mimic-range')).toContainText('5.0 s');
  const name = dialog.getByLabel('Clip name');
  await expect(name).toBeFocused();
  await name.fill('Key mimic');
  // 2.40: Enter captures a draft (the name field keeps focus), Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await expect(name).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('button', { name: 'Play from start: Key mimic', exact: true }),
  ).toBeEnabled();
  // Escape puts the draft away (it stays in the draft history).
  await name.blur();
  await page.keyboard.press('Escape');
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  await expect(dialog.getByLabel('Draft history')).toBeVisible();
  await page.keyboard.press('3');
  await expect(dialog.locator('.mimic-range')).toContainText('3.0 s');
  await page.keyboard.press('Escape');
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
});

test('Mimic history: a selection and an open draft never carry over to a new recording', async ({
  page,
  harness,
}) => {
  // Review of 2026-11-14 (2.41): frame numbers of one recording mean other audio in the next one.
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  const wave = dialog.getByRole('slider', { name: /^Mimic buffer, showing/ });
  await expect(wave).toBeVisible();
  await dialog.getByRole('button', { name: 'Last 3 s' }).click();
  await expect(dialog.locator('.mimic-range')).toContainText('3.0 s');
  // The companion replaces the recording (as when Mimic restarts) while the selection is open.
  harness.restartHistory();
  await expect(dialog.locator('.mimic-range')).toHaveCount(0, { timeout: 10000 });
  await expect(dialog.getByLabel('Clip name')).toHaveCount(0);
  await expect(wave).toBeVisible();
  // The same with a draft open: it belongs to the old recording and closes with it.
  await dialog.getByRole('button', { name: 'Last 3 s' }).click();
  await dialog.getByRole('button', { name: 'Capture' }).click();
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  harness.restartHistory();
  await expect(dialog.getByRole('region', { name: 'Draft' })).toHaveCount(0, { timeout: 10000 });
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  // The new recording works as usual.
  await dialog.getByRole('button', { name: 'Last 3 s' }).click();
  await dialog.getByRole('button', { name: 'Capture' }).click();
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await expect(page.getByText('Mimic restarted', { exact: false })).toHaveCount(0);
});

/** What historyTimeline answers (2.39), as far as these tests read it. */
type HistoryView = {
  sessionId: string;
  rate: number;
  liveFrame: number;
  fromFrame: number | null;
  available: { startFrame: number; endFrame: number }[];
};

test('Mimic history: go back, select words in older audio, clip them, and see gaps honestly', async ({
  page,
  harness,
}) => {
  test.slow(); // it needs some real history: the fixture source speaks in real time
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await dialog.getByLabel('Transcribe Mimic audio').check();
  // The main waveform starts at the newest 10 s; the overview holds the whole history.
  const wave = dialog.getByRole('slider', { name: /^Mimic buffer, showing/ });
  await expect(wave).toHaveAccessibleName(/showing the last 10 seconds/);
  const overview = dialog.getByRole('slider', { name: /Recording history overview/ });
  await expect(overview).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Live', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  // Let some history build up (the fixture plays a tone every 2 s), then go back in time.
  await page.waitForTimeout(14000);
  await dialog.getByRole('button', { name: 'Back 10 s' }).click();
  await expect(dialog.getByRole('button', { name: 'Go live' })).toBeVisible();
  await expect(wave).toHaveAccessibleName(/ending \d+ s before live/);
  await expect(overview).toHaveAttribute('aria-valuetext', /before live/);
  // Recording carries on while the view stays where it was: live keeps moving away from it.
  const when = dialog.locator('.mimic-when');
  await expect(when).toContainText(/\d+ s behind live/);
  const behind = async () => Number((await when.innerText()).match(/(\d+) s behind live/)?.[1]);
  const before = await behind();
  await expect.poll(behind, { timeout: 8000 }).toBeGreaterThan(before);
  // The older part's words: one sentence selected by its first word and a Shift-click on its last.
  const transcript = dialog.getByRole('list', { name: 'Transcript' });
  const sentence = transcript
    .getByRole('listitem')
    .filter({ has: page.getByRole('button', { name: 'Fixture', exact: true }) })
    .first();
  await expect(sentence).toBeVisible({ timeout: 20000 });
  const buttons = sentence.getByRole('button');
  const words = (await buttons.allInnerTexts()).map((w) => w.trim()).join(' ');
  expect(words).toMatch(/^Fixture tone \d+$/);
  await buttons.first().click();
  await buttons.last().click({ modifiers: ['Shift'] });
  await expect(dialog.getByLabel('Clip name')).toHaveAttribute('placeholder', words);
  // Hovering a word marks it (and the waveform under it).
  await buttons.nth(1).hover();
  await expect(buttons.nth(1)).toHaveClass(/hover/);
  await captureAndKeep(dialog);
  await expect(
    page.getByRole('button', { name: `Play from start: ${words}`, exact: true }),
  ).toBeEnabled();
  // The draft goes; the clip stays in Temp (an open draft keeps its audio in view).
  await dialog.getByRole('button', { name: 'Discard' }).click();
  // Go live returns to the newest audio.
  await dialog.getByRole('button', { name: 'Go live' }).click();
  await expect(dialog.getByRole('button', { name: 'Live', exact: true })).toBeVisible();
  await expect(wave).toHaveAccessibleName(/showing the last 10 seconds/);
  // Stopping keeps the history; when recording resumes, the stop shows as a gap, not as silence.
  await dialog.getByRole('button', { name: 'Stop listening' }).click();
  await expect(when).toContainText('recording stopped');
  await expect(wave).toBeVisible();
  await page.waitForTimeout(2000);
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('list', { name: 'Gaps in the recording' })).toContainText(
    /Stopped · \d+ s missing/,
    { timeout: 15000 },
  );
});

test('Mimic history: a guest sees only what was recorded since they joined, unless the host shares it', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText('Listening to Fixture');
  await page.waitForTimeout(6000); // six seconds the guest was not there for
  await page.keyboard.press('Escape');
  const guest = await joinGuest(page);
  const hostView = (await sendCommand(page, 'historyTimeline')).data as HistoryView;
  expect(hostView.available[0].startFrame).toBe(0);
  const guestView = (await sendCommand(guest, 'historyTimeline')).data as HistoryView;
  expect(guestView.fromFrame).toBeGreaterThan(guestView.rate * 5);
  for (const r of guestView.available)
    expect(r.startFrame).toBeGreaterThanOrEqual(guestView.fromFrame!);
  // The companion refuses audio from before the join, whatever a page sends, and shows none of it.
  const sourceId = guestView.sessionId;
  const early = await sendCommand(guest, 'mimicClip', {
    startFrame: 0,
    endFrame: guestView.rate,
    sourceId,
  });
  expect(early.error).toMatch(/before you joined/);
  const range = { startFrame: 0, endFrame: guestView.fromFrame!, bins: 20, sourceId };
  const theirs = (await sendCommand(guest, 'historyLevels', range)).data as { levels: number[] };
  expect(theirs.levels.every((v) => v === 0)).toBe(true);
  const hosts = (await sendCommand(page, 'historyLevels', range)).data as { levels: number[] };
  expect(hosts.levels.some((v) => v !== 0)).toBe(true);
  // The guest's window says so, and clipping what was said since the join works.
  await guest.getByRole('button', { name: 'Mimic', exact: true }).click();
  const guestDialog = guest.getByRole('dialog', { name: 'Mimic' });
  await expect(guestDialog.getByText('You see what was recorded since you joined')).toBeVisible();
  await guest.waitForTimeout(6000); // "Last 3 s" must lie wholly after the join
  await guestDialog.getByRole('button', { name: 'Last 3 s' }).click();
  await guestDialog.getByLabel('Clip name').fill('Since I joined');
  await captureAndKeep(guestDialog);
  await expect(playButton(page, 'Since I joined')).toBeEnabled({ timeout: TEMP_READY });
  // The host shares the start of the history: exactly that part opens to guests.
  expect(
    (await sendCommand(guest, 'historyShare', { startFrame: 0, endFrame: hostView.rate * 2 }))
      .error,
  ).toBeTruthy();
  expect(
    (await sendCommand(page, 'historyShare', { startFrame: 0, endFrame: hostView.rate * 2 })).error,
  ).toBeUndefined();
  const shared = await sendCommand(guest, 'mimicClip', {
    startFrame: 0,
    endFrame: guestView.rate / 2,
    sourceId,
    name: 'Shared start',
  });
  expect(shared.error).toBeUndefined();
  await expect(playButton(page, 'Shared start')).toBeEnabled({ timeout: TEMP_READY });
  const outside = await sendCommand(guest, 'mimicClip', {
    startFrame: hostView.rate * 3,
    endFrame: hostView.rate * 4,
    sourceId,
  });
  expect(outside.error).toMatch(/before you joined/);
});

/** A draft as draftList answers it (2.40), as far as these tests read it. */
type DraftReply = {
  drafts: { id: string; startFrame: number; endFrame: number; name: string | null }[];
  others: { id: string; ownerName: string; startFrame: number; endFrame: number }[];
};

test('Mimic drafts: Capture opens a draft, an edge drags into the audio around it, preview, keep, and it survives closing Mimic', async ({
  page,
  harness,
  browserName,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await dialog.getByRole('button', { name: 'Last 3 s', exact: true }).click();
  await dialog.getByLabel('Clip name').fill('Draft one');
  const playsBefore = harness.plays.length;
  await dialog.getByRole('button', { name: 'Capture', exact: true }).click();
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await expect(draft.locator('.mimic-range')).toContainText('3.0 s');
  // Capture never imports or plays anything: it is a draft until someone keeps or plays it.
  await expect(playButton(page, 'Draft one')).toHaveCount(0);
  expect(harness.plays.length).toBe(playsBefore);
  // Drag its start edge 2 s to the left, into the audio before it (the view shows 10 s; the draft is its last 3 s).
  const wave = dialog.getByRole('slider', { name: /^Mimic buffer, showing/ });
  const box = (await wave.boundingBox())!;
  const y = box.y + box.height / 2;
  await page.mouse.move(box.x + box.width * 0.7, y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.6, y, { steps: 4 });
  await page.mouse.move(box.x + box.width * 0.5, y, { steps: 4 });
  await page.mouse.up();
  await expect(draft.locator('.mimic-range')).toContainText(/(4\.9|5\.0|5\.1) s/);
  // The companion has the new edges (and pinned the audio they cover).
  await expect
    .poll(async () => {
      const list = (await sendCommand(page, 'draftList')).data as DraftReply;
      const [only] = list.drafts;
      return list.drafts.length === 1
        ? Math.round(((only.endFrame - only.startFrame) / 16000) * 10)
        : -1;
    })
    .toBeGreaterThanOrEqual(49);
  // Private preview: heard in this tab only; Soundpad plays nothing.
  if (browserName !== 'webkit') {
    await draft.getByRole('button', { name: 'Preview' }).click();
    await expect(draft.getByRole('button', { name: 'Stop preview' })).toBeVisible();
    await expect(draft.getByRole('button', { name: 'Preview' })).toBeVisible({ timeout: 10000 });
    expect(harness.plays.length).toBe(playsBefore);
  }
  await draft.getByRole('button', { name: 'Keep in Temp', exact: true }).click();
  await expect(playButton(page, 'Draft one')).toBeEnabled({ timeout: TEMP_READY });
  // Closing Mimic and playing a Soundboard tile keep the draft: Mimic opens on it again.
  await dialog.getByRole('button', { name: 'Close dialog' }).click();
  await playButton(page, 'Airhorn').click();
  await expect.poll(() => harness.plays.length).toBeGreaterThan(playsBefore);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  await expect(draft).toBeVisible();
  await expect(draft.getByLabel('Clip name')).toHaveValue('Draft one');
  await expect(draft.locator('.mimic-range')).toContainText(/(4\.9|5\.0|5\.1) s/);
  // Kept again with the same edges: the same clip, not a second one. Closing it ends the draft.
  await draft.getByRole('button', { name: 'Keep in Temp and close draft' }).click();
  await expect(draft).toHaveCount(0);
  await expect(dialog.getByLabel('Draft history')).toHaveCount(0);
  await expect(playButton(page, 'Draft one')).toHaveCount(1);
  expect(((await sendCommand(page, 'draftList')).data as DraftReply).drafts).toHaveLength(0);
});

test('Mimic drafts: Last N s with a draft open puts the draft away and never moves its edges', async ({
  page,
  harness,
}) => {
  // Review of 2.41: the quick select (like Jump and a search result) picked the new range for the open draft.
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(5600);
  await dialog.getByRole('button', { name: 'Last 3 s', exact: true }).click();
  await dialog.getByRole('button', { name: 'Capture', exact: true }).click();
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await expect(draft.locator('.mimic-range')).toContainText('3.0 s');
  const before = ((await sendCommand(page, 'draftList')).data as DraftReply).drafts;
  expect(before).toHaveLength(1);
  await dialog.getByRole('button', { name: 'Last 5 s', exact: true }).click();
  await expect(draft).toHaveCount(0);
  await expect(dialog.locator('.mimic-range')).toContainText('5.0 s');
  // Well past the moment a moved edge would be saved: the draft is as it was.
  await page.waitForTimeout(1000);
  const after = ((await sendCommand(page, 'draftList')).data as DraftReply).drafts;
  expect(after.map((d) => [d.id, d.startFrame, d.endFrame])).toEqual(
    before.map((d) => [d.id, d.startFrame, d.endFrame]),
  );
});

test('Mimic drafts: a guest previews privately only what was recorded since joining, and the host sees and copies their selection', async ({
  page,
  harness,
  browserName,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText('Listening to Fixture');
  await page.waitForTimeout(5000); // five seconds the guest was not there for
  await page.keyboard.press('Escape');
  const guest = await joinGuest(page);
  const view = (await sendCommand(guest, 'historyTimeline')).data as HistoryView;
  const sourceId = view.sessionId;
  // Before the join: refused, whatever the page sends. After it: a small WAV for this browser only.
  const early = await sendCommand(guest, 'mimicPreview', {
    startFrame: 0,
    endFrame: view.rate,
    sourceId,
  });
  expect(early.error).toMatch(/before you joined/);
  await guest.waitForTimeout(3500);
  const from = view.fromFrame!;
  const mine = await sendCommand(guest, 'mimicPreview', {
    startFrame: from + view.rate / 2,
    endFrame: from + view.rate * 2.5,
    sourceId,
  });
  expect(mine.error).toBeUndefined();
  const wav = Buffer.from((mine.data as { wav: string }).wav, 'base64');
  expect(wav.readUInt32LE(24)).toBeLessThanOrEqual(16000); // the guest's copy is at most 16 kHz
  expect(wav.length).toBeLessThan(2 * 16000 * 2 + 100);
  // In the window: Preview is there for a guest, and plays nothing in the game.
  await guest.getByRole('button', { name: 'Mimic', exact: true }).click();
  const guestDialog = guest.getByRole('dialog', { name: 'Mimic' });
  await guestDialog.getByRole('button', { name: 'Last 3 s', exact: true }).click();
  const plays = harness.plays.length;
  if (browserName !== 'webkit') {
    await guestDialog.getByRole('button', { name: 'Preview' }).click();
    await expect(guestDialog.getByRole('button', { name: 'Preview' })).toBeVisible({
      timeout: 10000,
    });
  }
  // The guest captures; the host is told quietly and sees it as a labelled band to copy.
  await guestDialog.getByLabel('Clip name').fill('Guest draft');
  await guestDialog.getByRole('button', { name: 'Capture', exact: true }).click();
  await expect(guestDialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await expect(page.getByText(/Fixture Guest captured a clip/)).toBeVisible();
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const bands = dialog.getByRole('list', { name: "Other people's selections" });
  await expect(bands).toContainText('Fixture Guest’s selection');
  await bands.getByRole('button', { name: 'Copy Fixture Guest’s selection to my drafts' }).click();
  const hostDraft = dialog.getByRole('region', { name: 'Draft' });
  await expect(hostDraft.getByLabel('Clip name')).toHaveValue('Guest draft');
  // An independent copy: the guest's own draft is untouched when the host discards theirs.
  await hostDraft.getByRole('button', { name: 'Discard' }).click();
  await expect(guestDialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  const theirs = (await sendCommand(guest, 'draftList')).data as DraftReply;
  expect(theirs.drafts.map((d) => d.name)).toEqual(['Guest draft']);
  // The host may let go of a guest's draft (2.40 review: so drafts left behind never hold the recording
  // history); it closes on the guest's side too.
  await bands.getByRole('button', { name: 'Discard Fixture Guest’s selection' }).click();
  await expect(bands).toHaveCount(0);
  await expect(guestDialog.getByRole('region', { name: 'Draft' })).toHaveCount(0);
  expect(((await sendCommand(guest, 'draftList')).data as DraftReply).drafts).toEqual([]);
  expect(harness.plays.length).toBe(plays);
});

test('guest storage: at the Temp budget new clips wait for the host, who allows more; a guest keeps a clip', async ({
  page,
  harness,
}) => {
  test.slow(); // the guest needs some recorded audio of their own
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await expect(page.getByLabel('Allow guest Keep')).toBeChecked(); // on with the usual friend permissions
  const storage = page.getByRole('form', { name: 'Keep and guest storage' });
  await storage.getByLabel('Temp per guest (MB)').fill('1');
  await storage.getByRole('button', { name: 'Save storage settings' }).click();
  await expect(page.getByText('Keep and storage settings saved.')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText('Listening to Fixture');
  await page.keyboard.press('Escape');
  const guest = await joinGuest(page);
  await guest.waitForTimeout(14000);
  const view = (await sendCommand(guest, 'historyTimeline')).data as HistoryView;
  const clip = (offset: number, name: string) =>
    sendCommand(guest, 'mimicClip', {
      startFrame: view.fromFrame! + Math.round(view.rate * offset),
      endFrame: view.fromFrame! + Math.round(view.rate * (offset + 12)),
      sourceId: view.sessionId,
      name,
    });
  // 12 s at the fixture's 16 kHz is about 380 KB: two fit in 1 MB, the third waits.
  expect((await clip(0.5, 'Budget one')).error).toBeUndefined();
  expect((await clip(1, 'Budget two')).error).toBeUndefined();
  expect((await clip(1.5, 'Budget three')).error).toMatch(/Waiting for the host to allow more/);
  // The guest is told where they stand; the host gets a request, like the lobby's.
  await guest.locator('#temp-manager > summary').click();
  await expect(guest.locator('#temp-manager')).toContainText('waiting for the host to allow more');
  const requests = page.getByRole('list', { name: 'Guests waiting for more storage' });
  await expect(requests).toContainText('Fixture Guest asks for more Temp space');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.locator('.guest-usage')).toContainText(/Temp 0\.\d of 1\.0 MB/);
  await page.keyboard.press('Escape');
  await requests.getByRole('button', { name: 'Allow Fixture Guest more' }).click();
  await expect(requests).toHaveCount(0);
  expect((await clip(1.5, 'Budget three')).error).toBeUndefined();
  await expect(playButton(page, 'Budget three')).toBeEnabled({ timeout: TEMP_READY });
  // Keep: into the host's destination (no folder choice for a guest), right from Temp.
  const source = guest
    .locator('.temp-source')
    .filter({ has: guest.getByText('Budget one', { exact: true }) });
  await source.getByRole('button', { name: 'Keep normal permanently' }).click();
  await expect(guest.getByText(/Kept: it is in the host/)).toBeVisible({ timeout: TEMP_READY });
  await expect(guest.locator('#temp-manager')).not.toContainText('Permanent folder');
});

test('live Nostr/WebRTC: guests see Mimic only when allowed and can clip from it', async ({
  browser,
  page,
  harness,
}) => {
  test.skip(process.env.LIVE_RELAY !== '1', 'Optional public-relay integration test.');
  test.setTimeout(150000);
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  await page.getByRole('button', { name: 'Start listening' }).click();
  await page.getByLabel('Transcribe Mimic audio').check();
  await page.keyboard.press('Escape');
  const url = await invite(page);
  const context = await browser.newContext();
  try {
    const guest = await context.newPage();
    await guest.goto(url);
    await finishGuestName(guest, 'Mimic Alice');
    await expect(
      guest.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
    ).toBeEnabled({
      timeout: 60000,
    });
    await expect(guest.getByRole('button', { name: 'Mimic on' })).toBeDisabled();
    // A guest who joins while Mimic is on is told right away.
    await expect(
      guest.getByText("The host's Mimic is listening to Fixture: tone every 2 s", { exact: false }),
    ).toBeVisible();
    await expect(guest.getByRole('button', { name: 'Mimic', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Access settings' }).click();
    await page.getByLabel('Allow guest Mimic').check();
    await page.keyboard.press('Escape');
    await guest.getByRole('button', { name: 'Mimic', exact: true }).click();
    const dialog = guest.getByRole('dialog', { name: 'Mimic' });
    await expect(dialog.getByLabel('Mimic source', { exact: true })).toHaveCount(0);
    await expect(dialog.getByLabel('Transcribe Mimic audio')).toHaveCount(0);
    await expect(dialog.getByLabel('F6 hotkey')).toHaveCount(0);
    await expect(dialog.getByText('From Voice Replay Studio')).toHaveCount(0); // host only
    // The host's transcript reaches the guest as text; clicking a line selects that audio.
    await expect(
      dialog
        .getByRole('list', { name: 'Transcript' })
        .getByRole('button', { name: 'Fixture', exact: true }) // one button per word (2.39)
        .first(),
    ).toBeVisible({ timeout: 20000 });
    await dialog.getByRole('button', { name: 'Last 3 s' }).click();
    // 2.40: a guest can preview privately (in their own browser, never in the game).
    await expect(dialog.getByRole('button', { name: 'Preview' })).toBeVisible();
    await dialog.getByLabel('Clip name').fill('Guest mimic');
    await captureAndKeep(dialog);
    await dialog.getByRole('button', { name: 'Discard' }).click();
    await expect(
      page.getByRole('button', { name: 'Play from start: Guest mimic', exact: true }),
    ).toBeEnabled();
    // Everyone hears about a new clip, and can replay it from the shared list.
    await expect(page.getByText('Mimic Alice mimicked \u201cGuest mimic\u201d')).toBeVisible();
    // Making a clip must not trip the host's per-guest rate limit (2.15.6: Temp refreshes once, not 9 times).
    await guest.waitForTimeout(1500);
    await expect(guest.getByText('Too many requests', { exact: false })).toHaveCount(0);
    // Guests with upload permission can play their clip with a quick effect (2.18.1).
    await dialog
      .getByRole('region', { name: 'Just mimicked' })
      .getByLabel('Play Guest mimic with an effect')
      .selectOption('echo');
    await expect(
      page.getByRole('button', { name: 'Play from start: Guest mimic (echo)', exact: true }),
    ).toBeEnabled({ timeout: 30000 });
    await expect(
      dialog
        .getByRole('region', { name: 'Just mimicked' })
        .getByRole('button', { name: 'Replay Guest mimic' }),
    ).toBeVisible();
    // A guest can Undo their own clip from its notice (2.32); the host sees it leave Temp.
    await dialog.getByRole('button', { name: 'Last 3 s' }).click();
    await dialog.getByLabel('Clip name', { exact: true }).fill('Guest oops');
    await captureAndKeep(dialog);
    const oops = page.getByRole('button', { name: 'Play from start: Guest oops', exact: true });
    await expect(oops).toBeEnabled({ timeout: 30000 });
    // The echo copy above may still be playing; Undo never stops another sound, so stop it first.
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await guest
      .getByRole('status')
      .filter({ hasText: 'Mimic Alice mimicked “Guest oops”' })
      .getByRole('button', { name: 'Undo' })
      .click();
    await expect(guest.getByText('Clip removed from Temp.')).toBeVisible({ timeout: 30000 });
    await expect(oops).toHaveCount(0, { timeout: 30000 });
    // The host's own clip notices offer the guest no Undo.
    await expect(
      page.getByRole('button', { name: 'Play from start: Guest mimic', exact: true }),
    ).toBeEnabled();
    // Guests only exist over real relays, so the guest view's accessibility is checked here.
    const { violations } = await new AxeBuilder({ page: guest }).analyze();
    expect(
      violations
        .filter((v) => v.impact === 'serious' || v.impact === 'critical')
        .map((v) => `${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`),
    ).toEqual([]);
  } finally {
    await context.close();
  }
});

test('a guest whose browser has no WebRTC is told why at once', async ({
  page,
  harness,
  browser,
  browserName,
}) => {
  // On GitHub's WebKit runner the guest's name prompt never appeared, in either attempt; locally it
  // passes. The check is browser-independent and Chromium covers it.
  test.skip(browserName === 'webkit', "Fails only on CI's WebKit; Chromium covers it.");
  await host(page, harness);
  const url = await invite(page);
  const context = await browser.newContext();
  try {
    // As if WebRTC were turned off by a setting or a privacy extension. A returning guest who already
    // chose a name goes straight to joining, which is where the check runs (the name prompt is covered
    // elsewhere). On a slow CI runner the name prompt once took over 30 s to appear in both attempts
    // (v2.29.0's tag run), so this test no longer waits for it.
    await context.addInitScript(() => {
      delete (window as { RTCPeerConnection?: unknown }).RTCPeerConnection;
      localStorage.setItem('soundpad-name-confirmed', '1');
    });
    // Since 2.37 such a guest tries the relay instead; with the relays unreachable as well, the
    // WebRTC message stays (and this test never depends on public relays).
    await context.routeWebSocket(/wss:\/\//, (socket) => socket.close());
    const guest = await context.newPage();
    await guest.goto(url);
    // A fresh browser context loads the whole app, which a slow CI runner can take a while to do (the
    // other guest tests allow 60 s too). Once loaded, the message appears at once.
    try {
      await expect(guest.getByText('WebRTC is turned off or not supported')).toBeVisible({
        timeout: 60000,
      });
    } catch (e) {
      // Flaky on CI only (3 of 7 runs before 2.34.1): say what the guest page showed instead.
      const shown = await guest
        .evaluate(() => ({
          url: location.href.replace(/#.*/, '#…'),
          ready: document.readyState,
          body: document.body.innerText.slice(0, 400),
        }))
        .catch((err: Error) => ({ error: err.message }));
      throw Error(`${(e as Error).message}\nGuest page: ${JSON.stringify(shown)}`, { cause: e });
    }
    await expect(guest.getByText('WebRTC blocked', { exact: true })).toBeVisible(); // header
  } finally {
    await context.close();
  }
});

test('scrolling over a waveform scrolls the page until the pointer rests there for a second', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByLabel('Search sounds').fill('Airhorn');
  await page.getByLabel('View mode').selectOption('waveforms');
  const wave = page
    .locator('.sound-entry')
    .filter({ has: page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }) })
    .locator('.wave-block');
  const surface = wave.getByRole('slider', { name: 'Seek Airhorn', exact: true });
  await expect(surface).toBeVisible();
  const fit = wave.getByRole('button', { name: 'Fit waveform Airhorn', exact: true });
  // Just arrived: the wheel belongs to the page, not the waveform.
  await surface.hover();
  await page.mouse.wheel(0, -240);
  await page.waitForTimeout(300);
  await expect(fit).toHaveCount(0);
  // Each wheel that went to the page restarts the wait; after a quiet second the wheel zooms.
  await page.waitForTimeout(1100);
  await page.mouse.wheel(0, -240);
  await expect(fit).toBeVisible();
});

test('wheel zoom works when the waveform data arrives late', async ({ page, harness }) => {
  // Hold back waveform requests, as a busy PC would, so the waveform surface mounts after the zoom
  // logic first ran. Before 2.14.4 the wheel listener was never attached in that case.
  await page.routeWebSocket(/.*/, (ws) => {
    const server = ws.connectToServer();
    ws.onMessage((message) => {
      if (String(message).includes('"waveform"')) setTimeout(() => server.send(message), 1500);
      else server.send(message);
    });
    server.onMessage((message) => ws.send(message));
  });
  await host(page, harness);
  await page.getByLabel('Search sounds').fill('Airhorn');
  await page.getByLabel('View mode').selectOption('waveforms');
  const wave = page
    .locator('.sound-entry')
    .filter({ has: page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }) })
    .locator('.wave-block');
  const surface = wave.getByRole('slider', { name: 'Seek Airhorn', exact: true });
  await expect(surface).toBeVisible();
  await surface.hover();
  await page.waitForTimeout(1100); // wheel zoom starts after resting on the waveform for 1 s
  await page.mouse.wheel(0, -240);
  await expect(
    wave.getByRole('button', { name: 'Fit waveform Airhorn', exact: true }),
  ).toBeVisible();
});

test('Temp offers to remove duplicate Temp categories that old companions created', async ({
  page,
  harness,
}) => {
  // The harness's mock library has none, so mark the Temp list as if the companion had found some.
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => server.send(message));
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.type === 'ack' && frame.data?.manager?.issues)
        frame.data.manager.issues.push({
          id: 'manager:category-duplicates',
          assetId: null,
          code: 'category-duplicates',
          message: 'Soundpad has 1507 copies of the Temp category with a garbled name.',
          severity: 'warning',
        });
      socket.send(JSON.stringify(frame));
    });
  });
  await host(page, harness);
  await page.locator('#temp-manager > summary').click();
  await expect(page.getByText('Soundpad has 1507 copies')).toBeVisible();
  page.once('dialog', (dialog) => void dialog.accept());
  await page.getByRole('button', { name: 'Remove duplicate categories' }).click();
  // The companion removes only empty garbled copies of its own category; the mock has none.
  await expect(page.getByText('Removed 0 duplicate Temp categories from Soundpad.')).toBeVisible();
});

test('Edit copy: dragging on the waveform sets Start and End, and the host can preview', async ({
  page,
  harness,
  browserName,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Drag tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  await expect(
    page.getByRole('button', { name: 'Play from start: Drag tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Drag tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Drag tone' });
  const wave = form.getByTestId('trim-wave');
  await expect(wave.locator('.trim-wave-path')).toHaveAttribute('d', /M/, { timeout: 15000 });
  const box = (await wave.boundingBox())!;
  const at = (f: number) => [box.x + box.width * f, box.y + box.height / 2] as const;
  // Drag across the middle half of the 1-second tone (right to left works too).
  await page.mouse.move(...at(0.75));
  await page.mouse.down();
  await page.mouse.move(...at(0.5), { steps: 3 });
  await page.mouse.move(...at(0.25), { steps: 3 });
  await page.mouse.up();
  const start = form.getByLabel('Start (s)'),
    end = form.getByLabel('End (s)');
  await expect.poll(async () => Number(await start.inputValue())).toBeCloseTo(0.25, 1);
  await expect.poll(async () => Number(await end.inputValue())).toBeCloseTo(0.75, 1);
  // Grab the end edge and pull it past the right side: it stops at the sound's end.
  await page.mouse.move(...at(0.75));
  await page.mouse.down();
  await page.mouse.move(box.x + box.width + 40, box.y + box.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect(end).toHaveValue('1');
  await expect.poll(async () => Number(await start.inputValue())).toBeCloseTo(0.25, 1);
  // The typed boxes still move the drawn range.
  await start.fill('0.5');
  await expect(wave.locator('.trim-wave-edge').first()).toHaveAttribute('x1', '400');
  // The waveform also takes the keyboard, like Mimic's (2.31): Shift+Left moves the start, Left the
  // end, Ctrl for whole seconds.
  await wave.focus();
  await page.keyboard.press('Shift+ArrowLeft');
  await expect(start).toHaveValue('0.4');
  await page.keyboard.press('ArrowLeft');
  await expect(end).toHaveValue('0.9');
  await page.keyboard.press('Control+Shift+ArrowLeft');
  await expect(start).toHaveValue('0');
  await expect(wave).toHaveAttribute('aria-valuetext', 'From 0.00 s to 0.90 s of 1.00 s');
  await start.fill('0.5');
  await end.fill('1');
  // Preview (2.20) plays the copy in this tab only: Soundpad plays nothing and Temp gains nothing.
  // Hosts run on Windows, where Safari doesn't exist; headless WebKit often refuses to play.
  if (browserName !== 'webkit') {
    const playsBefore = harness.plays.length;
    const sources = await page.locator('.temp-source').count();
    await form.getByRole('button', { name: 'Preview' }).click();
    await expect(form.getByRole('button', { name: 'Stop preview' })).toBeVisible();
    await expect(form.getByRole('button', { name: 'Preview' })).toBeVisible({ timeout: 8000 });
    expect(harness.plays.length).toBe(playsBefore);
    await expect(page.locator('.temp-source')).toHaveCount(sources);
  }
  await form.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', { name: /^Play from start: Drag tone \(/ }).first(),
  ).toBeEnabled({ timeout: TEMP_READY });
});

test('Edit copy: Zoom to selection shows just the chosen part for fine dragging', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Zoom tone.wav', mimeType: 'audio/wav', buffer: sessionWave(10) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Zoom tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Zoom tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Zoom tone' });
  const wave = form.getByTestId('trim-wave');
  await expect(wave.locator('.trim-wave-path')).toHaveAttribute('d', /M/, { timeout: 15000 });
  // The whole sound is chosen at first, so there is nothing to zoom to yet.
  await expect(form.getByRole('button', { name: 'Zoom to selection' })).toHaveCount(0);
  await form.getByLabel('Start (s)').fill('4');
  await form.getByLabel('End (s)').fill('6');
  const zoom = form.getByRole('button', { name: 'Zoom to selection' });
  await zoom.click();
  // 4–6 s with room either side: 3.7–6.3 s fills the strip.
  await expect(form.getByText('Showing 3.70–6.30 s of 10.00 s')).toBeVisible();
  await expect(form.getByRole('button', { name: 'Show whole sound' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  // Dragging the start edge across half the strip now moves it by only 1.3 s.
  const box = (await wave.boundingBox())!;
  const xAt = (s: number) => box.x + ((s - 3.7) / 2.6) * box.width;
  await page.mouse.move(xAt(4), box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(xAt(5), box.y + box.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect
    .poll(async () => Number(await form.getByLabel('Start (s)').inputValue()))
    .toBeCloseTo(5, 1);
  await expect(form.getByLabel('End (s)')).toHaveValue('6');
  await form.getByRole('button', { name: 'Show whole sound' }).click();
  await expect(form.getByText('Drag to choose the part to copy', { exact: false })).toBeVisible();
});

test('Edit copy: Loop repeats the preview and replays it when the cut changes', async ({
  page,
  harness,
  browserName,
}) => {
  // Preview is host-only and hosts run on Windows, where Safari doesn't exist; headless WebKit often
  // refuses to play.
  test.skip(browserName === 'webkit', 'Preview playback is checked in Chromium');
  // Record each preview the page starts, and whether it repeats.
  await page.addInitScript(() => {
    const w = window as unknown as { previews: { loop: boolean; at: number }[] };
    w.previews = [];
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function () {
      w.previews.push({ loop: this.loop, at: performance.now() });
      return play.call(this);
    };
  });
  const previews = () =>
    page.evaluate(() => (window as unknown as { previews: { loop: boolean }[] }).previews);
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Loop tone.wav', mimeType: 'audio/wav', buffer: sessionWave(3) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Loop tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Loop tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Loop tone' });
  await form.getByLabel('Loop').check();
  const playsBefore = harness.plays.length;
  await form.getByRole('button', { name: 'Preview' }).click();
  await expect.poll(async () => (await previews()).length).toBe(1);
  expect((await previews())[0].loop).toBe(true);
  // A looping 3-second preview is still going well after it would have ended once.
  await page.waitForTimeout(3500);
  await expect(form.getByRole('button', { name: 'Stop preview' })).toBeVisible();
  // Changing the cut replays the new one (after a short pause in typing), still looping.
  await form.getByLabel('Start (s)').fill('1');
  await expect.poll(async () => (await previews()).length, { timeout: 8000 }).toBe(2);
  expect((await previews())[1].loop).toBe(true);
  // Quick changes while a slow preview (same pitch at a slow speed) is being made wait their turn instead
  // of colliding with it (2.29.3: "Another copy is being made" stopped the loop).
  await form.getByRole('combobox').selectOption('speed');
  await form.getByLabel('Keep pitch').check();
  for (const speed of ['0.3', '0.4', '0.5', '0.6']) {
    await form.getByLabel('Exact speed (×)').fill(speed);
    await page.waitForTimeout(450);
  }
  await expect.poll(async () => (await previews()).length, { timeout: 15000 }).toBeGreaterThan(2);
  await page.waitForTimeout(1500);
  await expect(page.getByText('Another copy is being made', { exact: false })).toHaveCount(0);
  await expect(form.getByRole('button', { name: 'Stop preview' })).toBeVisible();
  await form.getByRole('combobox').selectOption('none');
  // Unticking Loop lets the current one finish and stop by itself; Soundpad never played anything.
  await form.getByLabel('Loop').uncheck();
  await expect(form.getByRole('button', { name: 'Preview' })).toBeVisible({ timeout: 8000 });
  expect(harness.plays.length).toBe(playsBefore);
  // Without Loop, changing the cut doesn't start anything.
  const started = (await previews()).length;
  await form.getByLabel('End (s)').fill('2.5');
  await page.waitForTimeout(1000);
  expect((await previews()).length).toBe(started);
});

test('Temp: a search appears once there are more than six sources', async ({ page, harness }) => {
  test.setTimeout(120000);
  await host(page, harness);
  const names = ['Alpha', 'Bravo', 'Charlie', 'Delta', 'Echo', 'Foxtrot', 'Golf'];
  // Different lengths, so no two uploads are the same audio (identical files are kept once).
  for (const [i, name] of names.entries()) {
    await page
      .getByLabel('Add session audio', { exact: true })
      .setInputFiles({ name: `${name}.wav`, mimeType: 'audio/wav', buffer: sessionWave(i + 1) });
    await expect(
      page.getByRole('button', { name: `Play from start: ${name}`, exact: true }),
    ).toBeEnabled({ timeout: TEMP_READY });
    if (i === 5) {
      // Six sources: no search yet.
      await page.locator('#temp-manager > summary').click();
      await expect(page.locator('.temp-source')).toHaveCount(6);
      await expect(page.getByRole('searchbox', { name: 'Search Temp' })).toHaveCount(0);
    }
  }
  const search = page.getByRole('searchbox', { name: 'Search Temp' });
  await expect(search).toBeVisible();
  await expect(page.locator('.temp-source')).toHaveCount(7);
  await search.fill('fox');
  await expect(page.locator('.temp-source')).toHaveCount(1);
  await expect(page.locator('.temp-source')).toContainText('Foxtrot');
  // An open Edit copy stays listed whatever the search says.
  await page
    .locator('.temp-source', { hasText: 'Foxtrot' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  await search.fill('nothing like this');
  await expect(page.getByText('No sources match', { exact: false })).toHaveCount(0);
  await expect(page.getByRole('form', { name: 'Edit a copy of Foxtrot' })).toBeVisible();
  await page
    .getByRole('form', { name: 'Edit a copy of Foxtrot' })
    .getByRole('button', { name: 'Cancel' })
    .click();
  await expect(page.getByText('No sources match “nothing like this”.')).toBeVisible();
  // Who added it counts too.
  await search.fill('host');
  await expect(page.locator('.temp-source')).toHaveCount(7);
});

test('Edit copy: Save & play makes the copy and plays it through Soundpad', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Play me.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Play me', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Play me' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Play me' });
  await form.getByLabel('Start (s)').fill('0.5');
  await form.getByRole('combobox').selectOption('chipmunk');
  const playsBefore = harness.plays.length;
  await form.getByRole('button', { name: 'Save & play' }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Play me (chipmunk)', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await expect
    .poll(() => harness.plays.slice(playsBefore).some((p) => p.startsWith('DoPlaySound(')))
    .toBe(true);
  await expect(page.getByText('Copy added to Temp and playing.', { exact: false })).toBeVisible();
  await expect(form).toHaveCount(0);
});

test('Edit copy: Undo on the notice takes the new copy back out (host)', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Undo me.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Undo me', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Undo me' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Undo me' });
  await form.getByRole('combobox').selectOption('echo');
  await form.getByRole('button', { name: 'Save copy' }).click();
  const copy = page.getByRole('button', { name: 'Play from start: Undo me (echo)', exact: true });
  await expect(copy).toBeEnabled({ timeout: TEMP_READY });
  const notice = page.getByRole('status').filter({ hasText: 'Copy added to Temp.' });
  await notice.getByRole('button', { name: 'Undo' }).click();
  await expect(page.getByText('Copy removed from Temp.')).toBeVisible();
  await expect(copy).toHaveCount(0, { timeout: TEMP_READY });
  // The original is untouched.
  await expect(
    page.getByRole('button', { name: 'Play from start: Undo me', exact: true }),
  ).toBeEnabled();
});

test('Edit copy: Undo right after Save & play stops the copy, then removes it', async ({
  page,
  harness,
}) => {
  // The companion never removes temporary audio that is playing, so Undo stops it first.
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Undo me.wav', mimeType: 'audio/wav', buffer: sessionWave(3) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Undo me', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Undo me' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Undo me' });
  await form.getByRole('combobox').selectOption('echo');
  await form.getByRole('button', { name: 'Save & play' }).click();
  const notice = page.getByRole('status').filter({ hasText: 'Copy added to Temp and playing.' });
  await notice.getByRole('button', { name: 'Undo' }).click();
  await expect(page.getByText('Copy removed from Temp.')).toBeVisible({ timeout: 15000 });
  await expect(
    page.getByRole('button', { name: 'Play from start: Undo me (echo)', exact: true }),
  ).toHaveCount(0, { timeout: TEMP_READY });
  await expect(page.getByText('Stop playback before removing', { exact: false })).toHaveCount(0);
});

test('Undo on the upload notice takes the wrong file straight back out', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Wrong file.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
  const sound = page.getByRole('button', { name: 'Play from start: Wrong file', exact: true });
  await expect(sound).toBeEnabled({ timeout: TEMP_READY });
  await page
    .getByRole('status')
    .filter({ hasText: 'Added to Temp for this session.' })
    .getByRole('button', { name: 'Undo' })
    .click();
  await expect(page.getByText('Upload removed from Temp.')).toBeVisible({ timeout: 15000 });
  await expect(sound).toHaveCount(0, { timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await expect(page.locator('.temp-source', { hasText: 'Wrong file' })).toHaveCount(0);
});

test('a notice with Undo stays while the pointer is on it, then leaves', async ({
  page,
  harness,
}) => {
  // 2.34.2: its button must not vanish as someone reaches it (it normally lasts 10 s).
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Hold me.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
  const notice = page.getByRole('status').filter({ hasText: 'Added to Temp for this session.' });
  await expect(notice.getByRole('button', { name: 'Undo' })).toBeVisible({ timeout: TEMP_READY });
  await notice.hover();
  await page.waitForTimeout(12000);
  await expect(notice).toBeVisible();
  await page.mouse.move(5, 5);
  await expect(notice).toHaveCount(0, { timeout: 5000 });
});

test('Edit copy can make a copy at any speed', async ({ page, harness }) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Speed tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  await expect(
    page.getByRole('button', { name: 'Play from start: Speed tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Speed tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Speed tone' });
  await form.getByRole('combobox').selectOption('speed');
  const speed = form.getByRole('group', { name: 'Speed' });
  await speed.getByLabel('Exact speed (×)').fill('0.5');
  await expect(speed).toContainText('0.5×: the copy lasts 2.00 s');
  await expect(form.getByLabel('Name')).toHaveAttribute('placeholder', 'Speed tone (0.5x speed)');
  // The slider is logarithmic: all the way left is 20 times slower, all the way right 20 times faster.
  await speed.getByRole('slider').fill('-100');
  await expect(speed.getByLabel('Exact speed (×)')).toHaveValue('0.05');
  await speed.getByRole('slider').fill('100');
  await expect(speed.getByLabel('Exact speed (×)')).toHaveValue('20');
  await speed.getByLabel('Exact speed (×)').fill('0.5');
  await form.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Speed tone (0.5x speed)', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  // Keep pitch (2.17): same speed, voices keep their pitch; the copy's name says so.
  await page
    .locator('.temp-source', { hasText: 'Speed tone' })
    .first()
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const again = page.getByRole('form', { name: 'Edit a copy of Speed tone' });
  await again.getByRole('combobox').selectOption('speed');
  await again.getByRole('group', { name: 'Speed' }).getByLabel('Exact speed (×)').fill('0.5');
  await again.getByLabel('Keep pitch').check();
  await expect(again.getByRole('group', { name: 'Speed' })).toContainText('Same pitch.');
  await expect(again.getByLabel('Name')).toHaveAttribute(
    'placeholder',
    'Speed tone (0.5x speed, same pitch)',
  );
  await again.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', {
      name: 'Play from start: Speed tone (0.5x speed, same pitch)',
      exact: true,
    }),
  ).toBeEnabled({ timeout: TEMP_READY });
});

test('the host can clear all temporary audio mid-session and keep hosting', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Clear me.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  const sound = page.getByRole('button', { name: 'Play from start: Clear me', exact: true });
  await expect(sound).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  page.once('dialog', (dialog) => void dialog.accept());
  await page.getByRole('button', { name: 'Clear all temporary audio' }).click();
  await expect(
    page.getByText(/Cleared 1 temporary audio file; Soundpad's Temp category is gone too\./),
  ).toBeVisible();
  await expect(sound).toHaveCount(0);
  // Still hosting: the original sounds play.
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeEnabled();
});

test('Temp keeps the native original distinct from its generated reverse', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Reverse and play Airhorn', exact: true }).click();
  await expect(page.locator('.player-track')).toContainText('Airhorn (reversed)');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.locator('#temp-manager > summary').click();
  await expect(page.getByRole('button', { name: 'Play normal', exact: true })).toBeEnabled();
  await expect(
    page.getByRole('button', { name: 'Keep normal permanently', exact: true }),
  ).toHaveCount(0);
  await expect(
    page.getByRole('button', { name: 'Keep reversed permanently', exact: true }),
  ).toBeEnabled();
});

test('accessibility: no serious or critical axe violations on the main screens', async ({
  page,
  harness,
}) => {
  const problems: string[] = [];
  async function scan(target: Page, screen: string) {
    const { violations } = await new AxeBuilder({ page: target }).analyze();
    for (const v of violations)
      if (v.impact === 'serious' || v.impact === 'critical')
        problems.push(`${screen}: ${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
  }
  const landing = await page.context().newPage();
  await landing.goto('/');
  await scan(landing, 'landing');
  await landing.close();
  await host(page, harness);
  await scan(page, 'board');
  await page.locator('#temp-manager > summary').click();
  await scan(page, 'temp');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await scan(page, 'settings');
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  await page.getByRole('button', { name: 'Start listening' }).click();
  await page.getByLabel('Transcribe Mimic audio').check();
  await expect(
    page.getByRole('list', { name: 'Transcript' }).getByRole('button').first(),
  ).toBeVisible({ timeout: 15000 });
  await scan(page, 'mimic');
  await page.keyboard.press('Escape');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan(page, 'phone');
  expect(problems).toEqual([]);
});

test('accessibility: no serious or critical axe violations in the dialogs, views and player tools', async ({
  page,
  harness,
}) => {
  const problems: string[] = [];
  async function scan(screen: string) {
    const { violations } = await new AxeBuilder({ page }).analyze();
    for (const v of violations)
      if (v.impact === 'serious' || v.impact === 'critical')
        problems.push(`${screen}: ${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
  }
  await host(page, harness);
  for (const [button, screen] of [
    ['Edit icon for Airhorn', 'icon editor'],
    ['Statistics for Airhorn', 'statistics'],
    ['Manage layout', 'my layout'],
  ]) {
    await page.getByRole('button', { name: button, exact: true }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await scan(screen);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  }
  await page.getByRole('button', { name: 'My Layout', exact: true }).click();
  await page.getByRole('button', { name: 'Organize Airhorn', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await scan('organize');
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Host Layout', exact: true }).click();
  await page.getByLabel('View mode', { exact: true }).selectOption('waveforms');
  await scan('waveforms view');
  await page.getByLabel('View mode', { exact: true }).selectOption('details');
  await page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }).click();
  await expect(page.getByRole('progressbar', { name: 'Sound progress' })).toBeVisible();
  await scan('details view while playing');
  await page.locator('.player').getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByLabel('Cue time seconds', { exact: true })).toBeVisible();
  await scan('player edit tools');
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Scan tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  await expect(
    page.getByRole('button', { name: 'Play from start: Scan tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY }); // decoding an upload can be slow on CI
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Scan tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  await scan('temp edit copy');
  expect(problems).toEqual([]);
});

test('slow connection: busy buttons, Temp and Mimic toggles keep keyboard focus while the companion answers', async ({
  page,
  harness,
}) => {
  // Delay every command by 600 ms, as a busy PC or slow link would. Chrome drops focus from a control
  // that becomes disabled, so busy controls must use aria-disabled instead.
  await page.routeWebSocket(/.*/, (ws) => {
    const server = ws.connectToServer();
    ws.onMessage((message) => setTimeout(() => server.send(message), 600));
    server.onMessage((message) => ws.send(message));
  });
  await host(page, harness);
  for (const name of [
    'Play from start: Airhorn',
    'Increase Kick counter',
    'Reverse and play Airhorn',
  ]) {
    const button = page.getByRole('button', { name, exact: true }).first();
    await button.focus();
    await page.keyboard.press('Enter');
    await expect(button).toHaveAttribute('aria-disabled', 'true');
    await expect(button).toBeFocused();
    await expect(button).not.toHaveAttribute('aria-disabled');
    await expect(button).toBeFocused();
  }
  await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
  // The reverse above left a Temp source; its buttons and Refresh library behave the same way.
  await page.locator('#temp-manager > summary').click();
  for (const button of [
    page.getByRole('button', { name: 'Play normal', exact: true }).first(),
    page.getByRole('button', { name: 'Refresh library', exact: true }),
  ]) {
    await button.focus();
    await page.keyboard.press('Enter');
    await expect(button).toHaveAttribute('aria-disabled', 'true');
    await expect(button).toBeFocused();
    await expect(button).not.toHaveAttribute('aria-disabled');
    await expect(button).toBeFocused();
  }
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('button', { name: 'Stop listening' })).toBeVisible();
  const f6 = dialog.getByLabel('F6 hotkey');
  await f6.focus();
  await page.keyboard.press('Space');
  await expect(f6).toHaveAttribute('aria-disabled', 'true');
  await expect(f6).toBeFocused();
  await expect(f6).not.toHaveAttribute('aria-disabled');
  await expect(f6).toBeChecked();
  await expect(f6).toBeFocused();
});

test('Mimic hotkey: the host browser remembers its key and length across companion restarts', async ({
  page,
  harness,
}) => {
  // As if this browser chose F7 and 15 s in an earlier session; the (restarted) companion is on F6 and 10 s.
  await page.addInitScript(() =>
    localStorage.setItem('soundpad-mimic-hotkey', JSON.stringify({ key: 'F7', seconds: 15 })),
  );
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByLabel('Hotkey key', { exact: true })).toHaveValue('F6');
  await dialog.getByLabel('F6 hotkey').check();
  await expect(dialog.getByText('Press F7 in any app to save the last 15 s')).toBeVisible();
  await expect(dialog.getByLabel('Hotkey key', { exact: true })).toHaveValue('F7');
  await expect(dialog.getByLabel('F7 hotkey')).toBeChecked();
  // A remembered key that another app now owns (the harness pretends F9 is) is reported, and the
  // hotkey still turns on with the current key.
  // Each toggle runs a command; a press while it runs is ignored (aria-disabled), so wait it out.
  const f7 = dialog.getByLabel('F7 hotkey');
  await expect(f7).not.toHaveAttribute('aria-disabled');
  await f7.uncheck();
  await expect(f7).not.toHaveAttribute('aria-disabled');
  await page.evaluate(() =>
    localStorage.setItem('soundpad-mimic-hotkey', JSON.stringify({ key: 'F9', seconds: 15 })),
  );
  await dialog.getByLabel('F7 hotkey').check();
  await expect(page.getByText('F9 is already used by another app', { exact: false })).toBeVisible();
  await expect(dialog.getByText('Press F7 in any app to save the last 15 s')).toBeVisible();
});

test('Mimic: the host imports a clip from Voice Replay Studio into Temp', async ({
  page,
  harness,
}) => {
  // The harness points at a fixture Voice Replay Studio library with two clips, never the real one.
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByText('From Voice Replay Studio').click();
  const list = dialog.getByRole('list', { name: 'Voice Replay Studio clips' });
  await expect(list.getByRole('listitem')).toHaveCount(2);
  await expect(list.getByRole('listitem').first()).toContainText('Fixture hello there');
  await expect(list.getByRole('listitem').first()).toContainText('hello there from voice replay');
  const { violations } = await new AxeBuilder({ page }).analyze();
  expect(
    violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id),
  ).toEqual([]);
  // Refresh reloads the list (clips saved meanwhile appear); nothing in it changes here.
  await dialog.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(list.getByRole('listitem')).toHaveCount(2);
  await list.getByRole('button', { name: 'Import Fixture hello there' }).click();
  await expect(page.getByText('Imported “Fixture hello there” into Temp.')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture hello there', exact: true }),
  ).toBeEnabled();
  // Import & play adds the other clip and plays it through Soundpad at once, like Mimic & play.
  const playsBefore = harness.plays.length;
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  await dialog.getByText('From Voice Replay Studio').click();
  await dialog
    .getByRole('button', { name: 'Import & play Fixture see you later', exact: true })
    .click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture see you later', exact: true }),
  ).toBeEnabled();
  await expect
    .poll(() => harness.plays.slice(playsBefore).some((p) => p.startsWith('DoPlaySound(')))
    .toBe(true);
  // Undo on the import notice takes it back out of Temp (2.34), stopping it first since it is playing.
  await page
    .getByRole('status')
    .filter({ hasText: 'Imported “Fixture see you later” into Temp.' })
    .getByRole('button', { name: 'Undo' })
    .click();
  await expect(page.getByText('Import removed from Temp.')).toBeVisible({ timeout: 15000 });
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture see you later', exact: true }),
  ).toHaveCount(0, { timeout: TEMP_READY });
  await expect(
    page.getByRole('button', { name: 'Play from start: Fixture hello there', exact: true }),
  ).toBeEnabled();
});

test('an older companion: Settings names what an update adds, and those controls stay hidden', async ({
  page,
  harness,
}) => {
  // Make the harness look like companion 2.13.0.
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => server.send(message));
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state?.companionVersion) frame.state.companionVersion = '2.13.0';
      socket.send(JSON.stringify(frame));
    });
  });
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  const note = page.getByRole('note');
  await expect(note).toContainText('Your Windows companion is 2.13.0');
  await expect(note).toContainText('the hotkey key choice (F6–F10)');
  await expect(note).toContainText('importing from Voice Replay Studio');
  await expect(note.getByRole('link', { name: 'Download the latest companion' })).toHaveAttribute(
    'href',
    /releases\/latest\/download\/SoundpadWeb\.exe$/,
  );
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await expect(dialog.getByLabel('F6 hotkey')).toBeVisible();
  await expect(dialog.getByLabel('Hotkey key', { exact: true })).toHaveCount(0);
  await expect(dialog.getByText('From Voice Replay Studio')).toHaveCount(0);
});

test('the current companion gets no update note', async ({ page, harness }) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('dialog', { name: 'Session & access' })).toBeVisible();
  await expect(page.getByText('A newer one adds')).toHaveCount(0);
});

test('Mimic help shows keyboard shortcuts with a mouse', async ({ page, harness }) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByText('or press', { exact: false })).toBeVisible();
  await expect(dialog.getByText('tap a transcript line', { exact: false })).toBeHidden();
});

test.describe('on a touch phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
  test('Mimic help speaks touch instead of keys', async ({ page, harness }) => {
    await host(page, harness);
    await page.getByRole('button', { name: 'Mimic', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Mimic' });
    await dialog.getByRole('button', { name: 'Start listening' }).click();
    await expect(dialog.getByText('tap a transcript line', { exact: false })).toBeVisible();
    await expect(dialog.getByText('or press', { exact: false })).toBeHidden();
  });
});

test('Mimic: a fresh clip can be played with a quick effect', async ({ page, harness }) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Effect me');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  await expect(recent.getByRole('button', { name: /Replay Effect me/ })).toBeVisible();
  const playsBefore = harness.plays.length;
  await recent.getByLabel('Play Effect me with an effect').selectOption('slow-voice');
  await expect(
    page.getByRole('button', {
      name: 'Play from start: Effect me (0.5x speed, same pitch)',
      exact: true,
    }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await expect
    .poll(() => harness.plays.slice(playsBefore).some((p) => p.startsWith('DoPlaySound(')))
    .toBe(true);
});

test('Mimic: Edit on a fresh clip opens its Edit copy in Temp', async ({ page, harness }) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Trim me');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  await expect(recent.getByRole('button', { name: /Replay Trim me/ })).toBeVisible();
  // Like Voice Replay Studio opening a new capture in its editor: one press, Mimic closes, Temp opens
  // with Edit copy for this clip, and Start has the keyboard focus.
  await recent.getByRole('button', { name: 'Edit a copy of Trim me' }).click();
  await expect(dialog).toBeHidden();
  const form = page.getByRole('form', { name: 'Edit a copy of Trim me' });
  await expect(form).toBeVisible({ timeout: 15000 });
  await expect(form.getByLabel('Start (s)')).toBeFocused();
  await expect(form.getByLabel('Start (s)')).toHaveValue('0');
  await form.getByLabel('Start (s)').fill('1');
  await form.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', { name: 'Play from start: Trim me (trimmed)', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
});

test('Mimic: Undo on the clip notice takes a clip made by mistake back out (host)', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name', { exact: true }).fill('Oops');
  // Mimic & play: the clip is playing when Undo is pressed, so Undo stops it first.
  await page.keyboard.press('Shift+Enter');
  const clip = page.getByRole('button', { name: 'Play from start: Oops', exact: true });
  await expect(clip).toBeEnabled({ timeout: TEMP_READY });
  const notice = page.getByRole('status').filter({ hasText: 'mimicked “Oops”' });
  await notice.getByRole('button', { name: 'Undo' }).click();
  await expect(page.getByText('Clip removed from Temp.')).toBeVisible({ timeout: 15000 });
  await expect(clip).toHaveCount(0, { timeout: TEMP_READY });
  await expect(dialog.getByRole('region', { name: 'Just mimicked' })).toHaveCount(0);
  await expect(page.getByText('Stop playback before removing', { exact: false })).toHaveCount(0);
});

test('Mimic: Keep on a fresh clip opens Keep permanently for it in Temp (host)', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const openMimic = async () => {
    await page.getByRole('button', { name: 'Mimic', exact: true }).click();
    return page.getByRole('dialog', { name: 'Mimic' });
  };
  let dialog = await openMimic();
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Keeper');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = () => dialog.getByRole('region', { name: 'Just mimicked' });
  await expect(recent().getByRole('button', { name: /Replay Keeper/ })).toBeVisible();
  // Like Voice Replay Studio's library: one press, Mimic closes, Temp asks where to keep it.
  await recent().getByRole('button', { name: 'Keep Keeper permanently' }).click();
  await expect(dialog).toBeHidden();
  const confirm = page.getByRole('region', { name: 'Confirm permanent audio save' });
  await expect(confirm).toBeVisible({ timeout: 15000 });
  await expect(confirm.getByRole('heading')).toHaveText('Keep Keeper (normal)');
  await expect(confirm.getByLabel('Permanent folder (host only)')).toBeFocused();
  await confirm.getByRole('button', { name: 'Confirm permanent save', exact: true }).click();
  await expect(page.locator('.temp-save')).toHaveCount(0);
  await expect(
    page.locator('.temp-source', { hasText: 'Keeper' }).getByText('normal · permanent'),
  ).toBeVisible({ timeout: 15000 });
  // Pressing Keep again only says it is already kept.
  dialog = await openMimic();
  await recent().getByRole('button', { name: 'Keep Keeper permanently' }).click();
  await expect(page.getByText('Keeper is already kept permanently.')).toBeVisible({
    timeout: 15000,
  });
  await expect(page.locator('.temp-save')).toHaveCount(0);
});

test('Edit copy: with transcripts on, a plain trim is named after its words', async ({
  page,
  harness,
}) => {
  // The harness's stand-in speech engine calls each burst of sound "Fixture tone N"; never real Whisper.
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await dialog.getByLabel('Transcribe Mimic audio').check();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Words test');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  await recent.getByRole('button', { name: 'Edit a copy of Words test' }).click();
  const form = page.getByRole('form', { name: 'Edit a copy of Words test' });
  await expect(form).toBeVisible({ timeout: 15000 });
  await expect(form).toContainText('named after its words');
  await expect(form.getByLabel('Name')).toHaveAttribute('placeholder', 'Its words (trimmed)');
  // An effect keeps its effect name, so the hint goes away.
  await form.getByRole('combobox').selectOption('echo');
  await expect(form).not.toContainText('named after its words');
  await form.getByRole('combobox').selectOption('none');
  await form.getByRole('button', { name: 'Save copy' }).click();
  await expect(
    page.getByRole('button', { name: /^Play from start: Fixture tone \d+.* \(trimmed\)$/ }).first(),
  ).toBeEnabled({ timeout: TEMP_READY });
});

test('accessibility: no serious or critical axe violations in the newer controls', async ({
  page,
  harness,
}) => {
  // Temp's Clear button, Edit copy's speed and Keep pitch, Mimic's Effect menu and Voice Replay Studio list (2.15–2.18);
  // Edit copy's waveform, Zoom, Preview and Loop, and Mimic's Edit button (2.19–2.23).
  const problems: string[] = [];
  async function scan(screen: string) {
    const { violations } = await new AxeBuilder({ page }).analyze();
    for (const v of violations)
      if (v.impact === 'serious' || v.impact === 'critical')
        problems.push(`${screen}: ${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
  }
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Scan tone.wav', mimeType: 'audio/wav', buffer: sessionWave(10) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Scan tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await scan('temp with clear button');
  await page
    .locator('.temp-source', { hasText: 'Scan tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Scan tone' });
  await form.getByRole('combobox').selectOption('speed');
  await form.getByLabel('Keep pitch').check();
  await scan('edit copy at any speed');
  await form.getByRole('combobox').selectOption('none');
  await form.getByLabel('Start (s)').fill('2');
  await form.getByLabel('End (s)').fill('5');
  await form.getByLabel('Loop').check();
  await form.getByRole('button', { name: 'Zoom to selection' }).click();
  await expect(form.getByRole('button', { name: 'Show whole sound' })).toBeVisible();
  await scan('edit copy zoomed, with preview and loop');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan('edit copy on a phone');
  await page.setViewportSize({ width: 1280, height: 720 });
  await form.getByRole('button', { name: 'Cancel' }).click();
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog.getByLabel('Clip name').fill('Scan clip');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Just mimicked' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Edit a copy of Scan clip' })).toBeVisible();
  await dialog.getByText('From Voice Replay Studio').click();
  await expect(
    dialog.getByRole('list', { name: 'Voice Replay Studio clips' }).getByRole('listitem'),
  ).toHaveCount(2);
  await scan('mimic effects and library');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan('mimic on a phone');
  expect(problems).toEqual([]);
});

test('Mimic on a phone: a fresh clip keeps its name readable beside Edit, Keep and Effect', async ({
  page,
  harness,
}) => {
  // 2.21 and 2.26 added buttons to each row; on a phone they squeezed the name to its first letter.
  await page.setViewportSize({ width: 390, height: 844 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog
    .getByLabel('Clip name', { exact: true })
    .fill('A rather long clip name that someone typed');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  const name = recent.locator('.mimic-recent-name').first();
  await expect(name).toBeVisible();
  expect((await name.boundingBox())!.width).toBeGreaterThan(100);
  // Everything stays inside the window.
  const recentBox = (await recent.boundingBox())!;
  for (const control of await recent.locator('button, select').all()) {
    const box = (await control.boundingBox())!;
    expect(box.x + box.width).toBeLessThanOrEqual(recentBox.x + recentBox.width + 1);
  }
});

test('on a phone, keyboard focus is never hidden behind the player bar', async ({
  page,
  harness,
}) => {
  // 2.27.2: Tab put Edit copy's Save copy and Save & play under the bar at the bottom of a phone screen.
  await page.setViewportSize({ width: 375, height: 812 });
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Focus tone.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
  await expect(
    page.getByRole('button', { name: 'Play from start: Focus tone', exact: true }),
  ).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Focus tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Focus tone' });
  await page.evaluate(() => window.scrollTo(0, 0));
  await form.getByLabel('Name').focus();
  for (const name of ['Save copy', 'Save & play', 'Preview']) {
    await page.keyboard.press('Tab');
    await expect(form.getByRole('button', { name })).toBeFocused();
    // What is drawn at the focused control's centre is the control itself, not the player bar.
    const shown = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement;
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return hit === el || el.contains(hit);
    });
    expect(shown, `${name} is visible when focused`).toBe(true);
  }
});

test('Mimic fits a phone: header keeps Settings on screen and long clip names do not widen the window', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  // The "Mimic is listening" notice must not cover the dialog's close button (the only way out on a phone).
  await expect(page.locator('.notification-stack .toast').first()).toBeVisible();
  const closeBox = (await dialog.getByRole('button', { name: 'Close dialog' }).boundingBox())!;
  const noticeBox = (await page.locator('.notification-stack').boundingBox())!;
  expect(noticeBox.y).toBeGreaterThan(closeBox.y + closeBox.height);
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog
    .getByLabel('Clip name')
    .fill('A rather long clip name that would wrap on a phone screen');
  // 2.40: Enter captures a draft, and Enter again keeps it in Temp.
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Draft' })).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('region', { name: 'Just mimicked' })).toBeVisible();
  const pastDialog = await page.evaluate(() => {
    const edge = document.querySelector('.dialog')!.getBoundingClientRect().right + 1;
    return [...document.querySelectorAll('.dialog *')].filter(
      (e) => e.getBoundingClientRect().right > edge,
    ).length;
  });
  expect(pastDialog).toBe(0);
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: 'Mimic on' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(
    false,
  );
  const settings = await page.getByRole('button', { name: 'Access settings' }).boundingBox();
  expect(settings!.x + settings!.width).toBeLessThanOrEqual(390);
});

// ---- Playback intents (2.35): the newest click wins, Stop and Panic, pending against playing ----

const LONG_TITLE =
  'An extremely long sound title that should wrap gracefully on every single phone screen without breaking the grid';
const playButton = (page: Page, name: string, nth = 0) =>
  page.getByRole('button', { name: `Play from start: ${name}`, exact: true }).nth(nth);
/**
 * Lets a guest page join the host page inside this browser context with no relay and no WebRTC: the
 * dev server's BroadcastChannel transport (web/src/network/test-transport.ts) carries the session.
 * Call before the host page loads. A production build has no such switch.
 */
async function useLocalGuests(page: Page) {
  await page
    .context()
    .addInitScript(() => localStorage.setItem('soundpad-test-transport', 'broadcast'));
}
/** Opens the host's invitation in a second page of the same context and waits until it is connected. */
async function joinGuest(hostPage: Page, name = 'Fixture Guest') {
  // The invitation exists once the companion has begun the session, which a slow Soundpad delays.
  await hostPage.getByRole('button', { name: 'Access settings' }).click();
  await expect(hostPage.locator('#invite')).toHaveValue(/#join=/);
  const link = await hostPage.locator('#invite').inputValue();
  await hostPage.getByRole('button', { name: 'Close dialog' }).click();
  const guest = await hostPage.context().newPage();
  await guest.goto(link);
  await finishGuestName(guest, name);
  await expect(playButton(guest, 'Airhorn')).toBeEnabled();
  await expect(guest.locator('header .status')).toHaveText('Connected');
  await expect(
    hostPage.locator('.board-heading').getByRole('status').filter({ hasText: 'connected guest' }),
  ).toHaveText(/1 connected guest/);
  return guest;
}
/** Records every error notice a page shows from now on (each leaves after 8 s); returns a reader. */
async function recordErrors(page: Page) {
  await page.evaluate(() => {
    const seen: string[] = [];
    (window as unknown as { errorNotices: string[] }).errorNotices = seen;
    new MutationObserver(() => {
      for (const notice of document.querySelectorAll('.notification-stack .notice-error')) {
        const text = notice.textContent ?? '';
        if (!seen.includes(text)) seen.push(text);
      }
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
  });
  return () => page.evaluate(() => (window as unknown as { errorNotices: string[] }).errorNotices);
}
/** The playback status the page was last told: STOPPED, PLAYING and so on. */
const reportedStatus = (page: Page) =>
  page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    return controller.getSnapshot().state.playback.status as string;
  });
/** Sends one command from the page as its controller would, without the page's own checks. */
const sendCommand = (page: Page, op: string, fields: Record<string, unknown> = {}) =>
  page.evaluate(
    async ([op, fields]) => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      const { library } = controller.getSnapshot().state;
      const play = { soundId: library.sounds[0].id, libraryVersion: library.version };
      try {
        return {
          data: await controller.run(op, op === 'play' ? play : fields, { quiet: true }),
        };
      } catch (e) {
        return { error: e instanceof Error ? e.message : String(e) };
      }
    },
    [op, fields] as const,
  );

test('two controllers: host and guest alternate plays and waveform cues, and the last click wins', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page);
  const hostErrors = await recordErrors(page),
    guestErrors = await recordErrors(guest);
  // A click on another sound's waveform starts it there: one play, then its cue.
  const hostCue = page.getByRole('slider', { name: 'Cue Laugh waveform', exact: true }).nth(1),
    guestCue = guest.getByRole('slider', { name: 'Cue Airhorn waveform', exact: true });
  for (const cue of [hostCue, guestCue])
    await expect(cue.locator('svg[viewBox="0 0 800 64"]')).toBeVisible();
  const middle = async (cue: typeof hostCue) => {
    const box = (await cue.boundingBox())!;
    return { x: box.width / 2, y: Math.min(14, box.height / 3) };
  };
  const hostAt = await middle(hostCue),
    guestAt = await middle(guestCue);
  await hostCue.click({ position: hostAt });
  await expect.poll(() => harness.plays.slice(0, 2).join()).toMatch(/^DoPlaySound\(3\),DoSeekMs\(/);

  // 3 s of both people clicking as fast as they can at the same time: the host plays Airhorn and
  // cues the second Laugh, the guest plays the first Laugh and cues Airhorn (the sound the host keeps
  // starting). force: a press on a tile whose own start is still unanswered is ignored by the page,
  // as it is for a person, instead of Playwright waiting for the tile.
  const until = Date.now() + 3000;
  const hammer = async (play: Locator, cue: Locator, at: { x: number; y: number }) => {
    let clicks = 0;
    for (; Date.now() < until; clicks++)
      await (clicks % 3 === 2
        ? cue.click({ force: true, position: at })
        : play.click({ force: true }));
    return clicks;
  };
  const counts = await Promise.all([
    hammer(playButton(page, 'Airhorn'), hostCue, hostAt),
    hammer(playButton(guest, 'Laugh'), guestCue, guestAt),
  ]);
  const clicks = counts[0] + counts[1];
  // Then one more from the guest. It is the last click and the last to reach the companion: the
  // host's clicks were already sent, and the guest's path (through the host's page) is the longer.
  await playButton(guest, LONG_TITLE).click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(4)');
  // Both players agree on what is playing now, and on who asked for it.
  for (const who of [page, guest]) {
    await expect(who.locator('.player-track')).toContainText(LONG_TITLE.slice(0, 40));
    await expect(who.locator('.player-track')).toContainText('Requested by Fixture Guest');
  }
  // Nothing older turns up afterwards: no late play, no cue of a sound that lost.
  await page.waitForTimeout(1500);
  expect(harness.soundpad.at(-1)).toBe('DoPlaySound(4)');
  expect(harness.plays.filter((p) => p === 'DoPlaySound(4)')).toHaveLength(1);
  test.info().annotations.push({
    type: 'burst',
    description: `${clicks} clicks in 3 s; Soundpad started ${harness.plays.filter((p) => p.startsWith('DoPlaySound(')).length} plays`,
  });
  // Both people got through during the burst.
  expect(harness.plays).toContain('DoPlaySound(1)');
  expect(harness.plays).toContain('DoPlaySound(2)');
  // Nobody was shown an error, and the guest is still in the session.
  expect(await hostErrors()).toEqual([]);
  expect(await guestErrors()).toEqual([]);
  await expect(guest.locator('header .status')).toHaveText('Connected');
  await expect(
    page.locator('.board-heading').getByRole('status').filter({ hasText: 'connected guest' }),
  ).toHaveText(/1 connected guest/);
  await expect(playButton(guest, 'Airhorn')).toBeEnabled();
});

test('Panic stops playback and holds guest playback until the host resumes; Shift+Escape does the same', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page);
  const guestErrors = await recordErrors(guest),
    hostErrors = await recordErrors(page);
  const panic = page.getByRole('button', {
    name: 'Panic: stop audio and pause guest playback',
    exact: true,
  });
  const paused = (who: Page) => who.locator('.playback-suspended');
  await expect(panic).toBeVisible();
  await expect(guest.getByRole('button', { name: /^Panic/ })).toHaveCount(0);

  await playButton(guest, 'Airhorn').click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(1)');
  await panic.click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoStopSound()');
  await expect(paused(page)).toContainText('Guest playback is paused.');
  const resume = paused(page).getByRole('button', { name: 'Resume guest playback', exact: true });
  await expect(resume).toBeVisible();
  await expect.poll(() => reportedStatus(page)).toBe('STOPPED');

  // The guest is told, its play controls say why, and its presses do nothing at all.
  await expect(paused(guest)).toContainText('The host paused guest playback');
  await expect(guest.locator('.grid-caption')).toHaveText('PAUSED BY HOST');
  await expect(playButton(guest, 'Airhorn')).toHaveAttribute('aria-disabled', 'true');
  await expect(playButton(guest, 'Airhorn')).toHaveAttribute(
    'title',
    'The host paused guest playback',
  );
  const before = harness.soundpad.length;
  await playButton(guest, 'Airhorn').click({ force: true });
  await playButton(guest, 'Laugh').click({ force: true });
  await guest.getByRole('button', { name: 'Stop', exact: true }).click({ force: true });
  // Even a command sent past the page's own check is refused quietly by the companion.
  expect(await sendCommand(guest, 'play')).toEqual({
    data: expect.objectContaining({ superseded: true, reason: 'suspended' }),
  });
  // Panic and Resume are the host's alone.
  expect(await sendCommand(guest, 'panic', { action: 'disable' })).toEqual({
    error: 'The host has disabled this action.',
  });
  await guest.waitForTimeout(700);
  expect(harness.soundpad.slice(before)).toEqual([]);
  expect(await guestErrors()).toEqual([]);
  await expect(paused(guest)).toBeVisible();

  // The host can still play, and the session goes on.
  await playButton(page, 'Laugh').click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(2)');
  await expect(guest.locator('header .status')).toHaveText('Connected');

  await resume.click();
  await expect(paused(page)).toHaveCount(0);
  await expect(paused(guest)).toHaveCount(0);
  await expect(playButton(guest, 'Airhorn')).not.toHaveAttribute('aria-disabled');
  await playButton(guest, LONG_TITLE).click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(4)');

  // Shift+Escape is Panic from the keyboard, except while typing.
  const search = page.getByRole('textbox', { name: 'Search sounds' });
  await search.fill('Laugh');
  await search.press('Shift+Escape');
  await page.waitForTimeout(500);
  await expect(paused(page)).toHaveCount(0);
  expect(harness.soundpad.at(-1)).toBe('DoPlaySound(4)');
  await search.fill('');
  await playButton(page, 'Airhorn').focus();
  await page.keyboard.press('Shift+Escape');
  await expect(paused(page)).toContainText('Guest playback is paused.');
  await expect(paused(guest)).toContainText('The host paused guest playback');
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoStopSound()');
  await paused(page).getByRole('button', { name: 'Resume guest playback', exact: true }).click();
  await expect(paused(guest)).toHaveCount(0);
  expect(await hostErrors()).toEqual([]);
  expect(await guestErrors()).toEqual([]);
});

test.describe('with a Soundpad that takes 40 ms to answer', () => {
  test.use({ harnessOptions: ['--latency-ms', '40'] });
  test('Stop wins: after a burst of plays Soundpad ends stopped, and nothing starts after the Stop', async ({
    page,
    harness,
  }) => {
    // Every play either person asks for travels to the companion over the host page's connection.
    let asked = 0;
    page.on('websocket', (socket) =>
      socket.on('framesent', ({ payload }) => {
        if (String(payload).includes('"op":"play"')) asked++;
      }),
    );
    await useLocalGuests(page);
    await host(page, harness);
    const guest = await joinGuest(page);
    const hostErrors = await recordErrors(page),
      guestErrors = await recordErrors(guest);
    const starts = () => harness.plays.filter((p) => p.startsWith('DoPlaySound(')).length;
    const stops = () => harness.soundpad.filter((c) => c === 'DoStopSound()').length;
    const burst = async (who: Page, sounds: [string, number][]) => {
      for (let i = 0; i < 10; i++)
        await playButton(who, ...sounds[i % sounds.length]).click({ force: true });
    };
    const stopBy = async (who: Page) => {
      const before = stops();
      await who.getByRole('button', { name: 'Stop', exact: true }).click();
      await expect.poll(stops).toBe(before + 1);
      // Plays that were still waiting when the Stop arrived must not start afterwards.
      await page.waitForTimeout(1500);
      expect(harness.soundpad.at(-1)).toBe('DoStopSound()');
      for (const each of [page, guest])
        await expect.poll(() => reportedStatus(each)).toBe('STOPPED');
    };
    // Something is playing; then both people hammer Play at once. A play costs this Soundpad at least
    // 80 ms, so plays are still on their way when the guest presses Stop: the last click, and the last
    // to arrive, since the host's were already sent.
    await playButton(page, 'Airhorn').click();
    await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(1)');
    await Promise.all([
      burst(page, [
        ['Laugh', 1],
        ['Airhorn', 0],
      ]),
      burst(guest, [
        ['Laugh', 0],
        [LONG_TITLE, 0],
      ]),
    ]);
    await stopBy(guest);
    expect(asked).toBe(21);
    // The host alone: a run of plays, then Stop on the same connection.
    await burst(page, [
      ['Airhorn', 0],
      ['Laugh', 0],
      [LONG_TITLE, 0],
    ]);
    await stopBy(page);
    expect(asked).toBe(31);
    test.info().annotations.push({
      type: 'burst',
      description: `31 plays asked for; Soundpad started ${starts()} and stopped ${stops()} times`,
    });
    expect(await hostErrors()).toEqual([]);
    expect(await guestErrors()).toEqual([]);
    await expect(guest.locator('header .status')).toHaveText('Connected');
  });
});

test.describe('with a Soundpad that takes 150 ms to answer', () => {
  test.use({ harnessOptions: ['--latency-ms', '150'] });
  test('a clicked tile shows pending until the companion answers, and playing only once it is reported', async ({
    page,
    harness,
  }) => {
    type Sample = {
      at: number;
      answered: boolean;
      pending: boolean;
      busy: boolean;
      hint: string;
      playing: boolean;
      reported: boolean;
    };
    await useLocalGuests(page);
    await host(page, harness);
    const guest = await joinGuest(page);
    for (const [who, name, index] of [
      [page, 'Airhorn', 1],
      [guest, LONG_TITLE, 4],
    ] as const) {
      // What the tile shows, next to whether the play was answered and what was reported: taken at
      // every change to the page and every 20 ms, so no state of the tile goes unseen.
      await who.evaluate(async (name) => {
        const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
        const id = controller
          .getSnapshot()
          .state.library.sounds.find((s: { name: string }) => s.name === name).id;
        const watch = { samples: [] as Sample[], answered: false };
        (window as unknown as { tileWatch: typeof watch }).tileWatch = watch;
        const run = controller.run.bind(controller);
        controller.run = async (...args: unknown[]) => {
          try {
            return await run(...args);
          } finally {
            if (args[0] === 'play') watch.answered = true;
          }
        };
        const sample = () => {
          const tile = document.querySelector(
            `.sound-entry:has([aria-label="Play from start: ${name}"])`,
          )!;
          const reported = controller.getSnapshot().state.playback;
          watch.samples.push({
            at: performance.now(),
            answered: watch.answered,
            pending: tile.getAttribute('data-intent') === 'pending',
            busy: tile.querySelector('.sound-button')?.getAttribute('aria-busy') === 'true',
            hint: tile.querySelector('.sound-hint')?.textContent ?? '',
            playing:
              tile.classList.contains('now-playing') ||
              !!tile.querySelector('.is-playing, .playing-dot'),
            reported:
              reported.soundId === id && ['PLAYING', 'PAUSED', 'SEEKING'].includes(reported.status),
          });
        };
        new MutationObserver(sample).observe(document.body, {
          attributes: true,
          childList: true,
          characterData: true,
          subtree: true,
        });
        setInterval(sample, 20);
      }, name);
      const tile = who.locator('.sound-entry', { has: playButton(who, name) });
      await playButton(who, name).click();
      await expect(tile).toHaveAttribute('data-intent', 'pending');
      await expect(who.locator('.player')).toHaveAttribute('data-intent', 'pending');
      await expect(tile.locator('.sound-hint')).toHaveText('Preparing audio…');
      await expect(playButton(who, name)).toHaveAttribute('aria-disabled', 'true');
      await expect(tile).toHaveClass(/now-playing/);
      await expect(tile).not.toHaveAttribute('data-intent');
      await expect(who.locator('.player')).not.toHaveAttribute('data-intent');
      expect(harness.plays.at(-1)).toBe(`DoPlaySound(${index})`);
      const samples = await who.evaluate(
        () => (window as unknown as { tileWatch: { samples: Sample[] } }).tileWatch.samples,
      );
      const pendingFrom = samples.findIndex((s) => s.pending),
        answeredFrom = samples.findIndex((s) => s.answered);
      expect(pendingFrom).toBeGreaterThanOrEqual(0);
      // This Soundpad needs at least 300 ms for a play (two requests), so the wait is a real one.
      expect(samples[answeredFrom].at - samples[pendingFrom].at).toBeGreaterThan(250);
      // Pending for exactly as long as the answer is outstanding...
      expect(
        samples
          .slice(pendingFrom, answeredFrom)
          .filter((s) => !s.pending || !s.busy || s.hint !== 'Preparing audio…'),
      ).toEqual([]);
      expect(samples.slice(answeredFrom).filter((s) => s.pending)).toEqual([]);
      // ...and "playing" only ever together with the companion's report of it.
      expect(samples.filter((s) => s.playing && !s.reported)).toEqual([]);
      expect(samples.some((s) => s.playing)).toBe(true);
    }
  });
});

test.describe('with a Soundpad that takes 1.5 s to import a file', () => {
  test.use({ harnessOptions: ['--slow-add-ms', '1500'] });
  test('Save & play: a newer Play made while the copy is prepared is not replaced by the copy', async ({
    page,
    harness,
  }) => {
    await host(page, harness);
    const errors = await recordErrors(page);
    await page
      .getByLabel('Add session audio', { exact: true })
      .setInputFiles({ name: 'Slow copy.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
    await expect(playButton(page, 'Slow copy')).toBeEnabled({ timeout: TEMP_READY });
    await page.locator('#temp-manager > summary').click();
    await page
      .locator('.temp-source', { hasText: 'Slow copy' })
      .getByRole('button', { name: 'Edit copy' })
      .click();
    const form = page.getByRole('form', { name: 'Edit a copy of Slow copy' });
    await form.getByRole('combobox').selectOption('chipmunk');
    const before = harness.soundpad.length;
    await form.getByRole('button', { name: 'Save & play' }).click();
    await playButton(page, 'Airhorn').click();
    // The copy is finished and added, but only the newer click is heard.
    await expect(playButton(page, 'Slow copy (chipmunk)')).toBeEnabled({ timeout: TEMP_READY });
    await expect(
      page.getByText('Copy added to Temp. The original is unchanged.', { exact: true }),
    ).toBeVisible();
    await expect(page.getByText('Copy added to Temp and playing.', { exact: false })).toHaveCount(
      0,
    );
    await expect.poll(() => harness.soundpad.slice(before)).toEqual(['DoPlaySound(1)']);
    await page.waitForTimeout(1500);
    expect(harness.soundpad.slice(before)).toEqual(['DoPlaySound(1)']);
    await expect(page.locator('.player-track')).toContainText('Airhorn');
    expect(await errors()).toEqual([]);
  });

  test('Mimic & play: a guest’s newer Play made while the clip is prepared is not replaced by the clip', async ({
    page,
    harness,
  }) => {
    await useLocalGuests(page);
    await host(page, harness);
    const guest = await joinGuest(page);
    const hostErrors = await recordErrors(page),
      guestErrors = await recordErrors(guest);
    await page.getByRole('button', { name: 'Mimic', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Mimic' });
    await dialog.getByRole('button', { name: 'Start listening' }).click();
    await expect(dialog.getByRole('status').first()).toContainText(
      'Listening to Fixture: tone every 2 s',
    );
    await page.waitForTimeout(2600);
    await dialog.getByRole('button', { name: 'Last 3 s' }).click();
    await dialog.getByLabel('Clip name').fill('Late clip');
    const before = harness.soundpad.length;
    await dialog.getByRole('button', { name: 'Mimic & play' }).click();
    await playButton(guest, 'Airhorn').click();
    // The clip is finished and added, but only the guest's newer click is heard.
    await expect(playButton(page, 'Late clip')).toBeEnabled({ timeout: TEMP_READY });
    await expect.poll(() => harness.soundpad.slice(before)).toEqual(['DoPlaySound(1)']);
    await page.waitForTimeout(1500);
    expect(harness.soundpad.slice(before)).toEqual(['DoPlaySound(1)']);
    // By now the 2.5 s sound may have ended; the player still names the last one that played.
    for (const who of [page, guest])
      await expect(who.locator('.player-track')).toContainText('Airhorn');
    expect(await hostErrors()).toEqual([]);
    expect(await guestErrors()).toEqual([]);
  });
});

test('two pages in one browser: a guest joins without any relay, uploads, saves and plays a copy, and is disconnected by the host', async ({
  page,
  harness,
}) => {
  // The other two-page tests rely on this stand-in transport; this one checks the parts they do not
  // touch: a request with a file and a name (an upload), its answer, and closing a peer.
  // What the host's page passes on to the companion, to see how the guest's Save & play travels.
  const relayed: { op: string; actor: string; autoplay?: boolean }[] = [];
  page.on('websocket', (socket) =>
    socket.on('framesent', ({ payload }) => {
      const { actor, command } = JSON.parse(String(payload));
      if (command) relayed.push({ op: command.op, actor, autoplay: command.autoplay });
    }),
  );
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page, 'Upload Alice');
  await guest
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Guest tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  await expect(guest.locator('.notification-stack')).toContainText(
    'Added to Temp for this session',
  );
  for (const who of [page, guest])
    await expect(playButton(who, 'Guest tone')).toBeEnabled({ timeout: TEMP_READY });
  await playButton(guest, 'Guest tone').click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(5)');
  await expect(page.locator('.player-track')).toContainText('Requested by Upload Alice');
  // A guest's Save & play is one command too (2.35): the host's page passes its autoplay mark on, and
  // the companion plays the copy when it is ready.
  await guest.locator('#temp-manager > summary').click();
  await guest
    .locator('.temp-source', { hasText: 'Guest tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = guest.getByRole('form', { name: 'Edit a copy of Guest tone' });
  await form.getByRole('combobox').selectOption('chipmunk');
  const before = harness.plays.length;
  await form.getByRole('button', { name: 'Save & play' }).click();
  for (const who of [page, guest])
    await expect(playButton(who, 'Guest tone (chipmunk)')).toBeEnabled({ timeout: TEMP_READY });
  await expect
    .poll(() => harness.plays.slice(before).filter((p) => p.startsWith('DoPlaySound(')))
    .toHaveLength(1);
  await expect(guest.getByText('Copy added to Temp and playing.', { exact: false })).toBeVisible();
  expect(relayed.filter((c) => c.op === 'trimAudio')).toEqual([
    { op: 'trimAudio', actor: 'guest', autoplay: true },
  ]);
  expect(relayed.filter((c) => c.op === 'audioPlay')).toEqual([]);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('button', { name: 'Disconnect', exact: true }).click();
  await expect(guest.getByText('Access revoked', { exact: true })).toBeVisible();
  await expect(
    page.locator('.board-heading').getByRole('status').filter({ hasText: 'connected guest' }),
  ).toHaveText(/0 connected guests/);
  // A reload is a new peer, and the invitation still stands.
  await guest.reload();
  await expect(playButton(guest, 'Guest tone')).toBeEnabled();
  await expect(
    page.locator('.board-heading').getByRole('status').filter({ hasText: 'connected guest' }),
  ).toHaveText(/1 connected guest/);
});

test('a companion older than 2.35: no Panic, and Save & play still plays, as two commands', async ({
  page,
  harness,
}) => {
  // Make the harness look like companion 2.34.2, and note what the page sends it.
  const sent: { op: string; autoplay?: boolean }[] = [];
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => {
      const { command } = JSON.parse(String(message));
      if (command) sent.push({ op: command.op, autoplay: command.autoplay });
      server.send(message);
    });
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state?.companionVersion) frame.state.companionVersion = '2.34.2';
      socket.send(JSON.stringify(frame));
    });
  });
  await host(page, harness);
  await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Panic/ })).toHaveCount(0);
  await playButton(page, 'Airhorn').click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  await page.keyboard.press('Shift+Escape');
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Old way.wav', mimeType: 'audio/wav', buffer: sessionWave(2) });
  await expect(playButton(page, 'Old way')).toBeEnabled({ timeout: TEMP_READY });
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Old way' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  const form = page.getByRole('form', { name: 'Edit a copy of Old way' });
  await form.getByRole('combobox').selectOption('chipmunk');
  const before = harness.plays.length;
  await form.getByRole('button', { name: 'Save & play' }).click();
  await expect(playButton(page, 'Old way (chipmunk)')).toBeEnabled({ timeout: TEMP_READY });
  await expect
    .poll(() => harness.plays.slice(before).some((p) => p.startsWith('DoPlaySound(')))
    .toBe(true);
  await expect(page.getByText('Copy added to Temp and playing.', { exact: false })).toBeVisible();
  // The copy was made by one command and played by a second; neither Panic nor autoplay was sent.
  const ops = sent.map((c) => c.op);
  expect(ops.indexOf('audioPlay')).toBeGreaterThan(ops.indexOf('trimAudio'));
  expect(sent.filter((c) => c.autoplay !== undefined || c.op === 'panic')).toEqual([]);
  expect(harness.soundpad).not.toContain('DoStopSound()');
  await expect(page.locator('.playback-suspended')).toHaveCount(0);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('note')).toContainText(
    'playback that follows the newest click, and Panic',
  );
});

// ---- Desktop session (2.36): one host page runs the guest room (the owner), the others are views ----

const VIEW_OF_TAB =
  'Hosting runs in another Soundpad Web tab. This tab is a view; closing it does not affect guests.';
const VIEW_OF_WINDOW =
  'Hosting runs in the Soundpad Web window. This tab is a view; closing it does not affect guests.';
/**
 * Test-only stand-in for the key the Soundpad Web window gets each launch; the harness takes it as
 * --owner-key. The real companion makes a random one and never prints it.
 */
const OWNER_KEY = 'e2e0'.repeat(16);
/** The board's count of a host page's guests; a view shows the owner's. */
const guestCount = (page: Page) =>
  page.locator('.board-heading').getByRole('status').filter({ hasText: 'connected guest' });
/** The invitation a host page shows in Settings, once it has one. */
async function shownInvite(page: Page) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.locator('#invite')).toHaveValue(/#join=/);
  const link = await page.locator('#invite').inputValue();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  return link;
}
/** Opens a launch link in another page of the same browser, as a second host tab (or the window) would. */
async function anotherHost(page: Page, link: string) {
  const other = await page.context().newPage();
  await other.goto(link);
  await expect(playButton(other, 'Airhorn')).toBeEnabled();
  return other;
}
/**
 * Starts watching a guest's connection status. The returned wait passes once the guest has noticed
 * its host page went away and has come back through the next owner. Every status the header shows is
 * recorded, however briefly: in Playwright's WebKit a closed page's goodbye on the stand-in transport
 * did not arrive (2.36), so the guest noticed only after 10 s without the host's state, as on a
 * dropped link, and by then the next owner was there and the gap lasted milliseconds.
 */
async function watchReconnect(guest: Page) {
  await guest.evaluate(() => {
    const seen: string[] = [];
    (window as unknown as { statuses: string[] }).statuses = seen;
    const read = () => document.querySelector('header .status')?.textContent ?? '';
    new MutationObserver(() => {
      if (seen.at(-1) !== read()) seen.push(read());
    }).observe(document.querySelector('header')!, {
      childList: true,
      subtree: true,
      characterData: true,
      attributes: true,
    });
  });
  return async () => {
    await expect
      .poll(
        () =>
          guest.evaluate(() =>
            (window as unknown as { statuses: string[] }).statuses.some((s) => s !== 'Connected'),
          ),
        { timeout: 20000 },
      )
      .toBe(true);
    await expect(guest.locator('header .status')).toHaveText('Connected', { timeout: 20000 });
  };
}

test('desktop session: a second host page is a view, its Replace invite and Disconnect work through the owner, and it has no End hosting', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page, 'View Alice');
  const first = await shownInvite(page);
  // The same launch link in a second tab: the companion names it a view of the first page's session.
  const view = await anotherHost(page, harness.launch);
  await expect(guestCount(view)).toHaveText(/1 connected guest/);
  await expect(view.locator('aside.sidebar')).toContainText(
    'Another Soundpad Web tab keeps your session running.',
  );
  await expect(page.locator('aside.sidebar')).toContainText('Keep this tab open to host.');
  await view.getByRole('button', { name: 'Access settings' }).click();
  const settings = view.getByRole('dialog', { name: 'Session & access' });
  await expect(view.locator('#invite')).toHaveValue(first);
  await expect(settings.getByRole('note').filter({ hasText: 'This tab is a view' })).toHaveText(
    VIEW_OF_TAB,
  );
  await expect(settings.locator('.guest-row')).toContainText('View Alice');
  await expect(settings.getByRole('button', { name: 'End hosting' })).toHaveCount(0);
  // Disconnect in the view: the owner, which holds the guest's connection, closes it.
  await settings.getByRole('button', { name: 'Disconnect', exact: true }).click();
  await expect(guest.getByText('Access revoked', { exact: true })).toBeVisible();
  for (const who of [page, view]) await expect(guestCount(who)).toHaveText(/0 connected guests/);
  // The invitation still stands: a reload is a new peer, and both pages list it.
  await guest.reload();
  await expect(playButton(guest, 'Airhorn')).toBeEnabled();
  for (const who of [page, view]) await expect(guestCount(who)).toHaveText(/1 connected guest/);
  // Replace invite in the view: the owner replaces it, the guest loses access, both pages show the new one.
  view.once('dialog', (d) => void d.accept());
  await settings.getByRole('button', { name: 'Replace invite' }).click();
  await expect(guest.getByText('Access revoked', { exact: true })).toBeVisible();
  await expect(view.locator('#invite')).not.toHaveValue(first);
  await expect(view.locator('#invite')).toHaveValue(/#join=/);
  const replaced = await view.locator('#invite').inputValue();
  expect(await shownInvite(page)).toBe(replaced);
  // The owner keeps End hosting.
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('button', { name: 'End hosting' })).toBeVisible();
});

test('desktop session: closing a view leaves the guest playing, and a reloaded owner keeps the invitation while the guest reconnects by itself', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page);
  const link = await shownInvite(page);
  const view = await anotherHost(page, harness.launch);
  await expect(guestCount(view)).toHaveText(/1 connected guest/);
  await view.close();
  await playButton(guest, 'Airhorn').click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  await expect(guest.locator('header .status')).toHaveText('Connected');
  await expect(guestCount(page)).toHaveText(/1 connected guest/);
  // A reload is a new page load: the companion gives it its place back after the grace, with the
  // identity it kept, so the invitation is the same and the guest needs no new link.
  const reconnected = await watchReconnect(guest);
  await page.reload();
  await expect(playButton(page, 'Airhorn')).toBeEnabled();
  await reconnected();
  expect(await shownInvite(page)).toBe(link);
  await expect(guestCount(page)).toHaveText(/1 connected guest/);
  const before = harness.plays.length;
  await playButton(guest, 'Airhorn').click();
  await expect.poll(() => harness.plays.slice(before)).toContain('DoPlaySound(1)');
});

test('desktop session: without a window, closing the owner page hands hosting to the view, and the guest reconnects to it', async ({
  page,
  harness,
}) => {
  // `page` is the view here, so the failure diagnostics read the page that survives.
  await useLocalGuests(page);
  const owner = await page.context().newPage();
  await host(owner, harness);
  const guest = await joinGuest(owner);
  const link = await shownInvite(owner);
  await host(page, harness);
  await expect(guestCount(page)).toHaveText(/1 connected guest/);
  await expect(page.locator('aside.sidebar')).toContainText(
    'Another Soundpad Web tab keeps your session running.',
  );
  const reconnected = await watchReconnect(guest);
  await owner.close();
  // After the companion's grace for a reload, the oldest view becomes the owner.
  await expect(page.locator('aside.sidebar')).toContainText('Keep this tab open to host.', {
    timeout: 15000,
  });
  await reconnected();
  expect(await shownInvite(page)).toBe(link);
  await expect(guestCount(page)).toHaveText(/1 connected guest/);
  await playButton(guest, 'Airhorn').click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('note').filter({ hasText: 'This tab is a view' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'End hosting' })).toBeVisible();
});

test.describe('with a Soundpad Web window', () => {
  test.use({ harnessOptions: ['--owner-key', OWNER_KEY] });
  test('desktop session: the window page takes hosting over, and the tab that hosted becomes a view without revoking guests', async ({
    page,
    harness,
  }) => {
    await useLocalGuests(page);
    await host(page, harness);
    const guest = await joinGuest(page);
    const link = await shownInvite(page);
    const reconnected = await watchReconnect(guest);
    // The window's page opens the launch link with the key, as the companion's window does.
    const desk = await anotherHost(page, `${harness.launch}&owner=${OWNER_KEY}`);
    await expect(desk.locator('aside.sidebar')).toContainText(
      'This window keeps your session running.',
    );
    await expect(page.locator('aside.sidebar')).toContainText(
      'The Soundpad Web window keeps your session running.',
    );
    await reconnected();
    expect(await shownInvite(desk)).toBe(link);
    for (const who of [desk, page]) await expect(guestCount(who)).toHaveText(/1 connected guest/);
    await page.getByRole('button', { name: 'Access settings' }).click();
    await expect(page.locator('#invite')).toHaveValue(link);
    await expect(page.getByRole('note').filter({ hasText: 'This tab is a view' })).toHaveText(
      VIEW_OF_WINDOW,
    );
    await expect(page.getByRole('button', { name: 'End hosting' })).toHaveCount(0);
    await page.getByRole('button', { name: 'Close dialog' }).click();
    await playButton(guest, 'Airhorn').click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
    await expect(guest.getByText('Access revoked', { exact: true })).toHaveCount(0);
  });
});

test('desktop session: a companion older than 2.36 gets no session messages, and one host page hosts as before', async ({
  page,
  harness,
}) => {
  // Make the harness look like companion 2.35.0: it reads the hello as protocol 2 (a 2.35 companion
  // ignores the newer fields), so it sends no role; and note anything the page sends with a type.
  const typed: string[] = [];
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.token) {
        expect(frame.protocol).toBe(3);
        frame.protocol = 2;
        delete frame.clientId;
        delete frame.ownerKey;
      }
      if (frame.type) typed.push(frame.type);
      server.send(JSON.stringify(frame));
    });
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state?.companionVersion) frame.state.companionVersion = '2.35.0';
      socket.send(JSON.stringify(frame));
    });
  });
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page);
  const link = await shownInvite(page);
  await expect(page.locator('aside.sidebar')).toContainText('Keep this tab open to host.');
  await playButton(guest, 'Airhorn').click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('dialog', { name: 'Session & access' })).toContainText(
    'The companion controls Soundpad. This tab connects your guests.',
  );
  await expect(page.getByRole('note').filter({ hasText: 'This tab is a view' })).toHaveCount(0);
  await expect(page.getByRole('note').filter({ hasText: 'newer one adds' })).toContainText(
    'the companion window that keeps your session running when the browser tab closes',
  );
  await expect(page.getByRole('button', { name: 'End hosting' })).toBeVisible();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  // The tab keeps its own copy of the identity for older companions, so a reload keeps the invitation.
  await page.reload();
  await expect(playButton(page, 'Airhorn')).toBeEnabled();
  expect(await shownInvite(page)).toBe(link);
  await expect(guestCount(page)).toHaveText(/1 connected guest/, { timeout: 20000 });
  expect(typed).toEqual([]);
});

/**
 * Session control (2.37). Pages of one browser context share localStorage, where a guest's
 * participant id lives, so each extra guest page gets its own profile before it loads (a confirmed
 * name, so it joins without the name prompt).
 */
async function openGuest(hostPage: Page, link: string, name: string) {
  const guest = await hostPage.context().newPage();
  const id = crypto.randomUUID();
  await guest.addInitScript(
    ([id, name]) => {
      localStorage.setItem('soundpad-profile', JSON.stringify({ id, name }));
      localStorage.setItem('soundpad-name-confirmed', '1');
      // 2.41: each guest page stands for another person's browser, with its own key (dev server only).
      sessionStorage.setItem('soundpad-test-guest-key', id);
    },
    [id, name] as const,
  );
  await guest.goto(link);
  return guest;
}
async function inviteLink(hostPage: Page) {
  await hostPage.getByRole('button', { name: 'Access settings' }).click();
  await expect(hostPage.locator('#invite')).toHaveValue(/#join=/);
  const link = await hostPage.locator('#invite').inputValue();
  await hostPage.getByRole('button', { name: 'Close dialog' }).click();
  return link;
}
const waitingList = (hostPage: Page) =>
  hostPage.locator('.board-heading').getByRole('list', { name: 'Guests waiting to join' });

test('lobby: with Ask me first the host lets one guest in and turns another away', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Admission').selectOption({ label: 'Ask me first' });
  await page.getByRole('button', { name: 'Close dialog' }).click();
  const link = await inviteLink(page);
  const ann = await openGuest(page, link, 'Ann');
  await expect(ann.locator('header .status')).toHaveText('Waiting for the host to let you in');
  await expect(playButton(ann, 'Airhorn')).toHaveCount(0);
  // A persistent request and a badge, not a notice that goes away.
  await expect(waitingList(page)).toContainText('Ann wants to join');
  await expect(page.getByRole('button', { name: 'Access settings, 1 waiting' })).toBeVisible();
  await page.waitForTimeout(9000);
  await expect(waitingList(page)).toContainText('Ann wants to join');
  await waitingList(page).getByRole('button', { name: 'Let in Ann' }).click();
  await expect(ann.locator('header .status')).toHaveText('Connected');
  await expect(playButton(ann, 'Airhorn')).toBeEnabled();
  await expect(waitingList(page)).toHaveCount(0);
  // Ann reloads: she keeps her place and is not asked about again.
  await ann.reload();
  await expect(ann.locator('header .status')).toHaveText('Connected');
  await expect(waitingList(page)).toHaveCount(0);

  const bob = await openGuest(page, link, 'Bob');
  await expect(waitingList(page)).toContainText('Bob wants to join');
  await waitingList(page).getByRole('button', { name: 'Turn away Bob' }).click();
  await expect(bob.locator('header .status')).toHaveText('The host didn’t let you in');
  await expect(bob.locator('.connection-banner')).toContainText('ask them for a new invitation');
  await expect(waitingList(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Access settings' }).click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog.getByRole('button', { name: 'Allow Bob again' })).toBeVisible();
  await expect(dialog).toContainText('Connected guests 1 / 12');
});

test('capacity: a full session offers an extra place, and Let in adds one', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Guest places').selectOption('1');
  await page.getByRole('button', { name: 'Close dialog' }).click();
  const link = await inviteLink(page);
  const ann = await openGuest(page, link, 'Ann');
  await expect(ann.locator('header .status')).toHaveText('Connected');
  const bob = await openGuest(page, link, 'Bob');
  await expect(bob.locator('header .status')).toHaveText('This session is full');
  await bob.getByRole('button', { name: 'Ask for an extra place' }).click();
  await expect(bob.locator('header .status')).toHaveText('Waiting for the host to let you in');
  await expect(waitingList(page)).toContainText('Bob asks for an extra place');
  await waitingList(page).getByRole('button', { name: 'Let in Bob' }).click();
  await expect(bob.locator('header .status')).toHaveText('Connected');
  await expect(playButton(bob, 'Airhorn')).toBeEnabled();
  await page.getByRole('button', { name: 'Access settings' }).click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog).toContainText('Connected guests 2 / 2');
  await expect(dialog).toContainText('Plus 1 extra place granted this session.');
});

test('per-guest permissions: one guest’s playback is off while another’s still plays', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const link = await inviteLink(page);
  const ann = await openGuest(page, link, 'Ann');
  const bob = await openGuest(page, link, 'Bob');
  for (const guest of [ann, bob])
    await expect(guest.locator('header .status')).toHaveText('Connected');
  await page.getByRole('button', { name: 'Access settings' }).click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await dialog.getByLabel('Permissions for Ann').click();
  await dialog.getByLabel('Remote playback for Ann').selectOption({ label: 'Off' });
  await expect(dialog.getByLabel('Permissions for Ann')).toContainText('(1 own)');
  await page.getByRole('button', { name: 'Close dialog' }).click();
  // Ann's page shows her own permissions; the host page refuses her play even when sent anyway.
  await expect(playButton(ann, 'Airhorn')).toBeDisabled();
  expect(await sendCommand(ann, 'play')).toEqual({
    error: 'The host has disabled this action.',
  });
  expect(harness.plays.filter((p) => p.startsWith('DoPlaySound('))).toEqual([]);
  await playButton(bob, 'Airhorn').click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
  // Same as everyone again.
  await page.getByRole('button', { name: 'Access settings' }).click();
  await dialog.getByLabel('Permissions for Ann').click();
  await dialog.getByLabel('Remote playback for Ann').selectOption({ label: 'Same as everyone' });
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await expect(playButton(ann, 'Airhorn')).toBeEnabled();
});

test('example session: plays example sounds in this browser, edits a copy, and leaves', async ({
  page,
  harness,
  browserName,
}) => {
  // The example never reaches the companion, Soundpad or a relay: no socket but the dev server's own.
  const sockets: string[] = [];
  page.on('websocket', (socket) => {
    const url = new URL(socket.url());
    if (!(url.host === `127.0.0.1:${PORT}` && url.pathname === '/')) sockets.push(socket.url());
  });
  await page.goto('/');
  await expect(page.getByRole('button', { name: /Play / })).toHaveCount(0);
  await expect(page.locator('#try-example-note')).toHaveText(
    // What can actually be tried (2.41), not "every feature".
    'Try the soundboard, edited copies, Mimic and simulated guests with example sounds. Nothing is sent to Soundpad; sounds play in this browser.',
  );
  await page.getByRole('button', { name: 'Try the example' }).click();
  // Playwright's Windows WebKit has no Web Audio at all (real Safari has it): the example must say it cannot
  // start, and stay on the start screen, rather than fail silently (2.38.0 CI).
  if (
    browserName === 'webkit' &&
    !(await page.evaluate(() => 'AudioContext' in window || 'webkitAudioContext' in window))
  ) {
    await expect(
      page.getByText(
        /The example could not start. This browser cannot play audio from a web page./,
      ),
    ).toBeVisible();
    await expect(
      page.getByRole('heading', { name: 'A soundboard. For the whole party.' }),
    ).toBeVisible();
    expect(sockets).toEqual([]);
    return;
  }
  await expect(page.getByRole('complementary', { name: 'Example session' })).toContainText(
    'Example session: nothing here reaches Soundpad.',
  );
  // Leaving clears the example's audio; favourites and view settings stay (2.41).
  await expect(page.getByRole('complementary', { name: 'Example session' })).toContainText(
    'Leaving clears the example’s audio, copies and Mimic clips; your favourites and view settings stay.',
  );
  await expect(page.locator('header .status')).toHaveText('Example session');
  // Mimic in the example is labelled as the older flow, and its hotkey promises nothing outside this page.
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('dialog', { name: 'Mimic' });
  await expect(mimic.locator('.mimic-intro')).toContainText('The example shows the older Mimic');
  await expect(mimic).toContainText("Host's PC only: there, pressing F6 in any app saves the last");
  await expect(mimic).not.toContainText('Press F6 in any app on this PC');
  await mimic.getByRole('button', { name: 'Close dialog' }).click();
  // There is no companion to update (the example stands in for one without the 2.39 recording history).
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByRole('dialog').first()).toBeVisible();
  await expect(page.getByText('A newer one adds', { exact: false })).toHaveCount(0);
  await page.keyboard.press('Escape');
  // Every example sound and category is there, and playing one shows it in the player.
  await expect(
    page.getByRole('navigation', { name: 'Sound categories' }).getByRole('button'),
  ).toHaveCount(6);
  // The example is the first library here with artwork. Artwork tiles are named "Play artwork <name>"
  // while they show "<name>" and a hint, which axe's label-content-name-mismatch flags; that naming is
  // older than the example and other tests rely on it, so the rule is left out here.
  const { violations } = await new AxeBuilder({ page })
    .disableRules(['label-content-name-mismatch'])
    .analyze();
  expect(
    violations
      .filter((v) => v.impact === 'serious' || v.impact === 'critical')
      .map((v) => `${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`),
  ).toEqual([]);
  const march = page.getByRole('button', {
    name: "Play from start: Columbia's Pride (Sousa)",
    exact: true,
  });
  await expect(march).toBeEnabled();
  await march.click();
  await expect(page.locator('.player-track')).toContainText("Columbia's Pride (Sousa)");
  await expect(page.locator('.player-track')).toContainText('Requested by Host · playing');
  // The newest click wins.
  await page.getByRole('button', { name: 'Play from start: Boing', exact: true }).click();
  await expect(page.locator('.player-track strong')).toHaveText('Boing');
  // Temp: reverse a sound, then make an edited copy of it.
  await page.getByRole('button', { name: 'Reverse and play Dog Bark' }).click();
  await expect(page.locator('.player-track strong')).toHaveText('Dog Bark (reversed)');
  await page.locator('#temp-manager > summary').click();
  const source = page.locator('.temp-source').filter({ hasText: 'Dog Bark' });
  await source.getByRole('button', { name: 'Edit copy' }).click();
  const form = page.getByRole('form', { name: 'Edit a copy of Dog Bark' });
  await form.getByLabel('Effect').selectOption('speed');
  await form.getByLabel('Exact speed (×)').fill('2');
  await form.getByLabel('Keep pitch').check();
  await form.getByRole('button', { name: 'Save copy' }).click();
  await expect(page.getByText('Copy added to Temp. The original is unchanged.')).toBeVisible();
  await expect(
    page.getByRole('button', {
      name: 'Play from start: Dog Bark (2x speed, same pitch)',
      exact: true,
    }),
  ).toBeVisible();
  // Keep needs the host's PC, and says so.
  await source.getByRole('button', { name: 'Keep reversed permanently' }).click();
  await page.getByRole('button', { name: 'Confirm permanent save' }).click();
  await expect(page.locator('#temp-manager [role="alert"]')).toContainText(
    'Not available in the example',
  );
  // ...and keeps saying so: the Temp list refreshing after the new copy must not hide the refusal.
  await page.waitForTimeout(1500);
  await expect(page.locator('#temp-manager [role="alert"]')).toContainText(
    'Not available in the example',
    { timeout: 100 },
  );
  // Leaving returns to the start page, with nothing left playing.
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.getByRole('button', { name: 'Try the example' })).toBeVisible();
  await expect(page.getByText('Not connected', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /Play / })).toHaveCount(0);
  expect(sockets).toEqual([]);
  expect(harness.plays).toEqual([]);
});

test('notices never cover Mimic’s close button, Leave example, Stop, Panic or Settings, and Undo stays reachable', async ({
  page,
  browserName,
}) => {
  // Review finding (2.41): stacked notices at the top right covered the Mimic dialog's close button and Leave
  // example, and Tab could not reach their Undo from inside the dialog.
  test.skip(browserName === 'webkit', 'Playwright’s Windows WebKit cannot start the example');
  /** True when a press in the middle of the control lands on the control itself. */
  const onTop = (control: Locator) =>
    control.evaluate((el) => {
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
      return !!hit && (hit === el || el.contains(hit));
    });
  for (const size of [
    { width: 1280, height: 720 },
    { width: 1440, height: 900 },
    { width: 412, height: 860 },
  ]) {
    await page.setViewportSize(size);
    await page.goto('/');
    await page.getByRole('button', { name: 'Try the example' }).click();
    await page.getByRole('button', { name: 'Mimic', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Mimic' });
    await dialog.getByRole('button', { name: 'Start listening' }).click();
    await expect(dialog.getByRole('button', { name: 'Last 3 s' })).toBeVisible({ timeout: 15000 });
    await page.waitForTimeout(3200);
    for (let i = 0; i < 2; i++) {
      await dialog.getByRole('button', { name: 'Last 3 s' }).click();
      await dialog.getByRole('button', { name: 'Mimic it' }).click();
    }
    const undo = page.getByRole('button', { name: 'Undo' }).first();
    await expect(undo).toBeVisible();
    // The close button is never under the notices, which are part of the dialog.
    const close = dialog.getByRole('button', { name: 'Close dialog' });
    expect(await onTop(close), `close at ${size.width}`).toBe(true);
    expect(await onTop(undo), `Undo at ${size.width}`).toBe(true);
    await expect(dialog.getByRole('button', { name: 'Undo' }).first()).toBeVisible();
    // Lying over the dialog's content (its waveform when scrolled), a notice lets presses and drags through
    // to it; only its buttons take the pointer.
    const message = dialog.locator('.notification-stack .toast span').first();
    expect(
      await message.evaluate((el) => {
        const r = el.getBoundingClientRect();
        const hit = document.elementFromPoint(r.x + 4, r.y + r.height / 2);
        return !!hit && !hit.closest('.notification-stack');
      }),
      `notice passes the pointer through at ${size.width}`,
    ).toBe(true);
    // Keyboard: Shift+Tab from Close reaches a notice inside the dialog's focus trap.
    await close.focus();
    let reached = false;
    for (let i = 0; i < 60 && !reached; i++) {
      await page.keyboard.press('Shift+Tab');
      reached = await page.evaluate(
        () => !!document.activeElement?.closest('[role="dialog"] .notification-stack'),
      );
    }
    expect(reached, `Tab to a notice at ${size.width}`).toBe(true);
    await close.click();
    await expect(dialog).toHaveCount(0);
    // On the board with notices still showing: every critical control is on top.
    await expect(page.locator('.notification-stack .toast').first()).toBeVisible();
    for (const control of [
      page.getByRole('button', { name: 'Leave example' }),
      page.locator('.board-actions').getByRole('button', { name: 'Mimic', exact: true }),
      page.getByRole('button', { name: /^Access settings/ }),
      page.locator('.transport .stop'),
      page.getByRole('button', { name: 'Panic: stop audio and pause guest playback' }),
      undo,
    ])
      expect(await onTop(control), `${await control.textContent()} at ${size.width}`).toBe(true);
    // Reopen, close, and leave, all with the pointer.
    await page
      .locator('.board-actions')
      .getByRole('button', { name: 'Mimic', exact: true })
      .click();
    await dialog.getByRole('button', { name: 'Close dialog' }).click();
    await page.getByRole('button', { name: 'Leave example' }).click();
    await expect(page.getByRole('button', { name: 'Try the example' })).toBeVisible();
  }
});

test('Mimic history: dragging the overview keeps one request of a kind in flight, and Stop and Play stay prompt', async ({
  page,
  harness,
}) => {
  // Review finding (2.41): every pointer move restarted the fetch loop, sending a new timeline, levels and
  // transcript request while the earlier ones were still on their way.
  test.slow();
  const kinds = ['historyTimeline', 'historyLevels', 'historyTranscript'];
  const open = new Map<string, string>();
  const most: Record<string, number> = {};
  const sent: Record<string, number> = {};
  const inFlight = (op: string) => [...open.values()].filter((o) => o === op).length;
  page.on('websocket', (socket) => {
    socket.on('framesent', ({ payload }) => {
      try {
        const op = JSON.parse(String(payload)).command;
        if (!op || !kinds.includes(op.op)) return;
        open.set(op.id, op.op);
        sent[op.op] = (sent[op.op] ?? 0) + 1;
        most[op.op] = Math.max(most[op.op] ?? 0, inFlight(op.op));
      } catch {
        /* Ignore non-JSON transport frames. */
      }
    });
    socket.on('framereceived', ({ payload }) => {
      try {
        const reply = JSON.parse(String(payload));
        if (typeof reply.id === 'string') open.delete(reply.id);
      } catch {
        /* Ignore non-JSON transport frames. */
      }
    });
  });
  await host(page, harness);
  await playButton(page, 'Airhorn').click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoPlaySound(1)');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await dialog.getByLabel('Transcribe Mimic audio').check();
  const overview = dialog.getByRole('slider', { name: /Recording history overview/ });
  await expect(overview).toBeVisible();
  await page.waitForTimeout(8000);
  const box = (await overview.boundingBox())!;
  for (const op of kinds) {
    most[op] = 0;
    sent[op] = 0;
  }
  const started = Date.now();
  // Three drags back and forth, 40 moves each, as fast as the pointer goes.
  await page.mouse.move(box.x + box.width - 4, box.y + box.height / 2);
  await page.mouse.down();
  for (const to of [box.x + 8, box.x + box.width * 0.6, box.x + box.width * 0.3])
    await page.mouse.move(to, box.y + box.height / 2, { steps: 40 });
  await page.mouse.up();
  const dragMs = Date.now() - started;
  // The outline follows at once: the view is no longer live.
  await expect(overview).toHaveAttribute('aria-valuetext', /before live/);
  // Stop right after the drag (the window closed, the drag's reads still answering) is prompt, and so is a Play.
  const stopped = Date.now();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect.poll(() => harness.soundpad.at(-1), { timeout: 2000 }).toBe('DoStopSound()');
  expect(Date.now() - stopped).toBeLessThan(1500);
  await playButton(page, 'Airhorn').click();
  await expect.poll(() => harness.soundpad.at(-1), { timeout: 2000 }).toBe('DoPlaySound(1)');
  await page.waitForTimeout(600);
  for (const op of kinds) expect(most[op], `${op} in flight at once`).toBeLessThanOrEqual(1);
  // A pass per ~120 ms at most while dragging (plus the ordinary polling), never one per move.
  expect(sent.historyLevels).toBeLessThanOrEqual(Math.ceil((dragMs + 2000) / 120) + 4);
  expect(sent.historyLevels).toBeLessThan(120);
});

// ---- Voice library (2.41) ------------------------------------------------------------------------

/** Keeps the last ten seconds of the history in the voice library, as a draft's Keep in voice library does. */
async function keepRecentInLibrary(page: Page, name: string) {
  const draft = (await sendCommand(page, 'draftCreate', { action: 'recent', name })) as {
    data?: { id: string };
    error?: string;
  };
  expect(draft.error).toBeUndefined();
  const kept = (await sendCommand(page, 'draftKeep', {
    draftId: draft.data!.id,
    action: 'library',
  })) as { data?: { recordingId: string }; error?: string };
  expect(kept.error).toBeUndefined();
  return kept.data!.recordingId;
}
async function startListening(page: Page) {
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText('Listening to Fixture');
  return dialog;
}

test('voice library: a kept draft appears in Voices with its words, a word search finds it, and the word plays into the game', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const dialog = await startListening(page);
  await dialog.getByLabel('Transcribe Mimic audio').check();
  await expect(
    dialog
      .getByRole('list', { name: 'Transcript' })
      .getByRole('button', { name: 'Fixture', exact: true })
      .first(),
  ).toBeVisible({ timeout: 15000 });
  await dialog.getByRole('button', { name: 'Last 3 s', exact: true }).click();
  await dialog.getByLabel('Clip name').fill('Library line');
  await dialog.getByRole('button', { name: 'Capture', exact: true }).click();
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await draft.getByRole('button', { name: 'Keep in voice library' }).click();
  await expect(page.getByText('“Library line” is in the voice library.')).toBeVisible({
    timeout: TEMP_READY,
  });
  await expect(draft).toHaveCount(0); // kept: the draft is done
  await dialog.getByRole('button', { name: 'Close dialog' }).click();
  await page.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = page.getByRole('dialog', { name: 'Voices' });
  const recordings = voices.getByRole('list', { name: 'Recordings' });
  await expect(recordings).toContainText('Library line');
  // Its words: copied from the history's transcript (or found in the background), "Fixture tone N".
  await expect(recordings).toContainText(/Fixture tone \d+/, { timeout: 20000 });
  await voices.getByLabel('Search the voice library').fill('tone');
  const results = voices.getByRole('list', { name: 'Search results' });
  const result = results.getByRole('listitem').filter({ hasText: 'Library line' });
  await expect(result.getByText('Voice clip', { exact: true })).toBeVisible();
  await expect(result.locator('mark').first()).toHaveText('tone');
  // Play exactly that word into the game: a region of the kept sound, not the whole clip.
  await voices.getByRole('radio', { name: /Play into game/ }).check();
  const before = harness.plays.length;
  await result.getByRole('button', { name: 'Play “tone” (match)' }).first().click();
  await expect.poll(() => harness.plays.slice(before).join(' ')).toMatch(/DoPlaySound\(\d+\)/);
  await expect.poll(() => harness.plays.slice(before).join(' ')).toMatch(/DoSeekMs\(\d+\)/);
});

test('voice library: a guest tags a recording and takes it back with Undo; the host sees who did it', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await startListening(page);
  await page.keyboard.press('Escape');
  const id = await keepRecentInLibrary(page, 'Shared line');
  const guest = await joinGuest(page);
  await guest.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = guest.getByRole('dialog', { name: 'Voices' });
  await voices
    .getByRole('list', { name: 'Recordings' })
    .getByRole('button', { name: 'Shared line', exact: true })
    .click();
  const detail = voices.getByRole('region', { name: 'Recording Shared line' });
  await detail.getByLabel('Add a tag').fill('funny');
  await detail.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(detail.getByLabel('Tags')).toContainText('funny');
  await expect(guest.locator('.notification-stack').getByText('Tag “funny” added.')).toBeVisible();
  await guest.locator('.notification-stack').getByRole('button', { name: 'Undo' }).click();
  await expect(detail.getByLabel('Tags')).not.toContainText('funny');
  // The host sees both changes, by name.
  const audit = (await sendCommand(page, 'libraryAudit', { recordingId: id })) as {
    data: { entries: { byName: string; field: string; undoOf: number | null }[] };
  };
  const tags = audit.data.entries.filter((e) => e.field === 'tags');
  expect(tags.map((e) => [e.byName, !!e.undoOf])).toEqual([
    ['Fixture Guest', true],
    ['Fixture Guest', false],
  ]);
  // With guest editing off, the guest can still look but no longer change it.
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guests to edit the voice library').uncheck();
  await page.keyboard.press('Escape');
  await expect(detail.getByLabel('Add a tag')).toBeDisabled();
});

test('voice library: the host indexes a Soundpad category and finds its sound by a word', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = page.getByRole('dialog', { name: 'Voices' });
  await voices.getByRole('tab', { name: 'Word index' }).click();
  await voices.getByRole('checkbox', { name: /^Reactions/ }).check();
  await voices.getByRole('button', { name: 'Save categories' }).click();
  // "Reactions" holds Laugh and, in "Deep cuts", Airhorn: each is read once, in the background.
  await expect(voices.getByRole('status').filter({ hasText: 'sounds indexed' })).toContainText(
    '2 of 2 sounds indexed',
    { timeout: 30000 },
  );
  await voices.getByRole('tab', { name: 'Library' }).click();
  await voices.getByLabel('Search the voice library').fill('fixture');
  const results = voices.getByRole('list', { name: 'Search results' });
  const laugh = results.getByRole('listitem').filter({ hasText: 'Laugh' });
  await expect(laugh.getByText('Sound', { exact: true })).toBeVisible();
  await expect(laugh.locator('mark').first()).toHaveText(/fixture/i);
  // Filtering out Sounds hides them.
  await voices
    .getByRole('group', { name: 'Result types' })
    .getByRole('button', { name: /^Sounds/ })
    .click();
  await expect(laugh).toHaveCount(0);
});

test('voice library: the review queue labels and keeps new recordings from the keyboard', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await startListening(page);
  await page.keyboard.press('Escape');
  await keepRecentInLibrary(page, 'First new');
  await page.waitForTimeout(1200); // a different stretch for the second one
  await keepRecentInLibrary(page, 'Second new');
  expect(
    (await sendCommand(page, 'libraryVoice', { action: 'create', name: 'Robo' })).error,
  ).toBeUndefined();
  await page.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = page.getByRole('dialog', { name: 'Voices' });
  await voices.getByRole('tab', { name: 'Review (2)' }).click();
  const queue = voices.getByRole('region', { name: 'Review queue' });
  await expect(queue).toBeFocused();
  await expect(queue).toContainText('1 of 2');
  await expect(queue).toContainText('First new');
  await page.keyboard.press('1'); // the first voice
  await expect(queue.getByRole('button', { name: /Robo/ })).toHaveAttribute('aria-pressed', 'true');
  await page.keyboard.press('Enter'); // keep: reviewed, next
  await expect(queue).toContainText('2 of 2');
  await expect(queue).toContainText('Second new');
  await page.keyboard.press('s'); // skip
  await expect(queue).toContainText('All caught up. 1 kept, 1 skipped.');
  await expect(voices.getByRole('tab', { name: 'Review (1)' })).toBeVisible();
  const list = (await sendCommand(page, 'libraryList', { reviewed: true })) as {
    data: { items: { title: string; voiceId: string | null }[] };
  };
  expect(list.data.items.map((r) => r.title)).toEqual(['First new']);
  expect(list.data.items[0].voiceId).toMatch(/^[a-f0-9]{32}$/);
});
