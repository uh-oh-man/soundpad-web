/**
 * The Stage G shell (2.43, WP-A): section navigation in both styles, views and their memory, the
 * non-modal editor panel (docked, sheet, minimized), its splitter, Escape and focus rules, dialogs over
 * the panel, the notice dock, Activity, the live announcer, the board's height budget, the keyboard
 * order and axe. Everything runs headless against the mock companion or the example.
 */
import AxeBuilder from '@axe-core/playwright';
import type { Locator, Page } from '@playwright/test';
import {
  test,
  expect,
  host,
  joinGuest,
  ORIGIN,
  playButton,
  sendCommand,
  startHarness,
  TEMP_READY,
  useLocalGuests,
} from './helpers/harness';
import { closeSection, editCopyForm, editorPanel, section, sectionNav } from './helpers/shell';

const nav = (page: Page, name: string) =>
  sectionNav(page).getByRole('button', { name, exact: true });
const mimicPanel = (page: Page) => page.getByRole('main', { name: 'Mimic', exact: true });
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

test('a Mimic draft and its selection survive while the Soundboard plays', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  const panel = await startMimic(page);
  await page.waitForTimeout(2600);
  await panel.getByRole('heading', { name: 'Mimic' }).focus();
  await page.keyboard.press('5');
  await panel.getByLabel('Clip name').fill('Kept while playing');
  await page.keyboard.press('Enter');
  const draft = panel.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  const names = ['Airhorn', 'Laugh'];
  await nav(page, 'Soundboard').click();
  for (let i = 0; i < 10; i++) await playButton(page, names[i % 2]).click();
  await nav(page, 'Mimic').click();
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
  await page.getByRole('button', { name: 'Activity' }).click();
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
  await expect(editorPanel(page)).toBeVisible(); // the open panel is section memory too
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

test('Mimic shortcuts clear the selection and leave view navigation available', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const mimic = await startMimic(page);
  await expect(mimic.getByRole('button', { name: 'Last 5 s' })).toBeVisible();
  await mimic.getByRole('heading', { name: 'Mimic' }).focus();
  await page.keyboard.press('5');
  await expect(mimic.getByLabel('Clip name')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(mimic.getByLabel('Clip name')).toHaveCount(0);
  await expect(mimic).toBeVisible();
  await nav(page, 'Soundboard').click();
  const search = page.getByRole('textbox', { name: 'Search sounds' });
  await search.press('5');
  await expect(search).toHaveValue('5');
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
  await expect(section(page, 'Mimic')).toBeVisible();
  await nav(page, 'Soundboard').click();
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
  await expect(mimicPanel(page).getByRole('button', { name: 'Start listening' })).toBeVisible();
  expect(Date.now() - started).toBeLessThan(15000);
  await expect(title).toHaveText('Activity');
  await expect(editorPanel(page).locator('form.temp-trim')).toHaveCount(0);
});

test('phone Mimic: Stop and Panic stay usable and the draft survives a visit to the board', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 412, height: 860 });
  await host(page, harness);
  const panel = await startMimic(page);
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
  await nav(page, 'Soundboard').click();
  await expect(page.locator('#board')).not.toHaveAttribute('inert', '');
  const before = harness.plays.length;
  await playButton(page, 'Airhorn').click();
  await expect.poll(() => harness.plays.slice(before)).toContain('DoPlaySound(1)');
  await nav(page, 'Mimic').click();
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
      ['Mimic settings', panel.getByText('Mimic settings', { exact: true })],
      ['Stop', page.getByRole('button', { name: 'Stop', exact: true })],
      ['Panic', page.getByRole('button', { name: 'Panic: stop audio and pause guest playback' })],
      ['Mimic', nav(page, 'Mimic')],
      ['Access settings', page.getByRole('button', { name: /^Access settings/ })],
      ['Increase Kick counter', page.getByRole('button', { name: 'Increase Kick counter' })],
      ['Kick counter details', page.getByRole('button', { name: 'Kick counter details' })],
      ['Undo', undo],
    ];

    for (const [name, control] of controls) {
      await control.scrollIntoViewIfNeeded();
      expect(await onTop(control), `${name} at ${size.width}`).toBe(true);
    }
    // Kick's details open above the player, and above the notices on a phone (Stage G integration).
    await page.getByRole('button', { name: 'Kick counter details' }).click();
    const decrease = page.getByRole('button', { name: 'Decrease Kick counter' });
    await expect(decrease).toBeVisible();
    expect(await onTop(decrease), `Decrease Kick counter at ${size.width}`).toBe(true);
    // Closed by its button (Safari does not focus a clicked button, so Escape would reach the body).
    await page.getByRole('button', { name: 'Kick counter details' }).click();
    await expect(decrease).toHaveCount(0);
    // Keyboard: no focus trap; Shift+Tab from the panel's close button reaches a notice's Undo (the dock
    // comes just before the splitter and the panel).
    await panel.getByText('Mimic settings', { exact: true }).focus();
    let reached = false;
    for (let i = 0; i < 40 && !reached; i++) {
      await page.keyboard.press('Tab');
      reached = await page.evaluate(
        () =>
          document.activeElement?.closest('.notification-stack') !== null &&
          document.activeElement?.textContent === 'Undo',
      );
    }
    expect(reached, `Tab to Undo at ${size.width}`).toBe(true);
    // The panel closes and opens again with the notices showing.
    await closeSection(page, 'Mimic');
    await expect(panel).toHaveCount(0);
    await nav(page, 'Mimic').click();
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: 'Stop listening' }).click();
    await closeSection(page, 'Mimic');
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
  // Each tile has several stops (play, art, quick actions, Favorite, Organize, …): room for all of them.
  for (let i = 0; i < 200; i++) {
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
  await page.getByRole('button', { name: 'Activity' }).click();
  if (linksTab) {
    await page.getByRole('link', { name: 'Skip to sounds' }).focus();
    await page.keyboard.press('Tab');
  } else await page.getByRole('link', { name: 'Skip to editor' }).focus();
  await expect(page.getByRole('link', { name: 'Skip to editor' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('#editor-title')).toBeFocused();
  // Skip to player lands on the player's first control (2.43 review: it went to "Skip to sounds").
  await page.getByRole('link', { name: 'Skip to player' }).focus();
  await page.keyboard.press('Enter');
  expect(await page.evaluate(() => !!document.activeElement?.closest('footer.player'))).toBe(true);
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
  await scan('Mimic');
  await nav(page, 'Soundboard').click();
  await page.getByRole('button', { name: 'Activity' }).click();
  await scan('activity docked');
  await chooseNav(page, 'Top tabs');
  await scan('tabs style');
  await chooseNav(page, 'Sidebar');
  await page.setViewportSize({ width: 412, height: 860 });
  await expect(editorPanel(page)).toBeVisible();
  await scan('activity sheet');
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.evaluate(() => (document.documentElement.dataset.contrast = 'high'));
  await scan('high contrast');
  await page.emulateMedia({ forcedColors: 'active' });
  // In forced colours the system picks every colour; axe still measures the page's own, so its contrast
  // check does not apply there.
  await scan('forced colours', ['color-contrast']);
  expect(problems).toEqual([]);
});

// ---- Phase 3 (Stage G integration): what crosses the packages (spec 12.4, Phase 3 list) ----

/** Rewrites every snapshot the companion sends, e.g. to stand in for an older companion. */
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
/**
 * Starts the example. Playwright's Windows WebKit has no Web Audio (real Safari has it): there the example
 * says it cannot start and the test ends early, as in example.spec.ts (new tests add no skips).
 */
async function startExample(page: Page) {
  await page.goto('/');
  const audio = await page.evaluate(
    () => 'AudioContext' in window || 'webkitAudioContext' in window,
  );
  await page.getByRole('button', { name: 'Try the example' }).click();
  if (!audio) {
    await expect(page.getByText(/The example could not start/)).toBeVisible();
    return false;
  }
  await expect(page.getByRole('complementary', { name: 'Example session' })).toBeVisible();
  return true;
}
const boardRibbon = (page: Page) => page.locator('.board-heading .header-ribbon');

test('Open editor for X: the sound editor with its own waveform and effects (2.43)', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Open editor for Airhorn' }).click();
  const panel = page.getByRole('dialog', { name: 'Edit Airhorn' });
  await expect(panel).toBeVisible();
  await expect(editorPanel(page)).toHaveAttribute('data-panel-kind', 'sound-edit');
  await expect(page.locator('#editor-title')).toBeFocused();
  // Its waveform's controls have names of their own, so the player's and the tiles' stay unique.
  await expect(panel.getByRole('slider', { name: 'Seek Airhorn in the editor' })).toBeVisible();
  // A Soundpad sound on a 2.43 companion (soundEdit): the copy form.
  const form = panel.getByRole('form', { name: 'Edit a copy of Airhorn' });
  await expect(form).toBeVisible();
  await expect(form.getByRole('button', { name: 'Save copy' })).toBeVisible();
  await expect(panel).not.toContainText('need companion 2.43');
  // The board stays playable while it is open.
  await playButton(page, 'Laugh').click();
  await expect.poll(() => harness.plays.some((p) => p.startsWith('DoPlaySound'))).toBe(true);
  await expect(panel).toBeVisible();
});

