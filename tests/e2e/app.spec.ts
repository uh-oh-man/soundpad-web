import { test as base, expect, type Page } from '@playwright/test';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { readFile } from 'node:fs/promises';
type Harness = { launch: string; plays: string[] };
async function startHarness(origin = 'http://127.0.0.1:5173') {
  const proc = spawn(
    resolve('companion/harness/bin/Release/net10.0-windows/win-x64/SoundpadWeb.Harness.exe'),
    [origin],
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
    await g1.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
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
    await expect(
      g1.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img'),
    ).toBeVisible();
    await expect(
      g2.getByRole('button', { name: 'Play Airhorn', exact: true }).locator('img'),
    ).toBeVisible();
    await page.getByRole('button', { name: 'Access settings' }).click();
    page.once('dialog', (d) => d.accept());
    await page.getByRole('button', { name: 'Replace invite' }).click();
    await expect(g2.getByText(/Access revoked/)).toBeVisible();
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
}) => {
  test.skip(!process.env.DEPLOYED_URL, 'Requires an actual deployed website URL.');
  const website = new URL(process.env.DEPLOYED_URL!);
  const harness = await startHarness(website.origin);
  try {
    if (browserName === 'chromium') await page.context().grantPermissions(['local-network-access']);
    await host(page, { ...harness, launch: website.href + '#' + harness.launch.split('#')[1] });
    await page.getByRole('button', { name: 'Play Airhorn', exact: true }).click();
    await expect.poll(() => harness.plays).toContain('DoPlaySound(1)');
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await page.getByRole('button', { name: 'Increase Kick counter' }).click();
    await expect(page.getByLabel('Kick count', { exact: true })).toHaveText('1');
  } finally {
    await harness.stop();
  }
});
