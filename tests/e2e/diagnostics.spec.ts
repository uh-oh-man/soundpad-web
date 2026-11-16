/**
 * Diagnostics and the layout preview (2.45, WP4): technical details on error notices, the host's Activity kept
 * in the companion across a reload, the companion log in Settings (host only, paths hidden on request), and the
 * miniature of My Layout in Copy into My Layout.
 */
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expect, host, joinGuest, sendCommand, test, useLocalGuests } from './helpers/harness';
import { editorPanel } from './helpers/shell';

/** A command the companion refuses, run as a control would (not quiet), so its error notice shows. */
const refusedCommand = (page: Page) =>
  page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    await controller.run('diagnosticsLog', { body: { lines: 0 } }).catch(() => {});
  });
const announcements = (page: Page) =>
  page.evaluate(
    () =>
      (window as unknown as { __soundpadAnnouncements?: string[] }).__soundpadAnnouncements ?? [],
  );
const REFUSAL = 'Ask for 1 to 500 log lines.';
test('an error notice has Technical details, reached by keyboard and announced once', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await refusedCommand(page);
  const card = page.getByRole('group', { name: 'Error notice' }).filter({ hasText: REFUSAL });
  await expect(card).toBeVisible();
  const summary = card.getByText('Technical details', { exact: true });
  await expect(summary).toBeVisible();
  // Closed, the details are not in the page: the message is found once.
  await expect(page.getByText(REFUSAL, { exact: true })).toHaveCount(1);
  // Tab from the card's Dismiss button reaches the disclosure; Enter opens it.
  await card.getByRole('button', { name: 'Dismiss notification' }).focus();
  await page.keyboard.press('Tab');
  await expect(summary).toBeFocused();
  await page.keyboard.press('Enter');
  const text = card.getByLabel('Technical details text');
  await expect(text).toContainText('Command: diagnosticsLog');
  await expect(text).toContainText('Page: host');
  await expect(text).toContainText(`Answer: ${REFUSAL}`);
  await expect(text).toContainText(/Command id: [0-9a-f-]{36}/);
  // While open the card stays, past its usual 8 s.
  await page.waitForTimeout(9000);
  await expect(card).toBeVisible();
  // Said once, as the notice; the details are never announced.
  const said = await announcements(page);
  expect(said.filter((s) => s === REFUSAL)).toHaveLength(1);
  expect(said.some((s) => s.includes('Command:'))).toBe(false);
});

test("the host's Activity comes back after a reload, with an error's details", async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('Kept before the reload.', 'success');
  });
  await refusedCommand(page);
  // Both reached the companion (they are sent a moment after they show).
  await expect
    .poll(async () => {
      const reply = await sendCommand(page, 'activityList');
      const entries = (reply.data as { entries?: { text: string }[] } | undefined)?.entries ?? [];
      return entries.map((e) => e.text);
    })
    .toEqual(expect.arrayContaining([REFUSAL, 'Kept before the reload.']));
  await page.reload();
  await expect(
    page.getByRole('button', { name: 'Play from start: Airhorn', exact: true }),
  ).toBeEnabled();
  await page.getByRole('button', { name: 'Activity' }).click();
  const panel = editorPanel(page);
  const kept = panel.getByRole('listitem').filter({ hasText: 'Kept before the reload.' });
  await expect(kept).toBeVisible();
  const error = panel.getByRole('listitem').filter({ hasText: REFUSAL });
  await expect(error).toBeVisible();
  await error.getByText('Technical details', { exact: true }).click();
  await expect(error.getByLabel('Technical details text')).toContainText('Command: diagnosticsLog');
  // Brought back, never re-sent: the companion still has each once.
  const reply = await sendCommand(page, 'activityList');
  const texts = (reply.data as { entries: { text: string }[] }).entries.map((e) => e.text);
  expect(texts.filter((t) => t === REFUSAL)).toHaveLength(1);
  expect(texts.filter((t) => t === 'Kept before the reload.')).toHaveLength(1);
  // Brought back as old news: marked restored, without buttons.
  const restored = await page.evaluate(async () => {
    const { noticeState } = await import(/* @vite-ignore */ '/src/' + 'notifications.ts');
    return noticeState()
      .history.filter((h: { restored?: boolean }) => h.restored)
      .map((h: { message: string; action?: unknown }) => [h.message, !!h.action]);
  });
  expect(restored.sort()).toEqual([
    [REFUSAL, false],
    ['Kept before the reload.', false],
  ]);
});

