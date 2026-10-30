import { test as base, expect, type Page } from '@playwright/test';
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
  await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled();
}
async function invite(page: Page) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  const url = await page.locator('#invite').inputValue();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  return url;
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
  await expect(page.getByRole('button', { name: 'Play Laugh', exact: true })).toHaveCount(2);
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
  await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toHaveCount(1);
  await page.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
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
    page.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img'),
  ).toBeVisible();
  await page.reload();
  await expect(
    page.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img'),
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
      await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toHaveCount(1);
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
    await expect(g1.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled({
      timeout: 60000,
    });
    await expect(g2.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled({
      timeout: 60000,
    });
    await g1.getByLabel('Your display name').fill('Relay Alice');
    await g1.getByLabel('Your display name').blur();
    await g1.getByLabel('Search sounds').fill('Airhorn');
    await g1.getByLabel('View mode').selectOption('waveforms');
    await expect(g1.getByRole('slider', { name: 'Seek Airhorn', exact: true })).toBeVisible();
    await g1.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
    await g1.getByRole('button', { name: 'Pause playback', exact: true }).click();
    await expect(g1.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
    const remoteWave = g1
      .locator('.sound-entry')
      .getByRole('slider', { name: 'Seek Airhorn', exact: true });
    await remoteWave.press('Home');
    await remoteWave.press('ArrowRight');
    await remoteWave.press('Enter');
    await expect.poll(() => harness.plays).toContain('DoSeekMs(100)');
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
      g1.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img'),
    ).toBeVisible();
    await expect(
      g2.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img'),
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
    await expect(g1.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled({
      timeout: 60000,
    });
    await expect(g2.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled({
      timeout: 60000,
    });
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(g1.getByLabel('Kick count', { exact: true })).toHaveText('1');
    await expect(g2.getByLabel('Kick count', { exact: true })).toHaveText('0');
    await g2.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
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
    await expect(guest.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled({
      timeout: 60000,
    });
    await guest.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
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
    await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled();
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
    .filter({ has: page.getByRole('button', { name: 'Play Airhorn', exact: true }) })
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
  await wave.getByRole('button', { name: 'Zoom in Airhorn', exact: true }).click();
  await expect(wave.getByLabel('Waveform offset Airhorn')).toBeVisible();
  await wave.getByRole('button', { name: 'Play cue Opening', exact: true }).click();
  await expect.poll(() => harness.plays).toContain('DoSeekMs(700)');
  await page.getByRole('button', { name: 'Pause playback', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Resume playback', exact: true })).toBeEnabled();
  await wave.getByRole('slider', { name: 'Seek Airhorn', exact: true }).press('Home');
  await wave.getByRole('slider', { name: 'Seek Airhorn', exact: true }).press('ArrowRight');
  await wave.getByRole('slider', { name: 'Seek Airhorn', exact: true }).press('Enter');
  await expect.poll(() => harness.plays).toContain('DoSeekMs(100)');
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
  await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toHaveCount(1);
  const oldInvite = await invite(page);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByRole('button', { name: 'End hosting', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Resume hosting', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Resume hosting', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeEnabled();
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
  await expect(page.getByRole('button', { name: 'Play Airhorn', exact: true })).toBeVisible();
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
  const image = page.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img');
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
      page.getByRole('button', { name: 'Play Benchmark fixture 10000', exact: true }),
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
  await expect(page.getByText('V2', { exact: true })).toBeVisible();
  await expect(
    page.getByText('Update your Windows companion for V2', { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'Download the updated companion' })).toHaveAttribute(
    'href',
    /releases\/latest\/download\/SoundpadWeb\.exe$/,
  );
  await expect(page.getByLabel('View mode')).toBeDisabled();
  await page.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
  await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
});
