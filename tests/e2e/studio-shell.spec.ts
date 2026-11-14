/**
 * The Stage G shell (2.43, WP-A): section navigation in both styles, views and their memory, the
 * non-modal editor panel (docked, sheet, minimized), its splitter, Escape and focus rules, dialogs over
 * the panel, the notice dock, Activity, the live announcer, the board's height budget, the keyboard
 * order and axe. Everything runs headless against the mock companion or the example.
 */
import AxeBuilder from '@axe-core/playwright';
import type { Locator, Page } from '@playwright/test';
import { test, expect, host, playButton, TEMP_READY } from './helpers/harness';
import { closeEditor, editorPanel, section, sectionNav } from './helpers/shell';

const nav = (page: Page, name: string) =>
  sectionNav(page).getByRole('button', { name, exact: true });
const mimicPanel = (page: Page) => page.getByRole('dialog', { name: 'Mimic' });
/** The first "Play from start" at 1440×900 before Stage G (Phase 0's before shots, host board). */
const TILE_Y_BEFORE = 612.375;

/** True when a press in the middle of the control lands on the control itself. */
const onTop = (control: Locator) =>
  control.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  });
const noSideScroll = (page: Page) =>
  page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1);

async function chooseNav(page: Page, style: 'Sidebar' | 'Top tabs') {
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page
    .getByRole('radiogroup', { name: 'Navigation' })
    .getByRole('radio', { name: style })
    .check();
  await page.getByRole('button', { name: 'Close dialog' }).click();
}

async function startMimic(page: Page) {
  await nav(page, 'Mimic').click();
  const panel = mimicPanel(page);
  await panel.getByRole('button', { name: 'Start listening' }).click();
  await expect(panel.getByRole('status').first()).toContainText('Listening to Fixture');
  return panel;
}

/** A 1 s, 440 Hz stereo WAV for session audio. */
function tone(seconds = 1) {
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

test('section nav: both styles, the choice survives a reload, aria-current follows the view, and every name is unique', async ({
  page,
  harness,
}) => {
  for (const size of [
    { width: 1440, height: 900 },
    { width: 412, height: 860 },
  ]) {
    await page.setViewportSize(size);
    await host(page, harness);
    for (const style of ['Sidebar', 'Top tabs'] as const) {
      await chooseNav(page, style);
      for (const name of ['Soundboard', 'Mimic', 'Voices', 'Sentences'])
        await expect(page.getByRole('button', { name, exact: true })).toHaveCount(1);
      // Where it sits: the sidebar on a wide screen in sidebar style, the topbar otherwise.
      const inTopbar = style === 'Top tabs' || size.width <= 1000;
      await expect(page.locator(inTopbar ? 'header.topbar' : 'aside.sidebar')).toContainText(
        'Soundboard',
      );
      await expect(nav(page, 'Soundboard')).toHaveAttribute('aria-current', 'page');
      await nav(page, 'Voices').click();
      await expect(section(page, 'Voices')).toBeVisible();
      await expect(nav(page, 'Voices')).toHaveAttribute('aria-current', 'page');
      await expect(nav(page, 'Soundboard')).not.toHaveAttribute('aria-current', 'page');
      await nav(page, 'Soundboard').click();
      await expect(page.getByRole('main', { name: 'Soundboard' })).toBeVisible();
      expect(await noSideScroll(page), `no sideways scroll at ${size.width} ${style}`).toBe(true);
    }
    // The last choice (top tabs) is kept on this browser.
    await page.reload();
    await expect(playButton(page, 'Airhorn')).toBeEnabled();
    await expect(
      page.locator('header.topbar').getByRole('navigation', { name: 'Sections' }),
    ).toHaveCount(1);
    await chooseNav(page, 'Sidebar');
  }
});

test('section memory: the board scroll and view come back, and "/" focuses the current view’s search', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 600 });
  await host(page, harness);
  await page.getByLabel('View mode', { exact: true }).selectOption('large');
  const board = page.locator('#board');
  await board.evaluate((el) => (el.scrollTop = 200));
  const before = await board.evaluate((el) => el.scrollTop);
  expect(before).toBeGreaterThan(50);
  await nav(page, 'Voices').click();
  await expect(section(page, 'Voices').getByLabel('Search the voice library')).toBeVisible();
  await page.locator('#board').focus();
  await page.keyboard.press('/');
  await expect(section(page, 'Voices').getByLabel('Search the voice library')).toBeFocused();
  await nav(page, 'Soundboard').click();
  await expect.poll(() => board.evaluate((el) => el.scrollTop)).toBe(before);
  await page.locator('#board').focus();
  await page.keyboard.press('/');
  await expect(page.getByRole('textbox', { name: 'Search sounds' })).toBeFocused();
  // A reload comes back to the view this tab showed.
  await nav(page, 'Sentences').click();
  await page.reload();
  await expect(section(page, 'Sentences')).toBeVisible();
  await expect(nav(page, 'Sentences')).toHaveAttribute('aria-current', 'page');
});

