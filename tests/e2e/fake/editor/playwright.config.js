// Runs the editor specs on their own (they serve their page through page.route and need no
// server): npx playwright test --config tests/e2e/fake/editor/playwright.config.js
// tests/e2e/playwright.config.js (U48) runs the same specs under ./build.sh fake-e2e.
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.js',
  fullyParallel: true,
  reporter: 'list',
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
    { name: 'mobile-webkit', use: { ...devices['iPhone 13'] } },
  ],
});
