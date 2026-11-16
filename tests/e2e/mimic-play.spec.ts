/**
 * Mimic history playback (2.45, WP1): the personal Preview here / Play into game switch, word clicks that play,
 * waveform clicks that play from a point while a drag only selects, and the playing-word highlight.
 */
import type { Locator, Page } from '@playwright/test';
import { setMimicTranscription } from './helpers/shell';
import {
  expect,
  host,
  joinGuest,
  sendCommand,
  test,
  useLocalGuests,
  type Harness,
} from './helpers/harness';

const MODE_KEY = 'soundpad-web:mimic-play-mode';
const PLAY = /^DoPlaySound\(\d+\)$/;

/** Opens Mimic on the host, starts listening with transcripts on, and waits for the first sentence. */
async function listen(page: Page) {
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('main', { name: 'Mimic', exact: true });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await setMimicTranscription(dialog);
  return dialog;
}
/** The first whole sentence "Fixture tone N" (its three word buttons). */
async function firstSentence(dialog: Locator) {
  const sentence = dialog
    .getByRole('list', { name: 'Transcript' })
    .getByRole('listitem')
    // `has` is matched inside each item, so it starts from the page, not the dialog.
    .filter({ has: dialog.page().getByRole('button', { name: 'Fixture', exact: true }) })
    .first();
  await expect(sentence).toBeVisible({ timeout: 20000 });
  const words = sentence.getByRole('button');
  await expect(words).toHaveCount(3);
  return words;
}
const mode = (dialog: Locator) =>
  dialog.getByRole('radiogroup', { name: 'Word and waveform clicks' });
const playingWords = (dialog: Locator) =>
  dialog.getByRole('list', { name: 'Transcript' }).locator('.mimic-word.playing');
/** Plays that reached Soundpad since `from` (an index into harness.plays). */
const playsSince = (harness: Harness, from: number) =>
  harness.plays.slice(from).filter((p) => PLAY.test(p));
/** Clicks the main waveform at a fraction of its width, without moving between press and release. */
async function clickWave(page: Page, dialog: Locator, fraction: number) {
  const canvas = dialog.getByRole('slider', { name: /Mimic buffer/ });
  await canvas.scrollIntoViewIfNeeded();
  const box = (await canvas.boundingBox())!;
  await page.mouse.click(box.x + box.width * fraction, box.y + box.height / 2);
  return { canvas, box };
}

