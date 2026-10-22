// Smoke checks of a running demo preview: two people see each other live, the simulated teammates act,
// every route opens without a console error, and the main routes are reachable by clicking.
import { expect, test } from '@playwright/test';
import { CODES, DEMO_PID, PRIYA_PID, ROUTES, TEAM, expectHealthyServer, otherHost, watchConsole } from './_preview.js';

test('a vote locked in one window shows as locked in the other', async ({ browser, baseURL, request }) => {
  const created = await request.post(`/api/teams/${TEAM}/poker-sessions`, { headers: { 'X-RS': '1', Origin: new URL(baseURL).origin }, data: {} });
  expect(created.status()).toBe(201);
  const { code } = await created.json();
  const path = `/teams/${TEAM}/poker/${code}`;

  const demo = await browser.newContext({ baseURL });
  const priya = await browser.newContext({ baseURL: otherHost(baseURL) });
  const demoPage = await demo.newPage();
  const priyaPage = await priya.newPage();
  const problems = [...watchConsole(demoPage), ...watchConsole(priyaPage)];

  await demoPage.goto(path);
  await priyaPage.goto(`${path}?as=priya`);
  // Nobody has a seat of their own: each window seats the other.
  await expect(priyaPage.locator('.nt-hand')).toBeVisible();
  await expect(priyaPage.locator(`.nt-seat[data-pid="${PRIYA_PID}"]`)).toHaveCount(0);
  const demoSeat = priyaPage.locator(`.nt-seat[data-pid="${DEMO_PID}"]`);
  await expect(demoSeat).toHaveAttribute('data-status', 'empty');

  await demoPage.locator('.nt-hand-card[data-points="5"]').click();
  await expect(demoSeat).toHaveAttribute('data-status', 'selected');
  await demoPage.getByRole('button', { name: 'Lock In' }).click();
  await expect(demoSeat).toHaveAttribute('data-status', 'locked');
  await expect(demoPage.locator(`.nt-seat[data-pid="${PRIYA_PID}"]`)).toHaveAttribute('data-status', 'empty');

  expect(problems).toEqual([]);
  await expectHealthyServer(request);
  await demo.close();
  await priya.close();
});

test('simulated teammates lock in their votes on the demo session', async ({ page, request }) => {
  const problems = watchConsole(page);
  await page.goto(`/teams/${TEAM}/poker/${CODES.teamPoker}`);
  const lockedPeers = page.locator('.nt-seat[data-status="locked"]');
  await expect(lockedPeers.first()).toBeVisible();
  // One teammate starts locked in; the others lock in on their own timers.
  await expect.poll(() => lockedPeers.count(), { timeout: 45_000 }).toBeGreaterThanOrEqual(3);
  expect(problems).toEqual([]);
  await expectHealthyServer(request);
});

for (const { route, path, ready, act } of ROUTES) {
  test(`${route} opens without console errors`, async ({ page, request }) => {
    const problems = watchConsole(page);
    await page.goto(path);
    await expect(page.locator(ready).first()).toBeVisible();
    await act?.(page);
    await page.waitForTimeout(500);
    expect(problems).toEqual([]);
    await expectHealthyServer(request);
  });
}

test('the main routes are reachable from visible navigation', async ({ page }) => {
  const problems = watchConsole(page);
  await page.goto('/dashboard');
  await page.getByRole('link', { name: 'Teams', exact: true }).click();
  await expect(page).toHaveURL(/\/teams$/);
  await page.getByRole('link', { name: 'Demo Team' }).first().click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}`));

  await page.getByRole('tab', { name: 'Poker' }).click();
  await page.getByRole('link', { name: 'Join' }).first().click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}/poker/`));
  await expect(page.locator('.nt-table')).toBeVisible();
  await page.getByRole('link', { name: 'Back' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}`));

  await page.getByRole('tab', { name: 'Boards' }).click();
  await page.getByRole('link', { name: 'Open board' }).first().click();
  await expect(page).toHaveURL(/\/retro\//);
  await expect(page.locator('.retro-card').first()).toBeVisible();

  await page.goto(`/teams/${TEAM}`);
  await page.getByRole('link', { name: 'Settings' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}/settings`));

  await page.goto('/dashboard');
  await page.getByRole('link', { name: /^Mentions/ }).click();
  await expect(page).toHaveURL(/\/mentions$/);
  await page.getByRole('link', { name: 'Dashboard' }).click();
  await page.getByRole('link', { name: CODES.quickPoker }).click();
  await expect(page).toHaveURL(new RegExp(`/poker/${CODES.quickPoker}`));

  await page.goto('/dashboard');
  await page.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitem', { name: 'Account' }).click();
  await expect(page).toHaveURL(/\/account$/);
  await page.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitem', { name: 'Admin' }).click();
  await expect(page).toHaveURL(/\/admin\/users$/);
  expect(problems).toEqual([]);
});