test('the panel stays open while tiles play: the Mimic draft and its selection survive alternating plays', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  const panel = await startMimic(page);
  await page.waitForTimeout(2600);
  await panel.getByLabel('F6 hotkey').focus();
  await page.keyboard.press('5');
  await panel.getByLabel('Clip name').fill('Kept while playing');
  await page.keyboard.press('Enter');
  const draft = panel.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  const names = ['Airhorn', 'Laugh'];
  for (let i = 0; i < 10; i++) await playButton(page, names[i % 2]).click();
  await expect.poll(() => harness.plays.filter((p) => p.startsWith('DoPlaySound')).length).toBe(10);
  await expect(panel).toBeVisible();
  await expect(draft).toBeVisible();
  await expect(panel.getByLabel('Clip name')).toHaveValue('Kept while playing');
});

test('splitter: keys, pointer drag, the width survives a reload and is clamped to the window', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await nav(page, 'Mimic').click();
  const splitter = page.getByRole('separator', { name: 'Resize editor' });
  const width = async () => Number(await splitter.getAttribute('aria-valuenow'));
  await expect(splitter).toHaveAttribute('aria-valuenow', '480');
  await splitter.focus();
  await page.keyboard.press('ArrowLeft');
  expect(await width()).toBe(496);
  await page.keyboard.press('Shift+ArrowRight');
  expect(await width()).toBe(432);
  await page.keyboard.press('Home');
  expect(await width()).toBe(360);
  await page.keyboard.press('Enter');
  expect(await width()).toBe(480);
  await page.keyboard.press('End');
  expect(await width()).toBe(718); // 1440 - 234 sidebar - 480 board - 8 splitter
  await page.keyboard.press('Home');
  // Drag 100 px to the left: 100 px wider.
  const box = (await splitter.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 - 100, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  expect(await width()).toBe(460);
  const panelBox = (await editorPanel(page).boundingBox())!;
  expect(Math.round(panelBox.width)).toBe(460);
  await page.reload();
  await expect(playButton(page, 'Airhorn')).toBeEnabled();
  await expect(mimicPanel(page)).toBeVisible(); // the open panel is section memory too
  await expect(splitter).toHaveAttribute('aria-valuenow', '460');
  // The VR window: the sidebar folds to a rail and the panel keeps the board 480 px wide.
  await page.setViewportSize({ width: 1100, height: 800 });
  await page.keyboard.press('Tab');
  await splitter.focus();
  await page.keyboard.press('End');
  expect(await width()).toBe(548); // 1100 - 64 rail - 480 - 8
  await expect(page.locator('.app-shell')).toHaveAttribute('data-rail', 'true');
  const boardBox = (await page.locator('#board').boundingBox())!;
  expect(boardBox.width).toBeGreaterThanOrEqual(479);
});