test.describe('with a companion log', () => {
  const dir = join(tmpdir(), `soundpad-wp4-log-${process.pid}-${Date.now()}`);
  test.use({ harnessOptions: ['--data-dir', dir] });
  test.beforeAll(() => {
    mkdirSync(dir, { recursive: true });
    const lines = [
      '2026-11-15 10:00:00.000 --- Soundpad Web 2.45.0 started ---',
      String.raw`2026-11-15 10:00:01.000 Imported C:\Users\Someone Else\Music\horn.wav`,
      '2026-11-15 10:00:02.000 error: Fixture failure for the log view',
    ];
    writeFileSync(join(dir, 'companion.log'), lines.join('\r\n') + '\r\n');
  });
  test.afterAll(() => {
    try {
      rmSync(dir, { recursive: true, force: true });
    } catch {
      /* a scanner may still hold a file; the temp folder is cleaned later */
    }
  });

  test('Settings shows the log tail to the host, hides paths on request, and never to a guest', async ({
    page,
    harness,
  }) => {
    await useLocalGuests(page);
      await host(page, harness);
    await page.getByRole('button', { name: 'Access settings' }).click();
    const settings = page.getByRole('dialog', { name: 'Session & access' });
    const section = settings.getByRole('region', { name: 'Companion log' });
    await section.getByRole('button', { name: 'Show the log' }).click();
    const log = section.getByLabel(/Companion log, last \d+ lines/);
    await expect(log).toContainText('Fixture failure for the log view');
    await expect(log).toContainText(String.raw`C:\Users\Someone Else\Music\horn.wav`);
    await section.getByLabel('Hide file paths').check();
    await expect(log).toContainText(String.raw`Imported …\horn.wav`);
    await expect(log).not.toContainText('Someone Else');
    await expect(section).toContainText('(file paths hidden)');
    await settings.getByRole('button', { name: 'Close dialog' }).click();

    const guest = await joinGuest(page);
    await guest.getByRole('button', { name: 'Access settings' }).click();
    const guestSettings = guest.getByRole('dialog', { name: 'Session & access' });
    await expect(guestSettings).toBeVisible();
    await expect(guestSettings.getByRole('region', { name: 'Companion log' })).toHaveCount(0);
    // Asked for anyway, it is refused before it reaches the log.
    const asked = await sendCommand(guest, 'diagnosticsLog');
    expect(asked.error).toBeTruthy();
    expect(JSON.stringify(asked)).not.toContain('Fixture failure');
    const activity = await sendCommand(guest, 'activityList');
    expect(activity.error).toBeTruthy();
  });
});

test('Copy into My Layout draws the resulting layout and redraws it as boxes are ticked', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page, 'Sharer');
  await guest.getByRole('button', { name: 'My Layout', exact: true }).click();
  // The guest's layout: a folder with Airhorn, Airhorn a favourite, and one Laugh hidden.
  await guest.getByRole('button', { name: 'Organize', exact: true }).click();
  await guest.getByRole('checkbox', { name: 'Select Airhorn', exact: true }).click();
  await guest.getByRole('button', { name: 'Add selected to…', exact: true }).click();
  await guest.getByRole('menuitem', { name: 'New folder…' }).click();
  await guest.getByRole('textbox', { name: 'New folder…' }).fill('Loud ones');
  await guest.getByRole('button', { name: 'Add', exact: true }).click();
  await guest.getByRole('button', { name: 'Done', exact: true }).press('Enter');
  await expect(guest.getByRole('button', { name: 'Organize', exact: true })).toHaveAttribute(
    'aria-pressed',
    'false',
  );
  await guest.getByRole('button', { name: 'Favorite Airhorn' }).first().click();
  await guest.getByRole('button', { name: 'Organize Laugh' }).first().click();
  await guest.getByRole('button', { name: 'Hide in My Layout' }).click();
  await guest.keyboard.press('Escape');

  const shared = page.getByRole('button', { name: /Sharer · 1 folders? · Copy into My Layout/ });
  await expect(shared).toBeVisible();
  // The guest's layout reaches the host a moment after each change: wait for the last one (the hidden Laugh).
  await expect
    .poll(() =>
      page.evaluate(async () => {
        const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
        const layouts = controller.getSnapshot().layouts as {
          name: string;
          organization: { hidden: string[]; favorites: string[] };
        }[];
        const sharer = layouts.find((l) => l.name === 'Sharer')?.organization;
        return sharer ? [sharer.favorites.length, sharer.hidden.length] : null;
      }),
    )
    .toEqual([1, 1]);
  await shared.click();
  const dialog = page.getByRole('dialog', { name: /Copy Sharer.s layout/ });
  const mini = dialog.getByRole('region', { name: 'My Layout after copying' });
  await expect(mini).toBeVisible();
  const folders = mini.getByRole('list', { name: 'Folders' });
  await expect(folders.getByRole('listitem')).toHaveText([/Loud ones\s*1 · new/]);
  const tiles = mini.getByRole('list', { name: 'All sounds, in order' });
  await expect(tiles.getByRole('listitem', { name: 'Airhorn, favorite' })).toHaveCount(1);
  await expect(tiles.getByRole('listitem', { name: 'Laugh, hidden' })).toHaveCount(1);
  // Hidden sounds are not among the board's tiles: one Laugh shows, the other is struck through.
  await expect(tiles.getByRole('listitem', { name: 'Laugh', exact: true })).toHaveCount(1);

  await dialog.getByRole('checkbox', { name: /Favorites/ }).uncheck();
  await expect(tiles.getByRole('listitem', { name: 'Airhorn, favorite' })).toHaveCount(0);
  await expect(tiles.getByRole('listitem', { name: 'Airhorn', exact: true })).toHaveCount(1);
  await dialog.getByRole('checkbox', { name: /Hidden sounds/ }).uncheck();
  await expect(tiles.getByRole('listitem', { name: 'Laugh, hidden' })).toHaveCount(0);
  await expect(tiles.getByRole('listitem', { name: 'Laugh', exact: true })).toHaveCount(2);
  await dialog.getByRole('checkbox', { name: /Loud ones/ }).uncheck();
  await expect(folders).toHaveCount(0);
  await dialog.getByRole('checkbox', { name: /Loud ones/ }).check();
  await expect(folders.getByRole('listitem')).toHaveCount(1);
});