test('Open editor for X on an older companion: cues, icon and statistics, and one line instead of a disabled form', async ({
  page,
  harness,
}) => {
  await rewriteSnapshots(page, (state) => {
    if (state.companionVersion) state.companionVersion = '2.42.0';
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Open editor for Airhorn' }).click();
  const panel = page.getByRole('dialog', { name: 'Edit Airhorn' });
  await expect(panel).toContainText(
    'Effects on Soundpad sounds need companion 2.43 or later. Cues, regions, icon and statistics work here.',
  );
  await expect(panel.getByRole('form', { name: 'Edit a copy of Airhorn' })).toHaveCount(0);
  await expect(panel.getByRole('slider', { name: 'Seek Airhorn in the editor' })).toBeVisible();
  await expect(panel.locator('details.sound-editor-stats')).not.toHaveAttribute('open', '');
});

test('Details for X in the player opens the sound editor with Statistics expanded', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await playButton(page, 'Airhorn').click();
  await page.getByRole('button', { name: 'Details for Airhorn' }).click();
  const panel = page.getByRole('dialog', { name: 'Edit Airhorn' });
  await expect(panel).toBeVisible();
  await expect(panel.locator('details.sound-editor-stats')).toHaveAttribute('open', '');
  // One editor, with the sound's title (the 2.5 s fixture may have ended by now, so no second press).
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(page.locator('#editor-title')).toHaveText('Edit Airhorn');
});

test('example: the ribbon follows a draft played into the game, with a new key for each play', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  if (!(await startExample(page))) return;
  await expect(boardRibbon(page)).toHaveAttribute('data-ribbon-state', 'idle');
  await nav(page, 'Mimic').click();
  const mimic = mimicPanel(page);
  await mimic.getByRole('button', { name: 'Start listening' }).click();
  // Latest phrase takes a finished phrase: wait until the first one has been heard (as example.spec.ts).
  await expect(
    mimic.getByRole('list', { name: 'Transcript' }).getByRole('button', { name: 'man,' }).first(),
  ).toBeVisible({ timeout: 15000 });
  await mimic.getByRole('button', { name: 'Latest phrase', exact: true }).click();
  const draft = mimic.getByRole('region', { name: 'Draft' });
  await expect(draft).toBeVisible();
  await draft.getByRole('button', { name: 'Play into game' }).click();
  await nav(page, 'Soundboard').click();
  await expect(boardRibbon(page)).toHaveAttribute('data-ribbon-state', /live|waiting/);
  await expect(boardRibbon(page)).toHaveAttribute('data-ribbon-key', /\S/);
  const first = await boardRibbon(page).getAttribute('data-ribbon-key');
  // The key is host:sound:epoch, and a second play of the same draft is a new epoch.
  expect(first!.split(':')).toHaveLength(3);
  await nav(page, 'Mimic').click();
  await draft.getByRole('button', { name: 'Play into game' }).click();
  await nav(page, 'Soundboard').click();
  await expect.poll(() => boardRibbon(page).getAttribute('data-ribbon-key')).not.toBe(first);
  await expect(boardRibbon(page)).toHaveAttribute('data-ribbon-key', /\S/);
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(boardRibbon(page)).toHaveAttribute('data-ribbon-key', '');
});

