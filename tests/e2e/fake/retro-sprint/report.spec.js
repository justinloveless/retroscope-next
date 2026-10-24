import { expect, test } from '../_harness/fixtures.js';
import { problem } from '../../../web-fake-server/http.js';
import { TEAM_ID, boardFor, openBoard, serveTeam, serveTeamInfo, teamRoom } from '../retro-actions/_actions.js';
import { snapshot } from '../retro-board/_board.js';

const JIRA = { configured: true, siteHost: 'fake-team.atlassian.net', projectKey: 'RS', boardId: 7 };

const openButton = (page) => page.getByRole('button', { name: 'Sprint report' });
const sheet = (page) => page.getByRole('dialog', { name: 'Sprint report' });
const reportCalls = (jira) => jira.calls.filter((c) => c.route === '/sprint-report');
const teamRequests = (fake) => fake.requests.filter((r) => r.path.startsWith(`/api/teams/${TEAM_ID}`));

function setup(fake, jira = JIRA) {
  serveTeam(fake, teamRoom());
  serveTeamInfo(fake, jira);
  return fake.jira({ teamId: TEAM_ID });
}

test('a member opens the report of the last closed sprint and switches to another sprint', async ({ page, fake }) => {
  const jira = setup(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();

  const panel = sheet(page);
  await expect(panel).toBeVisible();
  await expect(panel.locator('.retro-sprint-picker')).toHaveText('Sprint 8');
  await expect(panel.locator('.retro-sprint-goal')).toHaveText('Goal Take card payments end to end');
  await expect(panel.locator('.retro-sprint-tile')).toHaveText([/^3\s*Done\s*5 pts$/, /^1\s*Done after close\s*5 pts$/, /^3\s*Not done\s*8 pts$/]);
  const unfinished = panel.locator('.retro-sprint-group[data-outcome="notDone"] .retro-sprint-row');
  await expect(unfinished).toHaveCount(3);
  await expect(unfinished.first().locator('.retro-sprint-chip')).toHaveText(['Carried in', '→ Sprint 9']);
  await expect(unfinished.last().locator('.retro-sprint-chip')).toHaveText(['→ Backlog']);
  await expect(panel.getByRole('link', { name: 'RS-8' })).toHaveAttribute('href', 'https://fake-team.atlassian.net/browse/RS-8');
  await expect(panel.locator('.retro-sprint-note').last()).toContainText('points are current estimates');
  expect(reportCalls(jira)).toHaveLength(1);

  await panel.locator('.retro-sprint-picker').click();
  await page.getByRole('menuitem', { name: 'Sprint 9 (active)' }).click();
  await expect(panel.locator('.retro-sprint-picker')).toHaveText('Sprint 9 (active)');
  await expect(panel.locator('.retro-sprint-tile')).toHaveCount(2);
  await expect(panel.locator('.retro-sprint-chip[data-kind="backlog"], .retro-sprint-chip[data-kind="next"]')).toHaveCount(0);
  expect(reportCalls(jira)).toHaveLength(2);
  expect(fake.requests.filter((r) => r.path.endsWith('/sprint-report')).map((r) => r.query.get('sprint'))).toEqual([null, '101']);
});

test('the picked sprint is what the panel opens with next time', async ({ page, fake }) => {
  setup(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  await sheet(page).locator('.retro-sprint-picker').click();
  await page.getByRole('menuitem', { name: 'Sprint 7' }).click();
  await expect(sheet(page).locator('.retro-sprint-goal')).toHaveText('Goal Guest checkout');

  await page.reload();
  await openButton(page).click();
  await expect(sheet(page).locator('.retro-sprint-picker')).toHaveText('Sprint 7');
});

test('a Jira failure says why and Try again recovers', async ({ page, fake }) => {
  const jira = setup(fake);
  await openBoard(page, fake, { snap: boardFor() });
  jira.failNext({ ...problem(503, 'jira_unavailable'), headers: { 'Retry-After': '60' } });
  await openButton(page).click();
  await expect(sheet(page).getByRole('alert')).toHaveText('Jira is unavailable right now. Try again in a moment.');
  await sheet(page).getByRole('button', { name: 'Try again' }).click();
  await expect(sheet(page).locator('.retro-sprint-row')).toHaveCount(7);
});

test('Escape and the close button close the panel and give focus back to the button', async ({ page, fake }) => {
  setup(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  await expect(sheet(page).locator('.retro-sprint-row').first()).toBeVisible();
  await expect(page.locator('.retro-board')).toHaveClass(/has-side-sheet/);
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  await expect(openButton(page)).toBeFocused();
  await expect(openButton(page)).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('.retro-board')).not.toHaveClass(/has-side-sheet/);

  await openButton(page).click();
  await sheet(page).getByRole('button', { name: 'Close' }).click();
  await expect(sheet(page)).toBeHidden();
  await expect(openButton(page)).toBeFocused();
});

test('on a wide screen the panel sits beside the columns; on a phone it covers the screen', async ({ page, fake }, testInfo) => {
  setup(fake);
  const phone = testInfo.project.name === 'mobile-webkit';
  if (!phone) {
    await page.setViewportSize({ width: 1280, height: 800 });
  }
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  await expect(sheet(page).locator('.retro-sprint-row').first()).toBeVisible();
  const viewport = page.viewportSize();
  const panel = await sheet(page).boundingBox();
  const columns = await page.locator('.retro-columns').boundingBox();
  if (phone) {
    expect(Math.round(panel.width)).toBe(viewport.width);
  } else {
    expect(Math.round(panel.x + panel.width)).toBe(viewport.width);
    expect(columns.x + columns.width).toBeLessThanOrEqual(panel.x + 1);
    const header = await page.locator('.app-header').boundingBox();
    expect(panel.y).toBeGreaterThanOrEqual(header.y + header.height - 1);
    await page.locator('.app-header-end button:visible').last().click({ trial: true });
  }
});

test('teams without Jira or without a board get no button and no report request', async ({ page, fake }) => {
  for (const jira of [{ configured: false }, { ...JIRA, boardId: null }]) {
    const control = setup(fake, jira);
    const before = fake.requests.length;
    const team = page.waitForResponse((r) => new URL(r.url()).pathname === `/api/teams/${TEAM_ID}`);
    await openBoard(page, fake, { snap: boardFor() });
    await (await team).finished();
    // The button starts hidden, so it proves the gate only once the page has applied the team response.
    await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0))));
    await expect(openButton(page)).toBeHidden();
    expect(fake.requests.slice(before).filter((r) => r.path.startsWith(`/api/teams/${TEAM_ID}/`))).toEqual([]);
    expect(reportCalls(control)).toHaveLength(0);
    control.uninstall();
  }
});

test('non-members and quick boards get no button and send no team request', async ({ page, fake }) => {
  setup(fake);
  for (const snap of [snapshot({ teamId: TEAM_ID }), snapshot()]) {
    await openBoard(page, fake, { snap });
    await expect(page.locator('.retro-share-open')).toBeVisible();
    await expect(openButton(page)).toBeHidden();
  }
  expect(teamRequests(fake)).toEqual([]);
});
