/**
 * Manual ribbon perf probe (Stage G spec 12.5). Plays a fixture sound for about 2 s at 1440×900 and logs
 * the frames drawn and the p50/p95 of the DEV-only `ribbon-draw` measures. It reports numbers and asserts
 * nothing: timing asserts flake in CI. Run with
 * `npx playwright test --config playwright.shots.config.ts ribbon-perf`.
 */
import { test, expect, host, playButton } from '../e2e/helpers/harness';

test('ribbon perf probe', async ({ page, harness }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await host(page, harness);
  await page.evaluate(() => performance.clearMeasures('ribbon-draw'));
  const before = await page.evaluate(
    () =>
      (window as unknown as { __soundpadRibbon?: { frames: number } }).__soundpadRibbon?.frames ??
      0,
  );
  await playButton(page, 'Airhorn').click();
  await expect(page.locator('.board-heading .header-ribbon')).toHaveAttribute(
    'data-ribbon-state',
    /live|waiting/,
  );
  await page.waitForTimeout(2000);
  const result = await page.evaluate((start) => {
    const ribbon = (window as unknown as { __soundpadRibbon?: { frames: number; state: string } })
      .__soundpadRibbon;
    const times = performance
      .getEntriesByName('ribbon-draw')
      .map((e) => e.duration)
      .sort((a, b) => a - b);
    const at = (q: number) =>
      times.length ? times[Math.min(times.length - 1, Math.floor(q * times.length))] : 0;
    return {
      frames: (ribbon?.frames ?? 0) - start,
      state: ribbon?.state,
      measures: times.length,
      p50: at(0.5),
      p95: at(0.95),
    };
  }, before);
  console.log(`RIBBON_PERF ${JSON.stringify(result)}`);
});