test('Escape closes the panel from inside it and gives the keyboard back to the nav; board keys keep their meaning', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const panel = await startMimic(page);
  await page.waitForTimeout(2600);
  // "5" in the board's search is typing, not Mimic's "last 5 seconds".
  const search = page.getByRole('textbox', { name: 'Search sounds' });
  await search.fill('');
  await search.press('5');
  await expect(search).toHaveValue('5');
  await expect(panel.locator('.mimic-range')).toHaveCount(0);
  // ...and on a focused tile it is not Mimic's either.
  await search.fill('');
  await playButton(page, 'Airhorn').focus();
  await page.keyboard.press('5');
  await expect(panel.locator('.mimic-range')).toHaveCount(0);
  // Escape in the search clears it and leaves the panel open.
  await search.fill('Air');
  await search.press('Escape');
  await expect(search).toHaveValue('');
  await search.press('Escape');
  await expect(panel).toBeVisible();
  // Inside the panel, Escape closes it, and the nav's Mimic (which opened it) has the keyboard.
  await panel.getByRole('button', { name: 'Stop listening' }).focus();
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(nav(page, 'Mimic')).toBeFocused();
  // Pressing Mimic while it is open moves the keyboard into it instead of closing it.
  await nav(page, 'Mimic').click();
  await expect(page.locator('#editor-title')).toBeFocused();
  await nav(page, 'Mimic').click();
  await expect(mimicPanel(page)).toBeVisible();
  await expect(page.locator('#editor-title')).toBeFocused();
});

test('a dialog over the open panel: one Close dialog, Tab stays in the dialog, focus comes back to its opener', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await nav(page, 'Mimic').click();
  const opener = page.getByRole('button', { name: 'Access settings' });
  await opener.click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('button', { name: 'Close dialog' })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Mimic', exact: true })).toHaveCount(0);
  await expect(page.locator('.app-shell')).toHaveAttribute('inert', '');
  for (let i = 0; i < 25; i++) {
    await page.keyboard.press('Tab');
    expect(
      await page.evaluate(
        () => !!document.activeElement?.closest('[aria-label="Session & access"]'),
      ),
    ).toBe(true);
  }
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(mimicPanel(page)).toBeVisible();
  await expect(opener).toBeFocused();
  await expect(page.locator('.app-shell')).not.toHaveAttribute('inert', '');
});

test('fast switching between editors: each kind shows its own content and title', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await page
    .getByLabel('Add session audio', { exact: true })
    .setInputFiles({ name: 'Switch tone.wav', mimeType: 'audio/wav', buffer: tone() });
  await expect(playButton(page, 'Switch tone')).toBeEnabled({ timeout: TEMP_READY });
  const title = page.locator('#editor-title');
  const started = Date.now();
  await nav(page, 'Mimic').click();
  await expect(title).toHaveText('Mimic');
  await playButton(page, 'Airhorn').click();
  await page.locator('#temp-manager > summary').click();
  await page
    .locator('.temp-source', { hasText: 'Switch tone' })
    .getByRole('button', { name: 'Edit copy' })
    .click();
  await expect(title).toHaveText('Copy editor');
  await expect(page.getByRole('form', { name: 'Edit a copy of Switch tone' })).toBeVisible();
  await playButton(page, 'Laugh').click();
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const { requestEditor } = await import(/* @vite-ignore */ '/src/' + 'shell.ts');
    const sound = controller
      .getSnapshot()
      .state.library.sounds.find((s: { name: string }) => s.name === 'Airhorn');
    requestEditor({ kind: 'sound-edit', soundId: sound.id });
  });
  await expect(title).toHaveText('Edit Airhorn');
  await expect(editorPanel(page).getByText('Statistics', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Activity' }).click();
  await expect(title).toHaveText('Activity');
  await expect(editorPanel(page).getByRole('heading', { name: 'Notices' })).toBeVisible();
  await nav(page, 'Mimic').click();
  await expect(title).toHaveText('Mimic');
  await expect(mimicPanel(page).getByRole('button', { name: 'Start listening' })).toBeVisible();
  expect(Date.now() - started).toBeLessThan(15000);
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(editorPanel(page).locator('form.temp-trim')).toHaveCount(0);
});