test('example: notices never cover Leave example, the panel, Stop, Panic, the nav or Kick at four sizes', async ({
  page,
}) => {
  test.slow();
  let started = false;
  for (const size of [
    { width: 1280, height: 720 },
    { width: 1440, height: 900 },
    { width: 1100, height: 800 },
    { width: 412, height: 860 },
  ]) {
    await page.setViewportSize(size);
    if (!started) {
      if (!(await startExample(page))) return;
      started = true;
    }
    await nav(page, 'Mimic').click();
    const panel = mimicPanel(page);
    await expect(panel).toBeVisible();
    await page.evaluate(async (width) => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      for (let i = 1; i <= 4; i++)
        controller.notify(`Example notice ${i} at ${width}`, 'success', {
          label: 'Undo',
          run: () => {},
        });
    }, size.width);
    const narrow = size.width <= 1000;
    await expect(page.locator('.notification-stack .toast')).toHaveCount(narrow ? 2 : 3);
    const controls: [string, Locator][] = [
      ['Mimic settings', panel.getByText('Mimic settings', { exact: true })],
      ['Stop', page.getByRole('button', { name: 'Stop', exact: true })],
      ['Panic', page.getByRole('button', { name: 'Panic: stop audio and pause guest playback' })],
      ['Mimic', nav(page, 'Mimic')],
      ['Increase Kick counter', page.getByRole('button', { name: 'Increase Kick counter' })],
      ['Kick counter details', page.getByRole('button', { name: 'Kick counter details' })],
      ['Undo', page.locator('.notification-stack').getByRole('button', { name: 'Undo' }).first()],
    ];
    controls.push(['Leave example', page.getByRole('button', { name: 'Leave example' })]);
    for (const [name, control] of controls) {
      await control.scrollIntoViewIfNeeded();
      expect(await onTop(control), `${name} at ${size.width}`).toBe(true);
    }
    await closeSection(page, 'Mimic');
    await expect(panel).toHaveCount(0);
  }
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.getByRole('button', { name: 'Try the example' })).toBeVisible();
});

