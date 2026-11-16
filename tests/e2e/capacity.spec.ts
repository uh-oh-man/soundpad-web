/**
 * Capacity benchmark (2.45, WP4, D13): 1, 4, 8 and 12 guest pages against the mock Soundpad, each pressing Play
 * and asking a query at random moments, all at once. It records click-to-start (a guest's Play until the
 * companion answered that it started) and query (statistics) times, p50 and p95, to
 * test-results/capacity-<browser>.json, and checks nothing was refused and every guest stayed connected.
 *
 * What it measures: the host page relaying every guest's commands to the companion, and the companion itself.
 * What it does not: real networks. Guests reach the host over the dev server's in-browser BroadcastChannel
 * stand-in (useLocalGuests), every page shares one browser on one PC, and the mock Soundpad answers at once.
 * The numbers behind the 12-place cap are in docs/DEVELOPERS.md.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { Page } from '@playwright/test';
import { expect, host, playButton, test, useLocalGuests } from './helpers/harness';

const LEVELS = [1, 4, 8, 12] as const;
/** Rounds per level: every guest plays once and asks once per round, at a random moment within it. */
const ROUNDS = 8;
const ROUND_MS = 1000;

type Sample = {
  play: { ms: number; started: boolean; error?: string };
  query: { ms: number; error?: string };
};

/** One guest's Play and query, after `delay` ms, timed in its page. */
const sample = (guest: Page, delay: number, pick: number) =>
  guest.evaluate(
    async ([delay, pick]) => {
      const { controller } = await import(/* @vite-ignore */ '/src/' + 'controller.ts');
      await new Promise((r) => setTimeout(r, delay));
      const { library } = controller.getSnapshot().state;
      const sound = library.sounds[pick % library.sounds.length];
      const out: Sample = { play: { ms: 0, started: false }, query: { ms: 0 } };
      let t = performance.now();
      try {
        const data = (await controller.run(
          'play',
          { soundId: sound.id, libraryVersion: library.version },
          { quiet: true },
        )) as { played?: boolean } | undefined;
        out.play = { ms: performance.now() - t, started: data?.played === true };
      } catch (e) {
        out.play = { ms: performance.now() - t, started: false, error: String(e) };
      }
      t = performance.now();
      try {
        await controller.run('statistics', {}, { quiet: true });
        out.query = { ms: performance.now() - t };
      } catch (e) {
        out.query = { ms: performance.now() - t, error: String(e) };
      }
      return out;
    },
    [delay, pick] as const,
  );

/** Nearest-rank percentile of `values` (ms), to 0.1 ms. */
export function percentile(values: number[], p: number) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const rank = Math.max(1, Math.ceil((p / 100) * sorted.length));
  return Math.round(sorted[rank - 1] * 10) / 10;
}

test('capacity: 1, 4, 8 and 12 guests play and query at once', async ({
  page,
  harness,
  browserName,
}) => {
  test.setTimeout(420_000);
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await expect(page.locator('#invite')).toHaveValue(/#join=/);
  const link = await page.locator('#invite').inputValue();
  await page.getByRole('button', { name: 'Close dialog' }).click();
  const guests: Page[] = [];
  const results: Record<string, unknown>[] = [];
  for (const level of LEVELS) {
    while (guests.length < level) {
      const guest = await page.context().newPage();
      // Guests share the context (the in-browser transport needs it), so each gets its own person and its own
      // key first; otherwise every page after the first would join as the first one.
      await guest.addInitScript(
        ([id, name]) => {
          localStorage.setItem('soundpad-profile', JSON.stringify({ id, name }));
          localStorage.setItem('soundpad-name-confirmed', '1');
          // A page key instead of the context's saved one: a key is who a guest is (2.41).
          Object.defineProperty(window, 'indexedDB', { value: undefined, configurable: true });
        },
        [crypto.randomUUID(), `Guest ${guests.length + 1}`] as const,
      );
      await guest.goto(link);
      await expect(playButton(guest, 'Airhorn')).toBeEnabled({ timeout: 30000 });
      await expect(guest.locator('header .status')).toHaveText('Connected');
      guests.push(guest);
    }
    await expect(
      page.locator('.board-heading').getByRole('status').filter({ hasText: 'connected guest' }),
    ).toHaveText(new RegExp(`${level} connected guests?`), { timeout: 30000 });
    const samples: Sample[] = [];
    const started = Date.now();
    for (let round = 0; round < ROUNDS; round++) {
      const batch = await Promise.all(
        guests.map((g, i) => sample(g, Math.random() * ROUND_MS, round * 7 + i)),
      );
      samples.push(...batch);
    }
    const plays = samples.map((s) => s.play);
    const startedPlays = plays.filter((p) => p.started).map((p) => p.ms);
    const result = {
      guests: level,
      samples: samples.length,
      seconds: Math.round((Date.now() - started) / 100) / 10,
      play: {
        p50: percentile(startedPlays, 50),
        p95: percentile(startedPlays, 95),
        max: percentile(startedPlays, 100),
        started: startedPlays.length,
        // A newer play from someone else replaced it before it started (by design, 2.35).
        replaced: plays.filter((p) => !p.started && !p.error).length,
        errors: plays.filter((p) => p.error).map((p) => p.error),
      },
      query: {
        p50: percentile(
          samples.map((s) => s.query.ms),
          50,
        ),
        p95: percentile(
          samples.map((s) => s.query.ms),
          95,
        ),
        max: percentile(
          samples.map((s) => s.query.ms),
          100,
        ),
        errors: samples.filter((s) => s.query.error).map((s) => s.query.error),
      },
    };
    results.push(result);
    test
      .info()
      .annotations.push({ type: `capacity ${level}`, description: JSON.stringify(result) });
  }
  const report = { browser: browserName, rounds: ROUNDS, roundMs: ROUND_MS, results };
  mkdirSync(resolve('test-results'), { recursive: true });
  writeFileSync(
    resolve('test-results', `capacity-${browserName}.json`),
    JSON.stringify(report, null, 2),
  );
  await test.info().attach('capacity', {
    body: JSON.stringify(report, null, 2),
    contentType: 'application/json',
  });
  for (const r of results as {
    play: { errors: unknown[]; p95: number | null };
    query: { errors: unknown[]; p95: number | null };
  }[]) {
    expect(r.play.errors).toEqual([]);
    expect(r.query.errors).toEqual([]);
    // A generous bound: these catch a collapse, not a slow PC.
    expect(r.play.p95 ?? 0).toBeLessThan(3000);
    expect(r.query.p95 ?? 0).toBeLessThan(3000);
  }
  for (const g of guests) await expect(g.locator('header .status')).toHaveText('Connected');
});
