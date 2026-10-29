import { defineConfig, devices } from '@playwright/test';
const browser = process.env.TEST_BROWSER || 'chromium';
const device =
  browser === 'firefox'
    ? 'Desktop Firefox'
    : browser === 'webkit'
      ? 'Desktop Safari'
      : 'Desktop Chrome';
export default defineConfig({
  testDir: 'tests/e2e',
  timeout: 45000,
  expect: { timeout: 10000 },
  workers: 1,
  fullyParallel: false,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: 'npm run dev -- --port 5173 --strictPort',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: !process.env.CI,
  },
  projects: [{ name: browser, use: { ...devices[device] } }],
});
