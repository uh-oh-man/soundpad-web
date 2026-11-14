import { defineConfig } from '@playwright/test';
import base from './playwright.config';
/**
 * Stage G before/after screenshots (review only, never compared in CI): the base config pointed at
 * tests/shots, so `npm run test:e2e` and its skip counts are unaffected. Run with
 * `SHOTS_LABEL=before npx playwright test --config playwright.shots.config.ts`; the images go to
 * artifacts/screenshots/stage-g/<label>/ (gitignored). E2E_PORT picks the dev server's port, as for the
 * e2e helpers, so a second checkout can run it at the same time.
 */
const port = process.env.E2E_PORT ?? '5173';
export default defineConfig({
  ...base,
  testDir: 'tests/shots',
  testMatch: '*.shots.ts',
  timeout: 120000,
  use: { ...base.use, baseURL: `http://127.0.0.1:${port}`, screenshot: 'off', trace: 'off' },
  webServer: {
    command: `npm run dev -- --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: !process.env.CI,
  },
});