test('phone sheet: Stop and Panic stay usable, Show board folds it to a bar and Show editor brings the draft back', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 412, height: 860 });
  await host(page, harness);
  const panel = await startMimic(page);
  await expect(panel).toHaveAttribute('data-panel-mode', 'sheet');
  await page.waitForTimeout(2600);
  await panel.getByRole('button', { name: 'Last 5 s', exact: true }).click();
  await panel.getByLabel('Clip name').fill('Sheet draft');
  await page.keyboard.press('Enter');
  await expect(panel.getByRole('region', { name: 'Draft' })).toBeVisible();
  const stop = page.getByRole('button', { name: 'Stop', exact: true });
  const panic = page.getByRole('button', { name: 'Panic: stop audio and pause guest playback' });
  for (const control of [stop, panic]) {
    await expect(control).toBeVisible();
    expect(await onTop(control)).toBe(true);
  }
  await stop.click();
  expect(await noSideScroll(page)).toBe(true);
  // The board is out of reach under the sheet.
  await expect(page.locator('#board')).toHaveAttribute('inert', '');
  await panel.getByRole('button', { name: 'Show board' }).click();
  await expect(panel).toHaveAttribute('data-panel-mode', 'minimized');
  await expect(page.locator('#board')).not.toHaveAttribute('inert', '');
  const before = harness.plays.length;
  await playButton(page, 'Airhorn').click();
  await expect.poll(() => harness.plays.slice(before)).toContain('DoPlaySound(1)');
  await panel.getByRole('button', { name: 'Show editor' }).click();
  await expect(panel).toHaveAttribute('data-panel-mode', 'sheet');
  await expect(panel.getByRole('region', { name: 'Draft' })).toBeVisible();
  await expect(panel.getByLabel('Clip name')).toHaveValue('Sheet draft');
  expect(await noSideScroll(page)).toBe(true);
});

test('notices never cover the panel, Stop, Panic, the nav or Settings at four sizes, and Tab reaches their Undo', async ({
  page,
  harness,
}) => {
  // On the mock companion, so it runs in WebKit too; the example's own matrix (Leave example) is in
  // app.spec.ts ("notices never cover Mimic’s close button…").
  test.slow();
  for (const size of [
    { width: 1280, height: 720 },
    { width: 1440, height: 900 },
    { width: 1100, height: 800 },
    { width: 412, height: 860 },
  ]) {
    await page.setViewportSize(size);
    await host(page, harness);
    await nav(page, 'Mimic').click();
    const panel = mimicPanel(page);
    await panel.getByRole('button', { name: 'Start listening' }).click();
    await expect(panel.getByRole('status').first()).toContainText('Listening to Fixture');
    // Four notices with a button each (distinct words: the same words again replace a notice).
    await page.evaluate(async () => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      for (let i = 1; i <= 4; i++)
        controller.notify(`Fixture notice ${i}`, 'success', { label: 'Undo', run: () => {} });
    });
    const narrow = size.width <= 1000;
    await expect(page.locator('.notification-stack .toast')).toHaveCount(narrow ? 2 : 3);
    // The older ones fold into a button that opens Activity.
    await expect(page.getByRole('button', { name: /^\d+ more notices?$/ })).toBeVisible();
    const undo = page.locator('.notification-stack').getByRole('button', { name: 'Undo' }).first();
    const controls: [string, Locator][] = [
      ['Close dialog', panel.getByRole('button', { name: 'Close dialog' })],
      ['Stop', page.getByRole('button', { name: 'Stop', exact: true })],
      ['Panic', page.getByRole('button', { name: 'Panic: stop audio and pause guest playback' })],
      ['Mimic', nav(page, 'Mimic')],
      ['Access settings', page.getByRole('button', { name: /^Access settings/ })],
      ['Increase Kick counter', page.getByRole('button', { name: 'Increase Kick counter' })],
      ['Undo', undo],
    ];
    if (narrow) controls.push(['Show board', panel.getByRole('button', { name: 'Show board' })]);
    else controls.push(['Resize editor', page.getByRole('separator', { name: 'Resize editor' })]);
    for (const [name, control] of controls) {
      await control.scrollIntoViewIfNeeded();
      expect(await onTop(control), `${name} at ${size.width}`).toBe(true);
    }
    // Keyboard: no focus trap; Shift+Tab from the panel's close button reaches a notice's Undo (the dock
    // comes just before the splitter and the panel).
    await panel.getByRole('button', { name: 'Close dialog' }).focus();
    let reached = false;
    for (let i = 0; i < 40 && !reached; i++) {
      await page.keyboard.press('Shift+Tab');
      reached = await page.evaluate(
        () =>
          document.activeElement?.closest('.notification-stack') !== null &&
          document.activeElement?.textContent === 'Undo',
      );
    }
    expect(reached, `Tab to Undo at ${size.width}`).toBe(true);
    // The panel closes and opens again with the notices showing.
    await closeEditor(page);
    await expect(panel).toHaveCount(0);
    await nav(page, 'Mimic').click();
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: 'Stop listening' }).click();
    await closeEditor(page);
  }
});

