// Playwright configuration for both e2e targets (./build.sh fake-e2e | e2e sets RS_E2E_TARGET).
// fake: specs under tests/e2e/fake/ run against tests/web-fake-server, started per test by the
// fixtures in fake/_harness/fixtures.js (which also supply baseURL). compose: every other spec runs
// against deploy/compose.test.yml at RS_E2E_BASE_URL.
import { defineConfig, devices } from '@playwright/test';

const target = process.env.RS_E2E_TARGET ?? 'fake';
const ci = !!process.env.CI;
// Rendered times and number formats must not depend on the machine that runs the specs.
const pinned = { timezoneId: 'UTC', locale: 'en-US' };

export default defineConfig({
  testDir: target === 'fake' ? './fake' : '.',
  testIgnore: target === 'fake' ? [] : ['fake/**'],
  testMatch: '**/*.spec.js',
  outputDir: '../../artifacts/e2e/results',
  fullyParallel: true,
  forbidOnly: ci,
  retries: 0,
  reporter: ci ? [['list'], ['html', { open: 'never', outputFolder: '../../artifacts/e2e/report' }]] : 'list',
  use: {
    baseURL: target === 'fake' ? undefined : process.env.RS_E2E_BASE_URL ?? 'https://localhost:18443',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'], ...pinned } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'], ...pinned } },
    { name: 'webkit', use: { ...devices['Desktop Safari'], ...pinned } },
    { name: 'mobile-webkit', use: { ...devices['iPhone 13'], ...pinned } },
  ],
});
