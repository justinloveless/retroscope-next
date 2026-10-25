// Smoke checks of a running demo preview: two people see each other live, the simulated teammates act,
// every route opens without a console error, and the main routes are reachable by clicking.
import { expect, test } from '@playwright/test';
import { CODES, DEMO_PID, GUILD, PRIYA_PID, ROUTES, TEAM, expectHealthyServer, otherHost, watchConsole } from './_preview.js';

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

for (const { route, path, ready, act, allow } of ROUTES) {
  test(`${route} opens without console errors`, async ({ page, request }) => {
    const problems = watchConsole(page, { allow });
    await page.goto(path);
    await expect(page.locator(ready).first()).toBeVisible();
    await act?.(page);
    await page.waitForTimeout(500);
    expect(problems).toEqual([]);
    await expectHealthyServer(request);
  });
}

test('the main routes are reachable from the header, the sections and the crumbs', async ({ page }) => {
  const problems = watchConsole(page);
  const header = page.locator('header.app-header');
  const sections = header.getByRole('navigation', { name: 'Demo Team sections' });

  await page.goto('/');
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}$`));
  await expect(page.getByText('Latest session')).toBeVisible();

  await sections.getByRole('link', { name: 'Retros' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}\\?tab=retros$`));
  await page.getByRole('main').getByRole('link', { name: 'Sprint 42 retro' }).click();
  await expect(page).toHaveURL(/\/retro\//);
  await expect(page.locator('.retro-card').first()).toBeVisible();
  await page.getByRole('main').getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Retros' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}\\?tab=retros$`));

  await sections.getByRole('link', { name: 'Poker' }).click();
  await page.getByRole('link', { name: 'Join' }).first().click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}/poker/`));
  await expect(page.locator('.nt-table')).toBeVisible();
  await page.locator('.nt-toolbar').getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Poker' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}$`));

  await header.getByRole('link', { name: 'Team settings' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM}/settings`));

  await header.getByRole('link', { name: /^Mentions/ }).click();
  await expect(page).toHaveURL(/\/mentions$/);

  await header.getByRole('button', { name: 'Demo Team', exact: true }).click();
  await page.getByRole('menuitemradio', { name: /Platform Guild/ }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${GUILD}$`));
  await header.getByRole('button', { name: 'Platform Guild', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Create a team' }).click();
  await expect(page).toHaveURL(/\/teams$/);

  await header.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitem', { name: 'Account' }).click();
  await expect(page).toHaveURL(/\/account$/);
  await header.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitem', { name: 'Admin' }).click();
  await expect(page).toHaveURL(/\/admin\/users$/);
  expect(problems).toEqual([]);
});