test('Activity keeps a notice’s Undo for 60 s after it leaves the screen, and counts what is new', async ({
  page,
  harness,
}) => {
  await page.clock.install();
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    (window as unknown as { undone: number }).undone = 0;
    controller.notify('Fixture copy added.', 'success', {
      label: 'Undo',
      run: () => (window as unknown as { undone: number }).undone++,
    });
  });
  const activity = page.getByRole('button', { name: 'Activity' });
  await expect(activity).toHaveAccessibleDescription(/\d+ new/);
  await expect(page.locator('.notification-stack .toast')).toContainText('Fixture copy added.');
  // The card leaves after 10 s...
  await page.clock.fastForward(12000);
  await expect(page.locator('.notification-stack .toast')).toHaveCount(0);
  // ...and Activity still offers its Undo.
  await activity.click();
  const notices = editorPanel(page).getByRole('region', { name: 'Notices' });
  const item = editorPanel(page).getByRole('listitem').filter({ hasText: 'Fixture copy added.' });
  await expect(item.getByRole('button', { name: 'Undo' })).toBeVisible();
  void notices;
  await expect(activity).not.toHaveAccessibleDescription(/new/);
  await item.getByRole('button', { name: 'Undo' }).click();
  expect(await page.evaluate(() => (window as unknown as { undone: number }).undone)).toBe(1);
  await expect(item.getByRole('button', { name: 'Undo' })).toHaveCount(0);
  // Another one, left alone for over a minute: its Undo is gone from Activity.
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('Second fixture copy.', 'success', { label: 'Undo', run: () => {} });
  });
  const second = editorPanel(page)
    .getByRole('listitem')
    .filter({ hasText: 'Second fixture copy.' });
  await expect(second.getByRole('button', { name: 'Undo' })).toBeVisible();
  await page.clock.fastForward(75000);
  await expect(second.getByRole('button', { name: 'Undo' })).toHaveCount(0);
});

test('one notice is announced exactly once by the live region', async ({ page, harness }) => {
  await host(page, harness);
  // The live regions sit in a closed shadow root (text lookups never find a notice twice); in development
  // the page lists what it announced.
  const announced = () =>
    page.evaluate(
      () =>
        (window as unknown as { __soundpadAnnouncements?: string[] }).__soundpadAnnouncements ?? [],
    );
  const before = (await announced()).length;
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('Announced once.');
  });
  await expect(page.locator('.notification-stack .toast')).toContainText('Announced once.');
  await page.waitForTimeout(300);
  expect((await announced()).slice(before)).toEqual(['Announced once.']);
  // Text lookups find the card only, and the card itself does not announce (it would twice, moving
  // into a dialog).
  await expect(page.getByText('Announced once.')).toHaveCount(1);
  await expect(page.locator('.notification-stack .toast').first()).toHaveAttribute(
    'aria-live',
    'off',
  );
});

