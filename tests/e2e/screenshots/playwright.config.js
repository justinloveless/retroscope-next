// Playwright against a running demo preview (tests/web-fake-server/demo). `./build.sh screenshots` starts
// the demo and sets RS_PREVIEW_URL; against `./build.sh preview` it defaults to http://localhost:5173.
// Chromium only: the iPhone 13 shots emulate the device in Chromium, so only `npx playwright install
// chromium` is needed.
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.js',
  outputDir: '../../../artifacts/screenshots-results',
  fullyParallel: true,
  workers: 4,
  retries: 0,
  timeout: 60_000,
  reporter: 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: process.env.RS_PREVIEW_URL ?? 'http://localhost:5173',
    // Pinned, so dates and times in the shots do not depend on the machine taking them.
    timezoneId: 'UTC',
    locale: 'en-US',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium' }],
});