test('Mimic play mode: Preview by default and remembered; a word click plays exactly that word, Shift-click only selects', async ({
  page,
  harness,
  browserName,
}) => {
  await host(page, harness);
  const dialog = await listen(page);
  const switcher = mode(dialog);
  await expect(switcher.getByRole('radio', { name: 'Preview here' })).toBeChecked();
  await expect(switcher.getByRole('radio', { name: 'Play into game' })).not.toBeChecked();
  const words = await firstSentence(dialog);

  // Preview (headless WebKit often refuses to play audio, and hosts never use Safari): the word is selected and
  // heard in this browser only, lit while it plays; Soundpad plays nothing.
  if (browserName !== 'webkit') {
    const before = harness.plays.length;
    await words.first().click();
    await expect(words.first()).toHaveAttribute('aria-pressed', 'true');
    await expect(words.first()).toHaveClass(/\bplaying\b/);
    await expect(playingWords(dialog)).toHaveCount(0, { timeout: 8000 });
    await page.waitForTimeout(500);
    expect(playsSince(harness, before)).toEqual([]);
  }

  // Play into game is remembered in this browser, also after the window closes.
  await switcher.getByRole('radio', { name: 'Play into game' }).check();
  expect(await page.evaluate((key) => localStorage.getItem(key), MODE_KEY)).toBe('game');
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  await expect(mode(dialog).getByRole('radio', { name: 'Play into game' })).toBeChecked();

  // One click, one Soundpad play: the word becomes a Temp clip named after it and plays at once.
  let before = harness.plays.length;
  const first = (await firstSentence(dialog)).first();
  await first.click();
  await expect(first).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await expect(dialog.getByRole('button', { name: /Replay Fixture ·/ }).first()).toBeEnabled();
  await page.waitForTimeout(800);
  expect(playsSince(harness, before)).toHaveLength(1);

  // The same word again replays its clip rather than making another.
  before = harness.plays.length;
  const clipsBefore = await dialog.getByRole('button', { name: /Replay Fixture ·/ }).count();
  await first.click();
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await page.waitForTimeout(500);
  expect(await dialog.getByRole('button', { name: /Replay Fixture ·/ }).count()).toBe(clipsBefore);

  // Enter on a focused word plays it too.
  before = harness.plays.length;
  const tone = (await firstSentence(dialog)).nth(1);
  await tone.focus();
  await page.keyboard.press('Enter');
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await expect(page.locator('.player-track')).toContainText('tone');

  // Shift-click selects everything from the anchor ("tone") to it, and plays nothing.
  await page.waitForTimeout(800);
  before = harness.plays.length;
  const all = await firstSentence(dialog);
  await all.first().click({ modifiers: ['Shift'] });
  await expect(all.first()).toHaveAttribute('aria-pressed', 'true');
  await expect(all.nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(all.last()).toHaveAttribute('aria-pressed', 'false');
  await page.waitForTimeout(800);
  expect(playsSince(harness, before)).toEqual([]);
});

test('Mimic play into game: a newer word click supersedes the older one, with no obsolete play', async ({
  page,
  harness,
}) => {
  await page.addInitScript((key) => localStorage.setItem(key, 'game'), MODE_KEY);
  await host(page, harness);
  const dialog = await listen(page);
  const words = await firstSentence(dialog);
  // Watch the first word: an obsolete play would light it.
  await words.first().evaluate((el) => {
    const w = window as unknown as { litObsolete: boolean };
    w.litObsolete = false;
    new MutationObserver(() => {
      if (el.classList.contains('playing')) w.litObsolete = true;
    }).observe(el, { attributes: true, attributeFilter: ['class'] });
  });
  const before = harness.plays.length;
  // Two clicks in a row, the second while the first clip is still being made.
  await words.evaluateAll((buttons) => {
    (buttons[0] as HTMLButtonElement).click();
    (buttons[1] as HTMLButtonElement).click();
  });
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await expect(page.locator('.player-track')).toContainText('tone');
  // Both clips were made; only the newer one was heard, and only it was lit.
  await expect(dialog.getByRole('button', { name: /Replay Fixture ·/ }).first()).toBeEnabled();
  await expect(dialog.getByRole('button', { name: /Replay tone ·/ }).first()).toBeEnabled();
  await page.waitForTimeout(1500);
  expect(playsSince(harness, before)).toHaveLength(1);
  expect(
    await page.evaluate(() => (window as unknown as { litObsolete: boolean }).litObsolete),
  ).toBe(false);
});

test('Mimic waveform: a click plays from that point in the chosen mode, a drag only selects; the playing word clears on Stop and Panic', async ({
  page,
  harness,
}) => {
  await page.addInitScript((key) => localStorage.setItem(key, 'game'), MODE_KEY);
  await host(page, harness);
  const dialog = await listen(page);
  await firstSentence(dialog);
  // A few sentences in the view, so a play from the left of it goes through several words.
  await page.waitForTimeout(4000);

  // A click plays from there (into the game, as chosen): one Soundpad play, and one word lit at a time.
  let before = harness.plays.length;
  await clickWave(page, dialog, 0.3);
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await expect(playingWords(dialog)).toHaveCount(1);
  // Nothing is selected by a click.
  await expect(dialog.locator('.mimic-range')).toHaveCount(0);
  const stopPlaying = dialog.getByRole('button', { name: 'Stop playing' });
  await expect(stopPlaying).toBeVisible();
  // The player's Stop ends it, and the word is no longer lit.
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoStopSound()');
  await expect(playingWords(dialog)).toHaveCount(0);
  await expect(stopPlaying).toHaveCount(0);

  // Panic ends it too.
  before = harness.plays.length;
  await clickWave(page, dialog, 0.3);
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await expect(playingWords(dialog)).toHaveCount(1);
  await page
    .getByRole('button', { name: 'Panic: stop audio and pause guest playback', exact: true })
    .click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoStopSound()');
  await expect(playingWords(dialog)).toHaveCount(0);
  await page
    .locator('.playback-suspended')
    .getByRole('button', { name: 'Resume guest playback', exact: true })
    .click();

  // Stop playing beside the switch stops what plays in the game.
  before = harness.plays.length;
  await clickWave(page, dialog, 0.3);
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await stopPlaying.click();
  await expect.poll(() => harness.soundpad.at(-1)).toBe('DoStopSound()');
  await expect(playingWords(dialog)).toHaveCount(0);

  // A drag selects and its release never plays; nor does a press on an edge handle.
  await page.waitForTimeout(300);
  before = harness.plays.length;
  const canvas = dialog.getByRole('slider', { name: /Mimic buffer/ });
  let box = (await canvas.boundingBox())!;
  await page.mouse.move(box.x + box.width * 0.4, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.6, box.y + box.height / 2);
  await page.mouse.up();
  await expect(dialog.locator('.mimic-range')).toContainText('2.0 s');
  box = (await canvas.boundingBox())!;
  await page.mouse.click(box.x + box.width * 0.6, box.y + box.height / 2);
  await expect(dialog.locator('.mimic-range')).toContainText('2.0 s');
  await page.waitForTimeout(800);
  expect(playsSince(harness, before)).toEqual([]);

  // In Preview a waveform click plays nothing in the game (and keeps the selection).
  await mode(dialog).getByRole('radio', { name: 'Preview here' }).check();
  await clickWave(page, dialog, 0.2);
  await page.waitForTimeout(800);
  expect(playsSince(harness, before)).toEqual([]);
  await expect(dialog.locator('.mimic-range')).toContainText('2.0 s');
});

test('Mimic playing words with reduced motion: every word of the play is lit at once, statically', async ({
  page,
  harness,
}) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.addInitScript((key) => localStorage.setItem(key, 'game'), MODE_KEY);
  await host(page, harness);
  const dialog = await listen(page);
  await firstSentence(dialog);
  await page.waitForTimeout(4000);
  const before = harness.plays.length;
  await clickWave(page, dialog, 0.3);
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  // About 7 s to live: two or more sentences of three words, all lit together and without a transition.
  await expect.poll(() => playingWords(dialog).count()).toBeGreaterThanOrEqual(4);
  await expect(playingWords(dialog).first()).toHaveClass(/\bplaying-static\b/);
  const motion = await playingWords(dialog)
    .first()
    .evaluate((el) => {
      const style = getComputedStyle(el);
      return { transition: style.transitionDuration, animation: style.animationName };
    });
  expect(motion.animation).toBe('none');
  // The page's reduced-motion rule shortens every transition to 0.01 ms (studio.css); nothing fades.
  expect(motion.transition.split(',').every((d) => parseFloat(d) < 0.001)).toBe(true);
  // The lit words stay as they are while it plays, then clear on Stop.
  const lit = await playingWords(dialog).count();
  await page.waitForTimeout(600);
  expect(await playingWords(dialog).count()).toBe(lit);
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(playingWords(dialog)).toHaveCount(0);
});