test('the board starts at least 150 px higher than before Stage G at 1440×900', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  const box = (await playButton(page, 'Airhorn').boundingBox())!;
  expect(box.y).toBeLessThanOrEqual(TILE_Y_BEFORE - 150);
});

test('keyboard order: skip links, topbar, nav, search, tiles, then the player; Skip to editor lands on the panel', async ({
  page,
  harness,
  browserName,
}) => {
  // Safari's default Tab skips links (Option+Tab reaches them), so there the order starts at the topbar.
  const linksTab = browserName !== 'webkit';
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await page.locator('body').focus();
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  const order: string[] = [];
  for (let i = 0; i < 60; i++) {
    await page.keyboard.press('Tab');
    order.push(
      await page.evaluate(() => {
        const el = document.activeElement as HTMLElement;
        if (el.closest('.skip-links')) return 'skip';
        if (el.closest('header.topbar')) return 'topbar';
        if (el.closest('nav[aria-label="Sections"]')) return 'nav';
        if (el.matches('[aria-label="Search sounds"]')) return 'search';
        if (el.matches('.tile-play-strip, .tile-art-button, .sound-entry *')) return 'tile';
        if (el.closest('.player')) return 'player';
        return 'other';
      }),
    );
    if (order.at(-1) === 'player') break;
  }
  const first = (kind: string) => order.indexOf(kind);
  expect(order[0]).toBe(linksTab ? 'skip' : 'topbar');
  for (const [a, b] of [
    ...(linksTab ? [['skip', 'topbar']] : []),
    ['topbar', 'nav'],
    ['nav', 'search'],
    ['search', 'tile'],
    ['tile', 'player'],
  ])
    expect(first(a), `${a} before ${b}: ${order.join(' ')}`).toBeLessThan(first(b));
  // With the panel open, the second skip link goes to its heading.
  await nav(page, 'Mimic').click();
  if (linksTab) {
    await page.getByRole('link', { name: 'Skip to sounds' }).focus();
    await page.keyboard.press('Tab');
  } else await page.getByRole('link', { name: 'Skip to editor' }).focus();
  await expect(page.getByRole('link', { name: 'Skip to editor' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('#editor-title')).toBeFocused();
});

test('accessibility: axe finds nothing serious in either nav style, the docked panel, the sheet, Activity, High contrast and forced colours', async ({
  page,
  harness,
}) => {
  const problems: string[] = [];
  async function scan(screen: string, skip: string[] = []) {
    const { violations } = await new AxeBuilder({ page }).disableRules(skip).analyze();
    for (const v of violations)
      if (v.impact === 'serious' || v.impact === 'critical')
        problems.push(`${screen}: ${v.id} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await scan('sidebar style');
  await nav(page, 'Mimic').click();
  await scan('panel docked');
  await page.getByRole('button', { name: 'Activity' }).click();
  await scan('activity');
  await chooseNav(page, 'Top tabs');
  await scan('tabs style');
  await chooseNav(page, 'Sidebar');
  await page.setViewportSize({ width: 412, height: 860 });
  await nav(page, 'Mimic').click();
  await scan('sheet');
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.evaluate(() => (document.documentElement.dataset.contrast = 'high'));
  await scan('high contrast');
  await page.emulateMedia({ forcedColors: 'active' });
  // In forced colours the system picks every colour; axe still measures the page's own, so its contrast
  // check does not apply there.
  await scan('forced colours', ['color-contrast']);
  expect(problems).toEqual([]);
});
