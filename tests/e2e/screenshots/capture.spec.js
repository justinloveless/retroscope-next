// `./build.sh screenshots`: every route and the key states, light and dark, desktop (1440×900) and
// iPhone 13, into artifacts/screenshots/<route>-<state>-<theme>-<viewport>.png. contact-sheet.mjs then
// writes the index.html that lays them out. A shot fails on any console error, like the smoke spec.
import { mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { devices, expect, test } from '@playwright/test';
import { CODES, ROUTES, TEAM, search, watchConsole } from './_preview.js';

const OUT = join(dirname(fileURLToPath(import.meta.url)), '../../../artifacts/screenshots');
mkdirSync(OUT, { recursive: true });

const { defaultBrowserType: _engine, ...IPHONE } = devices['iPhone 13'];
const VIEWPORTS = {
  desktop: { viewport: { width: 1440, height: 900 } },
  mobile: IPHONE,
};

const poker = `/teams/${TEAM}/poker/${CODES.teamPoker}`;

async function centre(locator) {
  const box = await locator.boundingBox();
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

/** Points at the hand, so the fan rises out of its resting peek. */
async function raiseHand(page) {
  await page.locator('.nt-hand').hover();
  await page.waitForTimeout(400);
}

/** The Browse Jira panel: open by default on desktop, a sheet behind the bottom bar's button on phones. */
async function jiraPanel(page) {
  if (page.viewportSize().width < 768) {
    await page.getByRole('navigation', { name: 'Session controls' }).getByRole('button', { name: 'Browse Jira' }).click();
  }
  await expect(page.locator('.jira-browse')).toHaveAttribute('aria-busy', 'false');
  await expect(page.locator('.jira-row').first()).toBeVisible();
}

/** Key states beyond the plain routes: `act` brings the page into the state after it has drawn. */
const STATES = [
  { route: 'team', state: 'tab-poker', path: `/teams/${TEAM}?tab=poker`, ready: 'text=stories pointed' },
  { route: 'team', state: 'tab-members', path: `/teams/${TEAM}?tab=members`, ready: 'text=Priya Raman' },
  { route: 'team', state: 'tab-actions', path: `/teams/${TEAM}?tab=actions`, ready: 'text=Quarantine a flaky e2e test' },
  { route: 'team', state: 'tab-kudos', path: `/teams/${TEAM}?tab=kudos`, ready: 'text=Kudos leaderboard' },
  { route: 'team-settings', state: 'jira', path: `/teams/${TEAM}/settings#jira`, ready: 'text=demo-retail.atlassian.net' },
  { route: 'poker', state: 'voting', path: poker, ready: '.nt-seat[data-status="locked"]' },
  { route: 'poker', state: 'revealed', path: `${poker}?round=1`, ready: '.nt-seat .nt-flip.is-flipped' },
  {
    route: 'poker',
    state: 'drag',
    path: poker,
    ready: '.nt-hand-card[data-points="8"]',
    async act(page) {
      await raiseHand(page);
      const from = await centre(page.locator('.nt-hand-card[data-points="8"]'));
      const field = await page.locator('.nt-field').boundingBox();
      await page.mouse.move(from.x, from.y);
      await page.mouse.down();
      await page.mouse.move(from.x + 12, from.y - 40, { steps: 3 });
      await page.mouse.move(field.x + field.width / 2, field.y + field.height / 3, { steps: 5 });
      await expect(page.locator('.nt-drop')).toBeVisible();
      return async () => {
        await page.keyboard.press('Escape');
        await page.mouse.up();
      };
    },
  },
  { route: 'poker', state: 'hand-raised', path: poker, ready: '.nt-hand-card[data-points="8"]', act: raiseHand },
  {
    route: 'poker',
    state: 'between-hold',
    path: poker,
    ready: '.nt-hand-card[data-points="5"]',
    async act(page) {
      await raiseHand(page);
      const from = await centre(page.locator('.nt-hand-card[data-points="5"]'));
      await page.mouse.move(from.x, from.y);
      await page.mouse.down();
      await page.mouse.move(from.x, from.y - 20, { steps: 3 });
      await page.mouse.move(...Object.values(await centre(page.locator('.nt-zone-right'))), { steps: 3 });
      await expect(page.locator('.nt-zone-right')).toHaveClass(/is-holding/);
      return async () => {
        await page.keyboard.press('Escape');
        await page.mouse.up();
      };
    },
  },
  { route: 'poker', state: 'crowd-12-voting', path: `/poker/${CODES.crowdPoker}?round=2`, ready: '.nt-seat[data-status="locked"]' },
  { route: 'poker', state: 'crowd-12-revealed', path: `/poker/${CODES.crowdPoker}?round=1`, ready: '.nt-seat .nt-flip.is-flipped' },
  { route: 'poker', state: 'jira-panel', path: poker, ready: '.nt-table', act: jiraPanel },
  {
    route: 'poker',
    state: 'jira-drawer',
    path: poker,
    ready: '.nt-table',
    async act(page) {
      await jiraPanel(page);
      await page.locator('.jira-row-open').first().click();
      await expect(page.locator('.jira-drawer .jira-fields')).toBeVisible();
      await page.waitForTimeout(300);
    },
  },
  { route: 'retro', state: 'thinking-timer-running', path: `/retro/${CODES.thinking}`, ready: '.retro-card' },
  { route: 'retro', state: 'voting', path: `/retro/${CODES.voting}`, ready: '.retro-card' },
  { route: 'retro', state: 'discussing', path: `/retro/${CODES.discussing}`, ready: '.retro-card' },
  { route: 'retro', state: 'closed', path: `/retro/${CODES.closed}`, ready: '.retro-card' },
  {
    route: 'retro',
    state: 'kudos-celebration',
    path: `/retro/${CODES.discussing}`,
    ready: '.retro-card',
    async act(page) {
      const answer = await page.request.post('/__demo/celebrate');
      expect([204, 409]).toContain(answer.status());
      await page.waitForTimeout(400);
    },
  },
  {
    route: 'retro',
    state: 'kudos-drawer-open',
    path: `/retro/${CODES.discussing}`,
    ready: '.retro-kudos-tab',
    async act(page) {
      await page.locator('.retro-kudos-tab').click();
      await expect(page.getByRole('dialog', { name: 'Give kudos' })).toBeVisible();
      await page.waitForTimeout(400);
    },
  },
  { route: 'admin', state: 'teams', path: '/admin/teams', ready: 'role=searchbox', act: search('Platform Guild') },
  { route: 'admin', state: 'stats', path: '/admin/stats', ready: 'text=liveRooms' },
  {
    route: 'dashboard',
    state: 'header-menu',
    path: '/dashboard',
    ready: 'text=Recent retros',
    async act(page) {
      const burger = page.getByRole('button', { name: 'Menu', exact: true });
      if (await burger.isVisible()) {
        await burger.click();
        await expect(page.getByRole('dialog', { name: 'Menu' })).toBeVisible();
      } else {
        await page.getByRole('button', { name: /Account menu/ }).click();
        await expect(page.getByRole('menuitem', { name: 'Sign out' })).toBeVisible();
      }
    },
  },
];

// Poker keeps its own toolbar, with the header's theme and account menus docked into it.
const headerOf = (shot) => (shot.path.includes('/poker/') ? '.nt-toolbar .app-dock' : 'header.app-header');

const SHOTS = [...ROUTES.map((r) => ({ ...r, state: r.state ?? 'default' })), ...STATES];

for (const shot of SHOTS) {
  for (const theme of ['light', 'dark']) {
    for (const [viewport, device] of Object.entries(VIEWPORTS)) {
      const name = `${shot.route}-${shot.state}-${theme}-${viewport}`;
      test(name, async ({ browser, baseURL }) => {
        const context = await browser.newContext({ ...device, baseURL, colorScheme: theme, reducedMotion: 'reduce' });
        const page = await context.newPage();
        const problems = watchConsole(page);
        await page.goto(shot.path);
        await expect(page.locator(shot.ready).first()).toBeVisible();
        await expect(page.locator(headerOf(shot))).toBeVisible();
        await page.waitForTimeout(700);
        const undo = await shot.act?.(page);
        await page.screenshot({ path: join(OUT, `${name}.png`) });
        await undo?.();
        expect(problems).toEqual([]);
        await context.close();
      });
    }
  }
}