test('phone widths: no view, the Mimic sheet, Settings or Kick details scroll sideways', async ({
  page,
  harness,
}) => {
  for (const width of [412, 390, 375]) {
    await page.setViewportSize({ width, height: 860 });
    await host(page, harness);
    expect(await noSideScroll(page), `board at ${width}`).toBe(true);
    await nav(page, 'Mimic').click();
    await expect(mimicPanel(page)).toBeVisible();
    expect(await noSideScroll(page), `Mimic at ${width}`).toBe(true);
    await closeSection(page, 'Mimic');
    for (const name of ['Voices', 'Sentences'] as const) {
      await nav(page, name).click();
      await expect(section(page, name)).toBeVisible();
      expect(await noSideScroll(page), `${name} at ${width}`).toBe(true);
    }
    await nav(page, 'Soundboard').click();
    await page.getByRole('button', { name: 'Access settings' }).click();
    await expect(page.getByRole('dialog', { name: 'Session & access' })).toBeVisible();
    expect(await noSideScroll(page), `Settings at ${width}`).toBe(true);
    await page.getByRole('button', { name: 'Close dialog' }).click();
    await page.getByRole('button', { name: 'Kick counter details' }).click();
    const details = page.locator('#kick-details');
    await expect(details).toBeVisible();
    expect(await noSideScroll(page), `Kick details at ${width}`).toBe(true);
    const box = (await details.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
    await page.getByRole('button', { name: 'Kick counter details' }).click();
    await expect(details).toHaveCount(0);
  }
});

test('host style sync: the host’s two pages (separate browsers) share navigation, theme, motion and the open draft; guests never send it', async ({
  page,
  browser,
  harness,
}) => {
  test.slow();
  await useLocalGuests(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  // The companion window is another browser with its own storage: a second context stands in for it.
  const windowContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  try {
    const other = await windowContext.newPage();
    await other.goto(harness.launch);
    await expect(playButton(other, 'Airhorn')).toBeEnabled();
    // Navigation chosen on this page moves the other page's nav into its topbar.
    await chooseNav(page, 'Top tabs');
    await expect(other.locator('header.topbar nav[aria-label="Sections"]')).toBeVisible();
    // Theme and motion chosen on the other page show here.
    await other.getByRole('button', { name: 'Access settings' }).click();
    await other.getByLabel('Theme').selectOption('purple');
    await other
      .getByRole('radiogroup', { name: 'Motion' })
      .getByRole('radio', { name: 'Reduce' })
      .check();
    await other.getByRole('button', { name: 'Close dialog' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'purple');
    await expect(page.locator('html')).toHaveAttribute('data-motion', 'reduce');
    // A reload keeps it, and the companion has it for the next page.
    await page.reload();
    await expect(playButton(page, 'Airhorn')).toBeEnabled();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'purple');
    await expect(page.locator('header.topbar nav[aria-label="Sections"]')).toBeVisible();
    // The open draft: made on this page, it opens on the other one.
    const panel = await startMimic(page);
    await page.waitForTimeout(2600);
    await panel.getByRole('heading', { name: 'Mimic' }).focus();
    await page.keyboard.press('5');
    await panel.getByLabel('Clip name').fill('Shared draft');
    await page.keyboard.press('Enter');
    await expect(panel.getByRole('region', { name: 'Draft' })).toBeVisible();
    await nav(other, 'Mimic').click();
    const otherPanel = mimicPanel(other);
    await expect(otherPanel.getByRole('region', { name: 'Draft' })).toBeVisible();
    await expect(otherPanel.getByLabel('Clip name')).toHaveValue('Shared draft');
    // A guest never sends the host's style: the companion refuses it, and nothing changes.
    await nav(page, 'Soundboard').click();
    const guest = await joinGuest(page, 'Style Guest');
    const refused = await sendCommand(guest, 'uiPrefsSet', { uiPrefs: { theme: 'ocean' } });
    expect(refused.error).toBeTruthy();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'purple');
    await expect(other.locator('html')).toHaveAttribute('data-theme', 'purple');
  } finally {
    await windowContext.close();
  }
});

// ---- Stage G review fixes (2.43) ----

/** Where the focused element is, against the notice dock and the minimized editor bar. */
const focusedBox = (page: Page) =>
  page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null;
    const r = el?.getBoundingClientRect();
    const dock = document
      .querySelector('.notice-dock .notification-stack')
      ?.getBoundingClientRect();
    const panel = document.getElementById('editor-panel')?.getBoundingClientRect();
    return {
      inBody: !!el?.closest('.editor-body'),
      inBoard: !!el?.closest('#board'),
      bottom: r?.bottom ?? 0,
      height: r?.height ?? 0,
      dockTop: dock && dock.height ? dock.top : Infinity,
      panelTop: panel?.top ?? Infinity,
    };
  });