test('Mimic play mode for a guest: Play into game needs remote playback, and nothing before the join time plays', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await page.keyboard.press('Escape');
  const hostDialog = await listen(page);
  await firstSentence(hostDialog);
  await page.waitForTimeout(4000); // audio the guest was not there for
  await page.getByRole('button', { name: 'Soundboard', exact: true }).click();
  const guest = await joinGuest(page);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow remote playback').uncheck();
  await page.keyboard.press('Escape');
  await guest.evaluate((key) => localStorage.setItem(key, 'game'), MODE_KEY);
  await guest.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = guest.getByRole('main', { name: 'Mimic', exact: true });
  // Without remote playback Play into game is not offered, and clicks preview (the remembered choice stays).
  const switcher = mode(dialog);
  await expect(switcher.getByRole('radio', { name: 'Play into game' })).toBeDisabled();
  await expect(switcher.getByRole('radio', { name: 'Preview here' })).toBeChecked();
  await expect(switcher).toContainText('The host has not allowed guests to play');
  expect(await guest.evaluate((key) => localStorage.getItem(key), MODE_KEY)).toBe('game');
  // Even sent past the page's checks, a play into the game is refused.
  const view = (await sendCommand(guest, 'historyTimeline')).data as {
    sessionId: string;
    rate: number;
    liveFrame: number;
    fromFrame: number;
  };
  await guest.waitForTimeout(1500);
  const refused = await sendCommand(guest, 'mimicClip', {
    startFrame: view.fromFrame + view.rate / 4,
    endFrame: view.fromFrame + view.rate,
    sourceId: view.sessionId,
    autoplay: true,
  });
  expect(refused.error).toBeTruthy();

  // A click on the waveform before the guest joined plays nothing and says why.
  await guest.waitForTimeout(1500);
  const before = harness.plays.length;
  await clickWave(guest, dialog, 0.05);
  await expect(guest.getByText(/recorded before you joined/).first()).toBeVisible();
  expect(playsSince(harness, before)).toEqual([]);

  // With remote playback on, the guest's remembered Play into game applies: one word, one play.
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow remote playback').check();
  await page.keyboard.press('Escape');
  await expect(switcher.getByRole('radio', { name: 'Play into game' })).toBeChecked();
  const words = await firstSentence(dialog);
  await words.first().click();
  await expect.poll(() => playsSince(harness, before)).toHaveLength(1);
  await guest.waitForTimeout(800);
  expect(playsSince(harness, before)).toHaveLength(1);
});
