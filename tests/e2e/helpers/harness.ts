/**
 * The shared e2e harness (Stage G, Phase 0): the mock-Soundpad companion, the test fixture with its
 * failure diagnostics, and the steps nearly every spec takes (host, invite, join a guest). Shared by
 * app.spec.ts and the Stage G specs; owned by Phase 0 and frozen during the parallel packages.
 */
import { test as base, expect, type Page } from '@playwright/test';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
export type Harness = {
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
export const TEMP_READY = process.env.CI ? 45000 : 25000;
// The dev server's port: 5173 unless E2E_PORT says otherwise (a second checkout running the suite at the same time).
export const PORT = process.env.E2E_PORT ?? '5173';
export const ORIGIN = `http://127.0.0.1:${PORT}`;
export async function startHarness(origin = ORIGIN, count = 4, options: string[] = []) {
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
export const test = base.extend<{ harness: Harness; diagnostics: void; harnessOptions: string[] }>({
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
export async function host(page: Page, harness: Harness) {
  await page.goto(harness.launch);
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeEnabled();
}
export async function invite(page: Page) {
  await page.getByRole('button', { name: 'Access settings' }).click();
  const url = await page.locator('#invite').inputValue();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  return url;
}
export async function finishGuestName(page: Page, name = 'Fixture Guest') {
  const dialog = page.getByRole('dialog', { name: /gamer name/i });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Gamer name').fill(name);
  await dialog.getByRole('button', { name: 'Done', exact: true }).click();
}
export const playButton = (page: Page, name: string, nth = 0) =>
  page.getByRole('button', { name: `Play from start: ${name}`, exact: true }).nth(nth);
/**
 * Lets a guest page join the host page inside this browser context with no relay and no WebRTC: the
 * dev server's BroadcastChannel transport (web/src/network/test-transport.ts) carries the session.
 * Call before the host page loads. A production build has no such switch.
 */
export async function useLocalGuests(page: Page) {
  await page
    .context()
    .addInitScript(() => localStorage.setItem('soundpad-test-transport', 'broadcast'));
}
/** Opens the host's invitation in a second page of the same context and waits until it is connected. */
export async function joinGuest(hostPage: Page, name = 'Fixture Guest') {
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
/** Sends one command from the page as its controller would, without the page's own checks. */
export const sendCommand = (page: Page, op: string, fields: Record<string, unknown> = {}) =>
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
export { expect };