test('Mimic on a phone leaves the example banner and section navigation usable', async ({
  page,
}) => {
  await page.setViewportSize({ width: 412, height: 860 });
  if (!(await startExample(page))) return;
  await nav(page, 'Mimic').click();
  await expect(mimicPanel(page)).toBeVisible();
  const leave = page.getByRole('button', { name: 'Leave example' });
  await expect(leave).toBeVisible();
  expect(await onTop(leave)).toBe(true);
  await nav(page, 'Voices').click();
  await expect(section(page, 'Voices')).toBeVisible();
  await nav(page, 'Mimic').click();
  await expect(mimicPanel(page)).toBeVisible();
});

test('Activity: running a notice’s action keeps the keyboard in the list', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('Kept focus copy.', 'success', { label: 'Undo', run: () => {} });
  });
  await page.getByRole('button', { name: 'Activity' }).click();
  const item = editorPanel(page).getByRole('listitem').filter({ hasText: 'Kept focus copy.' });
  await item.getByRole('button', { name: 'Undo' }).focus();
  await page.keyboard.press('Enter');
  await expect(item.getByRole('button', { name: 'Undo' })).toHaveCount(0);
  await expect(item).toBeFocused();
  await expect(editorPanel(page)).toBeVisible();
});

test('error notices are a named group, not role alert (the announcer says them once)', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('Fixture failure.', 'error');
  });
  const card = page.locator('.notification-stack .notice-error');
  await expect(card).toContainText('Fixture failure.');
  await expect(card).toHaveAttribute('role', 'group');
  await expect(page.getByRole('group', { name: 'Error notice' })).toContainText('Fixture failure.');
  await expect(page.locator('.notification-stack [role="alert"]')).toHaveCount(0);
});

