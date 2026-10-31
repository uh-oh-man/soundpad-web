import { test as base, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
type Harness = { launch: string; plays: string[] };
async function startHarness(origin = 'http://127.0.0.1:5173', count = 4) {
  const proc = spawn(
    resolve(
      'companion/harness/bin/Release/net10.0-windows10.0.19041.0/win-x64/SoundpadWeb.Harness.exe',
    ),
    [origin, String(count)],
    { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] },
  );
  const plays: string[] = [];
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
      output += chunk.toString();
      const ready = /TEST_READY (\{[^\n]+\})/.exec(output);
      if (ready) {
        clearTimeout(timeout);
        resolve(JSON.parse(ready[1]));
      }
      for (const match of chunk.toString().matchAll(/TEST_PLAY ([^\r\n]+)/g)) plays.push(match[1]);
    });
    proc.stderr.on('data', (chunk) => {
      if (chunk.toString().includes('Unhandled')) reject(Error('Test harness error.'));
    });
  });
  return {
    launch: `${origin}/#host=${address.port}.${address.token}`,
    plays,
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
const test = base.extend<{ harness: Harness }>({
  harness: async ({}, use) => {
    const harness = await startHarness();
    try {
      await use(harness);
    } finally {
      await harness.stop();
    }
  },
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
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export layout', exact: true }).click();
  const download = await downloadPromise;
  const exported = JSON.parse(await readFile((await download.path())!, 'utf8'));
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
  const large = await startHarness('http://127.0.0.1:5173', 10000);
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
function sessionWave() {
  const frames = 48000,
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

test('session uploads use temp, reverse immediately, and only approved files survive End Hosting', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Session tone.wav', mimeType: 'audio/wav', buffer: sessionWave() });
  const sound = page.getByRole('button', { name: 'Play from start: Session tone', exact: true });
  await expect(sound).toBeEnabled();
  await expect(
    page
      .getByRole('navigation', { name: 'Sound categories' })
      .getByRole('button', { name: /^Temp · Soundpad Web.*1$/ }),
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
  await expect(
    page.getByRole('button', { name: 'Play from start: Session tone (reversed)', exact: true }),
  ).toHaveCount(0);
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
  await page.mouse.wheel(0, -240);
  await expect(footer.getByRole('button', { name: 'Fit waveform Airhorn' })).toBeVisible();
  await footer.getByRole('button', { name: 'Edit', exact: true }).click();
  await footer.getByLabel('Cue time seconds', { exact: true }).fill('0.123');
  await footer.getByRole('button', { name: 'Seek selected position Airhorn' }).click();
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
  const harness = await startHarness('http://127.0.0.1:5173', 300);
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
  ).toBeEnabled();
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
}) => {
  await host(page, harness);
  await expect(page.locator('.mimic-badge')).toHaveCount(0);
  const opener = page.getByRole('button', { name: 'Mimic', exact: true });
  await opener.click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await expect(dialog.getByLabel('Mimic source', { exact: true })).toHaveValue('fixture:tone');
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status')).toContainText('Listening to Fixture: tone every 2 s');
  await expect(page.getByRole('button', { name: 'Mimic on' })).toBeVisible();
  await expect(
    page.getByText('Mimic is listening to Fixture: tone every 2 s.', { exact: false }),
  ).toBeVisible();
  await expect(dialog.getByRole('slider', { name: /Mimic buffer/ })).toBeVisible();
  // Let the fixture produce at least one tone burst, then grab it with a quick select.
  await page.waitForTimeout(2600);
  await dialog.getByRole('button', { name: 'Last 3 s' }).click();
  await expect(dialog.locator('.mimic-range')).toContainText('3.0 s');
  // Private preview: plays in this tab only, saves nothing, and Soundpad plays nothing.
  const playsBefore = harness.plays.length;
  await dialog.getByRole('button', { name: 'Preview' }).click();
  await expect(dialog.getByRole('button', { name: 'Stop preview' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Preview' })).toBeVisible({ timeout: 8000 });
  expect(harness.plays.length).toBe(playsBefore);
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
  // Starts as the whole clip (just under 3 s: Mimic started listening 2.6 s before this clip).
  await expect(trim.getByLabel('End (s)')).toHaveValue(/^2\.\d+$/);
  await trim.getByLabel('Start (s)').fill('0.5');
  await trim.getByLabel('End (s)').fill('2');
  await expect(trim).toContainText(/1\.50 s of 2\.\d\d s/);
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
  // Dragging selects an exact range; Clear returns to the live view.
  const canvas = dialog.locator('.mimic-wave canvas');
  const box = (await canvas.boundingBox())!;
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
  // The F6 hotkey is host-only and reports its state (the harness uses a fixture key, never real F6).
  await dialog.getByLabel('F6 hotkey').check();
  await expect(dialog.getByText('Press F6 in any app to save the last 10 s')).toBeVisible();
  await dialog.getByLabel('F6 saves the last').selectOption('5');
  await expect(dialog.getByText('Press F6 in any app to save the last 5 s')).toBeVisible();
  await dialog.getByLabel('F6 hotkey').uncheck();
  await expect(dialog.getByLabel('F6 hotkey')).not.toBeChecked();
  await dialog.getByRole('button', { name: 'Stop listening' }).click();
  await expect(page.locator('.mimic-badge')).toHaveCount(0);
  await expect(page.getByText('Mimic stopped listening.')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.getByLabel('Allow guest Mimic')).not.toBeChecked();
});

test('Mimic transcript: lines appear, a click selects them, and they name the clip', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await dialog.getByLabel('Transcribe Mimic audio').check();
  const transcript = dialog.getByRole('list', { name: 'Transcript' });
  const line = transcript.getByRole('button', { name: /Fixture tone \d+/ }).first();
  await expect(line).toBeVisible({ timeout: 15000 });
  const words = (await line.locator('span').innerText()).trim();
  await line.click();
  await expect(line).toHaveAttribute('aria-pressed', 'true');
  await expect(dialog.getByLabel('Clip name')).toHaveAttribute('placeholder', words);
  await dialog.getByRole('button', { name: 'Mimic it' }).click();
  await expect(
    page.getByRole('button', { name: `Play from start: ${words}`, exact: true }),
  ).toBeEnabled();
  await dialog.getByLabel('Transcribe Mimic audio').uncheck();
  await expect(transcript).toHaveCount(0);
});

test('Mimic keyboard: number keys grab recent audio, Enter mimics, Escape clears before closing', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  // Shortcuts still work right after ticking a checkbox (focus stays on it).
  await dialog.getByLabel('F6 hotkey').check();
  await expect(dialog.getByLabel('F6 hotkey')).toBeFocused();
  await page.keyboard.press('5');
  await expect(dialog.locator('.mimic-range')).toContainText('5.0 s');
  const name = dialog.getByLabel('Clip name');
  await expect(name).toBeFocused();
  await name.fill('Key mimic');
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('button', { name: 'Play from start: Key mimic', exact: true }),
  ).toBeEnabled();
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  await page.keyboard.press('3');
  await expect(dialog.locator('.mimic-range')).toContainText('3.0 s');
  await page.keyboard.press('Escape');
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
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
    // The host's transcript reaches the guest as text; clicking a line selects that audio.
    await expect(
      dialog
        .getByRole('list', { name: 'Transcript' })
        .getByRole('button', { name: /Fixture tone/ })
        .first(),
    ).toBeVisible({ timeout: 20000 });
    await dialog.getByRole('button', { name: 'Last 3 s' }).click();
    await expect(dialog.getByRole('button', { name: 'Preview' })).toHaveCount(0); // audio never goes to guests
    await dialog.getByLabel('Clip name').fill('Guest mimic');
    await dialog.getByRole('button', { name: 'Mimic it' }).click();
    await expect(
      page.getByRole('button', { name: 'Play from start: Guest mimic', exact: true }),
    ).toBeEnabled();
    // Everyone hears about a new clip, and can replay it from the shared list.
    await expect(page.getByText('Mimic Alice mimicked \u201cGuest mimic\u201d')).toBeVisible();
    await expect(
      dialog
        .getByRole('region', { name: 'Just mimicked' })
        .getByRole('button', { name: 'Replay Guest mimic' }),
    ).toBeVisible();
  } finally {
    await context.close();
  }
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

test('Mimic fits a phone: header keeps Settings on screen and long clip names do not widen the window', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Mimic' });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await page.waitForTimeout(2600);
  await page.keyboard.press('5');
  await dialog
    .getByLabel('Clip name')
    .fill('A rather long clip name that would wrap on a phone screen');
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
