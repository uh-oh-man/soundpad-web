/**
 * 2.45 (WP2): one "Recover previous session / Discard" prompt after a crash (D05), a guest's storage
 * request that opens to their recent Temp and Keep clips (D06), and guest capture preselected in a new
 * host's first-run guide (O02). The harness cannot crash and start again on its own data, so the first
 * test stands in for the crashed launch's snapshot and answers sessionRecover itself (the companion side
 * is covered by companion/tests/DraftRecoveryTests.cs). The harness reports the release's version, so
 * snapshots below 2.45 are raised to it here: these tests run before the integration bump.
 */
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import {
  test,
  expect,
  host,
  joinGuest,
  ORIGIN,
  sendCommand,
  startHarness,
  useLocalGuests,
} from './helpers/harness';

type State = Record<string, unknown> & {
  companionVersion?: string;
  tempRecovery?: number | null;
  mimic?: { history?: { recoveredMs: number } | null } | null;
};
function raise(state: State) {
  const [major = 0, minor = 0] = String(state.companionVersion ?? '0.0')
    .split('.')
    .map(Number);
  if (state.companionVersion && (major < 2 || (major === 2 && minor < 45)))
    state.companionVersion = '2.45.0';
}
/** Raises every snapshot to 2.45; `answer` may reply to a command itself instead of the companion. */
async function companion245(
  page: Page,
  change: (state: State) => void = () => {},
  answer?: (command: { id: string; op: string; action?: string }) => unknown,
) {
  let last: Record<string, unknown> | null = null;
  let resend: (() => void) | null = null;
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    const forward = (frame: Record<string, unknown>) => {
      const copy = JSON.parse(JSON.stringify(frame)) as { state?: State };
      if (copy.state) {
        raise(copy.state);
        change(copy.state);
      }
      socket.send(JSON.stringify(copy));
    };
    resend = () => last && forward(last);
    socket.onMessage((message) => {
      const frame = JSON.parse(String(message));
      const reply = frame.command && answer?.(frame.command);
      if (reply === undefined) return server.send(message);
      socket.send(JSON.stringify({ type: 'ack', id: frame.command.id, ok: true, data: reply }));
    });
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.type === 'state') last = frame;
      forward(frame);
    });
  });
  // Sends the last snapshot again through `change` (after the test changed what it injects).
  return () => resend?.();
}

const MB = 1024 * 1024;

test('after a crash one prompt lists Temp, history and drafts, and Recover or Discard answers for all of them', async ({
  page,
  harness,
}) => {
  let crashed = true;
  const sent: string[] = [];
  const answer = {
    pending: true,
    temp: { count: 2, ms: null, bytes: 3 * MB },
    history: { count: 1, ms: 90000, bytes: 3 * MB },
    drafts: { count: 2, ms: 4500, bytes: 144000 },
  };
  const again = await companion245(
    page,
    (state) => {
      if (!crashed) return;
      state.tempRecovery = 2;
      if (state.mimic?.history) state.mimic.history.recoveredMs = 90000;
    },
    (command) => {
      if (command.op !== 'sessionRecover') return undefined;
      sent.push(command.action ?? 'status');
      if (command.action === 'recover' || command.action === 'discard') {
        crashed = false;
        setTimeout(again, 50);
      }
      return crashed ? answer : { ...answer, pending: false };
    },
  );
  await host(page, harness);
  const prompt = page.getByRole('alert', { name: 'Previous session', exact: true });
  await expect(prompt).toBeVisible();
  const parts = prompt.getByRole('list', { name: 'What the previous session left' });
  await expect(parts.getByRole('listitem')).toHaveText([
    '2 clips in Temp · 3.0 MB',
    '1 min 30 s of recording history · 3.0 MB',
    '2 drafts of yours (5 s) · 0.1 MB',
  ]);
  // One question: not the older Temp prompt, and no separate notice in Mimic.
  await expect(page.getByRole('button', { name: 'Recover previous session’s Temp' })).toHaveCount(
    0,
  );
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
  await expect(mimic.getByRole('button', { name: 'Start listening' })).toBeVisible();
  await expect(mimic.locator('.mimic-recovered')).toHaveCount(0);
  await page.keyboard.press('Escape');
  // Discard asks first; cancelling sends nothing.
  page.once('dialog', (dialog) => {
    expect(dialog.message()).toContain('Anything you kept stays');
    void dialog.dismiss();
  });
  await prompt.getByRole('button', { name: 'Discard' }).click();
  await expect(prompt).toBeVisible();
  expect(sent.filter((a) => a !== 'status')).toEqual([]);
  // Recover answers for everything at once, from the keyboard too.
  await prompt.getByRole('button', { name: 'Recover previous session' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('The previous session is back')).toBeVisible();
  await expect(prompt).toHaveCount(0);
  expect(sent.filter((a) => a !== 'status')).toEqual(['recover']);
});

test('with an older companion the Temp prompt stays as it was', async ({ page, harness }) => {
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => server.send(message));
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state) {
        frame.state.companionVersion = '2.44.0';
        frame.state.tempRecovery = 1;
      }
      socket.send(JSON.stringify(frame));
    });
  });
  await host(page, harness);
  await expect(page.getByRole('button', { name: 'Recover previous session’s Temp' })).toBeVisible();
  await expect(page.getByRole('alert', { name: 'Previous session', exact: true })).toHaveCount(0);
});