test('two dialogs: Escape closes the newer one only, and the keyboard stays in the other', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const opener = page.getByRole('button', { name: 'Access settings' });
  await opener.focus();
  await page.keyboard.press('Enter');
  const settings = page.getByRole('dialog', { name: 'Session & access' });
  await expect(settings).toBeVisible();
  // The companion's quit prompt opens over it (the window's close button, mid-session).
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    (controller as unknown as { update(p: object): void }).update({
      quitPrompt: { id: 'a'.repeat(32), clips: 1 },
    });
  });
  const quit = page.getByRole('dialog', { name: 'Quit Soundpad Web' });
  await expect(quit).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(quit).toHaveCount(0);
  await expect(settings).toBeVisible();
  expect(
    await page.evaluate(() => !!document.activeElement?.closest('[aria-label="Session & access"]')),
  ).toBe(true);
  await page.keyboard.press('Escape');
  await expect(settings).toHaveCount(0);
  await expect(opener).toBeFocused();
});

test('reloading into Mimic leaves keyboard focus at the start of the page', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const mimicLink = nav(page, 'Mimic');
  await mimicLink.focus();
  await page.keyboard.press('Enter');
  await expect(mimicLink).toBeFocused();
  await page.reload();
  await expect(mimicPanel(page)).toBeVisible();
  await page.waitForTimeout(300);
  await expect(mimicPanel(page).getByRole('heading', { name: 'Mimic' })).not.toBeFocused();
  // So the first Tab starts at the top of the page (in Safari, whose Tab skips links, at the topbar).
  await page.keyboard.press('Tab');
  expect(
    await page.evaluate(() => !!document.activeElement?.closest('.skip-links, header.topbar')),
  ).toBe(true);
});

test('the capture chip ends in an ellipsis on a desktop and keeps its words for screen readers on a phone', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await startMimic(page);
  const long = page.locator('.capture-chip .capture-long');
  await expect(long).toHaveCSS('text-overflow', 'ellipsis');
  await page.setViewportSize({ width: 412, height: 860 });
  // Out of sight but not display:none, so the status region still has its words.
  await expect(long).not.toHaveCSS('display', 'none');
  expect(await long.evaluate((el) => el.getBoundingClientRect().width)).toBeLessThanOrEqual(1);
  await expect(page.getByRole('status').filter({ hasText: 'Capturing' })).toHaveCount(1);
});

test('the sound editor asks for Statistics only once they are opened', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const original = controller.run.bind(controller);
    const w = window as unknown as { ops: string[] };
    w.ops = [];
    // One sound's statistics (the board's own sort asks for all of them, without a sound).
    controller.run = (async (op: string, ...rest: unknown[]) => {
      const fields = rest[0] as { soundId?: string } | undefined;
      w.ops.push(fields?.soundId ? `${op}:${fields.soundId}` : op);
      return (original as (...a: unknown[]) => unknown)(op, ...rest);
    }) as typeof controller.run;
  });
  const ops = () => page.evaluate(() => (window as unknown as { ops: string[] }).ops);
  await page.getByRole('button', { name: 'Open editor for Airhorn' }).click();
  const summary = editorPanel(page).locator('.sound-editor-stats summary');
  await expect(summary).toBeVisible();
  // Plays while it is closed fetch nothing either.
  await playButton(page, 'Airhorn').click();
  await page.waitForTimeout(600);
  const one = (list: string[]) => list.some((op) => op.startsWith('statistics:'));
  expect(one(await ops())).toBe(false);
  await summary.click();
  await expect.poll(async () => one(await ops())).toBe(true);
});

