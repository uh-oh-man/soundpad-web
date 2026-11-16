/**
 * Spotify for guests (2.46, a guest's "spotify button dont work"). The harness's Spotify is a fixture
 * (companion/harness/SpotifyFixture.cs): two tracks, never the PC's real Spotify.
 */
import type { Page } from '@playwright/test';
import { test, expect, host, joinGuest, useLocalGuests } from './helpers/harness';
import { spotifyMore } from './helpers/playback';

/**
 * Holds every status read a guest relays through the host's page until `releaseSpotify()`, as a slow
 * link to the host does: the guest's poll is then always on its way when they press.
 */
const holdGuestStatusReads = (page: Page) =>
  page.evaluate(async () => {
    const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
    const bridge = (controller as unknown as { bridge: { send: (...a: unknown[]) => unknown } })
      .bridge;
    const send = bridge.send.bind(bridge);
    const w = window as unknown as { held: number; releaseSpotify: () => void };
    let release = () => {};
    const released = new Promise<void>((r) => (release = r));
    w.held = 0;
    w.releaseSpotify = release;
    bridge.send = async (c: unknown, actor: unknown, ...rest: unknown[]) => {
      const command = c as { op: string; action?: string };
      if (
        command.op === 'spotify' &&
        actor === 'guest' &&
        (command.action ?? 'status') === 'status'
      ) {
        w.held++;
        await released;
      }
      return send(c, actor, ...rest);
    };
  });
const held = (page: Page) => page.evaluate(() => (window as unknown as { held: number }).held);
const release = (page: Page) =>
  page.evaluate(() => (window as unknown as { releaseSpotify: () => void }).releaseSpotify());

test("a guest's Play/Pause and Next press the host's Spotify while their status read is still on its way", async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const hostPlayer = page.getByRole('region', { name: 'Spotify player' });
  // The switch shows the companion's answer, so it changes once that arrives (a click, not check()).
  await hostPlayer.getByLabel('Enable Spotify desktop integration').click();
  await expect(hostPlayer.getByText('Fixture song', { exact: true })).toBeVisible();
  const guest = await joinGuest(page, 'Ann');
  const player = guest.getByRole('region', { name: 'Spotify player' });
  await expect(player.getByText('Fixture song', { exact: true })).toBeVisible();
  const toggle = player.getByRole('button', { name: 'Play or pause Spotify' });
  const next = player.getByRole('button', { name: 'Next Spotify track' });
  await expect(toggle).not.toHaveAttribute('aria-disabled', 'true');
  await expect(toggle).toHaveAttribute('title', 'Plays on the host’s PC');
  // Before 2.46 a press while the guest's own status read was on its way did nothing at all.
  await holdGuestStatusReads(page);
  await expect.poll(() => held(page)).toBeGreaterThan(0);
  await toggle.click();
  await expect(toggle.locator('svg.lucide-pause')).toBeVisible();
  await next.click();
  await expect(player.getByText('Second fixture song', { exact: true })).toBeVisible();
  // The status read they overtook answers later with the old track; it never undoes the press.
  await release(page);
  await guest.waitForTimeout(1500);
  await expect(player.getByText('Second fixture song', { exact: true })).toBeVisible();
  await expect(hostPlayer.getByText('Second fixture song', { exact: true })).toBeVisible({
    timeout: 10000,
  });
  await spotifyMore(guest);
  await expect(player.getByText('The host’s Spotify · shared session player')).toBeVisible();
  await guest.close();
});

test('a press waits for a slow Spotify status read on the companion instead of being answered busy', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const hostPlayer = page.getByRole('region', { name: 'Spotify player' });
  await hostPlayer.getByLabel('Enable Spotify desktop integration').click();
  await expect(hostPlayer.getByText('Fixture song', { exact: true })).toBeVisible();
  const guest = await joinGuest(page, 'Ann');
  const player = guest.getByRole('region', { name: 'Spotify player' });
  await expect(player.getByText('Fixture song', { exact: true })).toBeVisible();
  // Every read of Spotify now takes 2.5 s, so the host's poll (every 3 s) is nearly always reading
  // when the guest presses. Before 2.46 the companion answered such a press "Spotify is busy".
  harness.send('spotify-status-delay 2500');
  await guest.waitForTimeout(3500);
  await player.getByRole('button', { name: 'Next Spotify track' }).click();
  await expect(player.getByText('Second fixture song', { exact: true })).toBeVisible({
    timeout: 6000,
  });
  await spotifyMore(guest);
  await player.getByRole('button', { name: 'Previous Spotify track' }).click();
  await expect(player.getByText('Fixture song', { exact: true })).toBeVisible({ timeout: 6000 });
  // Every notice the guest's page showed (the diagnostics fixture keeps them): none said busy.
  expect(
    await guest.evaluate(() => (window as unknown as { shownNotices: string[] }).shownNotices),
  ).not.toContainEqual(expect.stringContaining('busy'));
  harness.send('spotify-status-delay 0');
  await guest.close();
});

test('with guest Spotify controls off, a guest sees the track, and its buttons say why and press nothing', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const hostPlayer = page.getByRole('region', { name: 'Spotify player' });
  await hostPlayer.getByLabel('Enable Spotify desktop integration').click();
  await expect(hostPlayer.getByText('Fixture song', { exact: true })).toBeVisible();
  const guest = await joinGuest(page, 'Ann');
  const player = guest.getByRole('region', { name: 'Spotify player' });
  await expect(player.getByText('Fixture song', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Spotify controls').uncheck();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  const next = player.getByRole('button', { name: 'Next Spotify track' });
  await expect(next).toHaveAttribute('aria-disabled', 'true');
  await expect(next).toHaveAccessibleDescription('The host has turned off guest Spotify controls.');
  await expect(player.getByText('The host has turned off guest Spotify controls.')).toBeVisible();
  // aria-disabled, not disabled: it keeps focus and still answers a press, with the reason.
  await next.focus();
  await guest.keyboard.press('Enter');
  await expect(
    guest
      .locator('.notification-stack')
      .getByText('The host has turned off guest Spotify controls.'),
  ).toBeVisible();
  // Nothing was pressed: the host still sees the first track, after a status read or two.
  await page.waitForTimeout(3500);
  await expect(hostPlayer.getByText('Fixture song', { exact: true })).toBeVisible();
  await expect(player.getByText('Fixture song', { exact: true })).toBeVisible();
  // Turned back on, the same button works.
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Spotify controls').check();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await expect(next).not.toHaveAttribute('aria-disabled', 'true');
  await next.click();
  await expect(player.getByText('Second fixture song', { exact: true })).toBeVisible();
  await guest.close();
});