type HistoryView = { sessionId: string; fromFrame: number | null; rate: number };

test('guests can read only their own recent storage actions without requesting more space', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await page.keyboard.press('Escape');
  const guest = await joinGuest(page);
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const mimic = page.getByRole('main', { name: 'Mimic', exact: true });
  await mimic.getByRole('button', { name: 'Start listening' }).click();
  await expect(mimic.getByRole('status').first()).toContainText('Listening to Fixture');
  await expect
    .poll(async () => {
      const view = (await sendCommand(guest, 'historyTimeline')).data as HistoryView & {
        liveFrame: number;
      };
      return view ? view.liveFrame - (view.fromFrame ?? 0) : 0;
    })
    .toBeGreaterThan(3 * 48000);
  const view = (await sendCommand(guest, 'historyTimeline')).data as HistoryView;
  for (const [name, length] of [
    ['Guest clip one', 3],
    ['Guest clip two', 4],
  ] as const) {
    const result = await sendCommand(guest, 'mimicClip', {
      startFrame: view.fromFrame ?? 0,
      endFrame: (view.fromFrame ?? 0) + view.rate * length,
      sourceId: view.sessionId,
      name,
    });
    expect(result.error).toBeUndefined();
  }
  const own = (await sendCommand(guest, 'budgetActions', { guestId: 'someone-else' })).data as {
    guestId: string;
    actions: { name: string }[];
  };
  expect(own.actions.map((a) => a.name)).toEqual(['Guest clip two', 'Guest clip one']);
  expect(own.guestId).not.toBe('someone-else');
  await expect(page.getByRole('list', { name: 'Guests waiting for more storage' })).toHaveCount(0);
});

const guide = (page: Page) => page.getByRole('dialog', { name: 'Set up Soundpad Web' });
const guestMimic = (page: Page) =>
  page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    return controller.getSnapshot().state.permissions.guestMimic ?? false;
  });
async function toInvitations(page: Page) {
  const dialog = guide(page);
  await dialog.getByRole('button', { name: /Invitations/ }).click();
  return dialog.getByRole('checkbox', { name: /Guest Mimic/ });
}
const removeDir = (dir: string) => {
  try {
    rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  } catch {
    // A file still held for a moment: the temp folder is cleaned up later.
  }
};

test('a new host’s first-run guide starts with guest capture ticked, once; an upgraded install keeps its setting', async ({
  page,
  browser,
}) => {
  const dir = mkdtempSync(join(tmpdir(), 'soundpad-web-o02-e2e-'));
  const fresh = await startHarness(ORIGIN, 4, ['--first-run', '--data-dir', dir]);
  try {
    await page.goto(fresh.launch);
    await expect(page.locator('header .status')).toHaveAttribute('title', 'Host ready');
    await expect(guide(page)).toBeVisible();
    await expect.poll(() => guestMimic(page)).toBe(true);
    const box = await toInvitations(page);
    await expect(box).toBeChecked();
    // The host unticks it: it stays off, also when the guide opens again before setup is finished.
    await box.uncheck();
    await expect.poll(() => guestMimic(page)).toBe(false);
    await page.reload();
    await expect(page.locator('header .status')).toHaveAttribute('title', 'Host ready');
    await expect(guide(page)).toBeVisible();
    await page.waitForTimeout(1500);
    expect(await guestMimic(page)).toBe(false);
    await expect(await toInvitations(page)).not.toBeChecked();
  } finally {
    await fresh.stop();
    removeDir(dir);
  }

  // An install with kept data (here the 2.43 style file): the guide from its card changes nothing. A new
  // browser profile, so nothing this one remembered decides it.
  const context = await browser.newContext();
  page = await context.newPage();
  const kept = mkdtempSync(join(tmpdir(), 'soundpad-web-o02-e2e-'));
  writeFileSync(join(kept, 'ui.json'), JSON.stringify({ version: 1, theme: 'ocean' }));
  const upgraded = await startHarness(ORIGIN, 4, ['--first-run', '--data-dir', kept]);
  try {
    await page.goto(upgraded.launch);
    await expect(page.locator('header .status')).toHaveAttribute('title', 'Host ready');
    const card = page.getByRole('complementary', { name: 'Check your setup' });
    await card.getByRole('button', { name: 'Open setup' }).click();
    await expect(guide(page)).toBeVisible();
    await page.waitForTimeout(1500);
    expect(await guestMimic(page)).toBe(false);
    await expect(await toInvitations(page)).not.toBeChecked();
  } finally {
    await upgraded.stop();
    await context.close();
    removeDir(kept);
  }
});