test('leaving the example drops its Temp list, so the next session never shows its sources', async ({
  page,
}) => {
  if (!(await startExample(page))) return;
  const listed = () =>
    page.evaluate(async () => {
      const m = await import(/* @vite-ignore */ '/src/' + 'temp-assets.ts');
      return m.tempAssetsSnapshot().data !== undefined;
    });
  await expect.poll(listed).toBe(true);
  await page.getByRole('button', { name: 'Leave example' }).click();
  await expect(page.getByRole('button', { name: 'Try the example' })).toBeVisible();
  await expect.poll(listed).toBe(false);
});

test('example at 1440: the compact Spotify row shows its name, the Host’s PC only tag and the reason uncut', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  if (!(await startExample(page))) return;
  const spotify = page.locator('section.spotify-player');
  await expect(spotify).toContainText('Spotify unavailable');
  // Nothing to control in the example: no transport takes the row's width.
  await expect(spotify.getByRole('button', { name: 'Play or pause Spotify' })).toHaveCount(0);
  const tag = spotify.locator('.host-only-tag');
  await expect(tag).toBeVisible();
  expect(await tag.evaluate((el) => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  expect(
    await spotify.evaluate(
      (root) =>
        root.querySelector('.host-only-tag')!.getBoundingClientRect().right <=
        root.getBoundingClientRect().right + 1,
    ),
  ).toBe(true);
});

test('the companion window with an editor docked keeps Spotify’s Play/Pause and Next in the rail', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1100, height: 800 });
  await host(page, harness);
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const original = controller.run.bind(controller);
    controller.run = (async (op: string, fields: Record<string, unknown>) =>
      op === 'spotify'
        ? {
            enabled: true,
            available: true,
            title: 'Fixture song',
            artist: 'Fixture artist',
            status: 'Playing',
            canToggle: true,
            canNext: true,
            canPrevious: true,
          }
        : original(op, fields)) as typeof controller.run;
  });
  const player = page.getByRole('region', { name: 'Spotify player' });
  await player.getByLabel('Enable Spotify desktop integration').check();
  await expect(player.getByRole('button', { name: 'Play or pause Spotify' })).toBeVisible();
  await page.getByRole('button', { name: 'Activity' }).click();
  await expect(page.locator('.app-shell')).toHaveAttribute('data-rail', 'true');
  for (const name of ['Play or pause Spotify', 'Next Spotify track']) {
    const button = player.getByRole('button', { name });
    await expect(button).toBeVisible();
    expect(await onTop(button), name).toBe(true);
  }
});

test('Voices and Sentences: one search field like the board’s, and a New sentence field with room', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await nav(page, 'Voices').click();
  await expect(section(page, 'Voices').getByLabel('Search the voice library')).toHaveCSS(
    'border-top-width',
    '0px',
  );
  await nav(page, 'Sentences').click();
  const field = section(page, 'Sentences').getByLabel('New sentence', { exact: true });
  expect((await field.boundingBox())!.width).toBeGreaterThanOrEqual(280);
});

/** Where the focused control is against the bar at the top of its scroll area (null when it is in it). */
const underTopBar = (page: Page, bar: string) =>
  page.evaluate((selector) => {
    const el = document.activeElement as HTMLElement | null;
    const top = document.querySelector(selector);
    if (!el || !top || top.contains(el)) return null;
    const r = el.getBoundingClientRect();
    if (!r.height) return null;
    const edge = top.getBoundingClientRect().bottom;
    const name = el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 30) ?? el.tagName;
    return r.top < edge - 0.5
      ? `${name}: top ${Math.round(r.top)} under ${Math.round(edge)}`
      : null;
  }, bar);

test('2.43 review: on a phone, keyboard focus never hides under the sticky header, going up or down with notices', async ({
  page,
}) => {
  // Enough sounds for the page to scroll well past the header.
  const harness = await startHarness(ORIGIN, 40);
  try {
    await page.setViewportSize({ width: 412, height: 860 });
    await host(page, harness);
    await headerWalk(page);
  } finally {
    await harness.stop();
  }
});

async function headerWalk(page: Page) {
  const header = '.app-shell > .topbar';
  await expect(page.locator(header)).toHaveCSS('position', 'sticky');
  // From a tile well down the board, Shift+Tab back up to the search.
  await page.locator('#board .tile-play-strip').nth(20).focus();
  expect(await page.evaluate(() => scrollY)).toBeGreaterThan(200);
  const search = page.getByRole('textbox', { name: 'Search sounds' });
  let steps = 0;
  for (; steps < 200; steps++) {
    await page.keyboard.press('Shift+Tab');
    await expect.poll(() => underTopBar(page, header), `Shift+Tab ${steps}`).toBeNull();
    if (await search.evaluate((el) => el === document.activeElement)) break;
  }
  expect(steps).toBeGreaterThan(5);
  await expect(search).toBeFocused();
  // Notices showing: clearing them on the way down never puts a control under the header instead.
  await page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    controller.notify('A fixture notice, long enough to need a second line on a phone.', 'info', {
      label: 'Undo',
      run: () => {},
    });
    controller.notify('A second fixture notice.', 'info', { label: 'Undo', run: () => {} });
  });
  await expect(page.locator('.notification-stack .toast')).toHaveCount(2);
  let checked = 0;
  for (let i = 0; i < 40; i++) {
    await page.keyboard.press('Tab');
    await expect.poll(() => underTopBar(page, header), `Tab ${i}`).toBeNull();
    const box = await focusedBox(page);
    if (!box.inBoard || !box.height) continue;
    checked++;
    expect(box.bottom, `Tab ${i} under the notices`).toBeLessThanOrEqual(box.dockTop + 1);
  }
  expect(checked).toBeGreaterThan(5);
}

test('2.43 review: Shift+Tab up Session & access never leaves a control under the dialog’s title; Appearance says what the host shares', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1280, height: 720 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  const dialog = page.getByRole('dialog', { name: 'Session & access' });
  await expect(dialog).toBeVisible();
  // A host on companion 2.43 shares the style with the companion window (ui.json).
  await expect(dialog.getByRole('region', { name: 'Appearance' })).toContainText(
    'This browser and the companion window share these, except Notice sounds.',
  );
  const close = dialog.getByRole('button', { name: 'Close dialog' });
  await expect(close).toBeFocused();
  // From the close button Shift+Tab wraps to the last control, then walks all the way back up.
  let steps = 0;
  for (; steps < 120; steps++) {
    await page.keyboard.press('Shift+Tab');
    if (await close.evaluate((el) => el === document.activeElement)) break;
    await expect
      .poll(
        () => underTopBar(page, '[aria-label="Session & access"] > .dialog-title'),
        `Shift+Tab ${steps}`,
      )
      .toBeNull();
  }
  expect(steps).toBeGreaterThan(10);
  // The dialog did scroll, so the title row was stuck over its content for most of the walk.
  await page.keyboard.press('Shift+Tab');
  await expect.poll(() => dialog.evaluate((el) => el.scrollTop)).toBeGreaterThan(50);
});

test('2.43 review: Escape in a field of the sound editor leaves the field and keeps what was typed; a second Escape closes it', async ({
  page,
  harness,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await page.getByRole('button', { name: 'Open editor for Airhorn' }).click();
  const panel = page.getByRole('dialog', { name: 'Edit Airhorn' });
  const name = editCopyForm(page, 'Airhorn').getByRole('textbox', { name: 'Name', exact: true });
  await name.fill('My edited copy name');
  await name.press('Escape');
  await expect(panel).toBeVisible();
  await expect(page.locator('#editor-title')).toBeFocused();
  await expect(name).toHaveValue('My edited copy name');
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
});
