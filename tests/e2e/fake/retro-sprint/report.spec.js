import { expect, test } from '../_harness/fixtures.js';
import { problem } from '../../../web-fake-server/http.js';
import { TEAM_ID, boardFor, openBoard, serveTeam, serveTeamInfo, teamRoom } from '../retro-actions/_actions.js';

const JIRA = { configured: true, siteHost: 'fake-team.atlassian.net', projectKey: 'RS', boardId: 7 };

const openButton = (page) => page.getByRole('button', { name: 'Sprint report' });
const sheet = (page) => page.getByRole('dialog', { name: 'Sprint report' });
const reportCalls = (jira) => jira.calls.filter((c) => c.route === '/sprint-report');

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
  await page.getByRole('menuitemradio', { name: 'Sprint 9 (active)' }).click();
  await expect(panel.locator('.retro-sprint-picker')).toHaveText('Sprint 9 (active)');
  await expect(panel.locator('.retro-sprint-tile')).toHaveCount(2);
  await expect(panel.locator('.retro-sprint-chip[data-kind="backlog"], .retro-sprint-chip[data-kind="next"]')).toHaveCount(0);
  expect(reportCalls(jira)).toHaveLength(2);
  expect(fake.requests.filter((r) => r.path.endsWith('/sprint-report')).map((r) => [r.query.get('sprint'), r.query.get('sprints')])).toEqual([[null, '4'], ['101', '4']]);
});

test('the picked sprint is what the panel opens with next time', async ({ page, fake }) => {
  setup(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  await sheet(page).locator('.retro-sprint-picker').click();
  await page.getByRole('menuitemradio', { name: 'Sprint 7' }).click();
  await expect(sheet(page).locator('.retro-sprint-goal')).toHaveText('Goal Guest checkout');

  await page.reload();
  await openButton(page).click();
  await expect(sheet(page).locator('.retro-sprint-picker')).toHaveText('Sprint 7');
});

test('the sprint menu lists 4 closed sprints, loads 8 more with the button or by scrolling, and stays inside the screen', async ({ page, fake }) => {
  setup(fake);
  await page.setViewportSize({ width: 1280, height: 520 });
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  const picker = sheet(page).locator('.retro-sprint-picker');
  await expect(picker).toHaveText('Sprint 8');
  await picker.click();
  const menu = page.getByRole('menu', { name: 'Sprints' });
  const items = menu.getByRole('menuitemradio');
  await expect(items).toHaveText(['Sprint 9 (active)', 'Sprint 8', 'Sprint 7', 'Sprint 6', 'Sprint 5']);
  await expect(menu.getByRole('menuitemradio', { name: 'Sprint 8' })).toBeFocused();
  await expect(menu.getByRole('menuitemradio', { name: 'Sprint 8' })).toHaveAttribute('aria-checked', 'true');

  await page.keyboard.press('End');
  await expect(menu.getByRole('menuitem', { name: 'Load more sprints' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(items).toHaveCount(12);
  await expect(menu.getByRole('menuitem', { name: 'Load more sprints' })).toBeHidden();
  await expect(menu.getByRole('menuitemradio', { name: 'Sprint 4' })).toBeFocused();
  const box = await menu.boundingBox();
  expect(box.y + box.height).toBeLessThanOrEqual(520);
  expect(await menu.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  expect(fake.requests.filter((r) => r.path.endsWith('/sprint-report')).map((r) => [r.query.get('sprint'), r.query.get('sprints')])).toEqual([[null, '4'], ['100', '12']]);

  await page.keyboard.press('End');
  await expect(menu.getByRole('menuitemradio', { name: 'Pilot' })).toBeFocused();
  await expect(menu.getByRole('menuitemradio', { name: 'Pilot' })).toBeInViewport();
  await page.keyboard.press('Enter');
  await expect(menu).toBeHidden();
  await expect(picker).toBeFocused();
  await expect(picker).toHaveText('Pilot');
  await expect(sheet(page).locator('.retro-sprint-empty')).toHaveText('No issues in this sprint.');
  await page.keyboard.press('ArrowDown');
  await expect(menu.getByRole('menuitemradio', { name: 'Pilot' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(picker).toBeFocused();
  await expect(sheet(page)).toBeVisible();
});

test('scrolling to the bottom of the sprint menu loads more sprints', async ({ page, fake }) => {
  setup(fake);
  await page.setViewportSize({ width: 1280, height: 300 });
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  await sheet(page).locator('.retro-sprint-picker').click();
  const menu = page.getByRole('menu', { name: 'Sprints' });
  await expect(menu.getByRole('menuitemradio')).toHaveCount(5);
  expect(await menu.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  await menu.evaluate((el) => el.scrollTo(0, el.scrollHeight));
  await expect(menu.getByRole('menuitemradio')).toHaveCount(12);
  expect(fake.requests.filter((r) => r.path.endsWith('/sprint-report')).map((r) => r.query.get('sprints'))).toEqual(['4', '12']);
});

test('the person filter keeps one person\'s issues and recounts the tiles', async ({ page, fake }) => {
  const jira = setup(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await openButton(page).click();
  const panel = sheet(page);
  const person = panel.locator('.retro-sprint-person');
  await expect(person).toHaveText('Everyone');
  await person.click();
  await expect(page.getByRole('menuitem')).toHaveText(['Everyone', 'Ada Fake · 3', 'ben Example · 1', 'Grace Fixture · 2', 'Unassigned · 1']);
  await page.getByRole('menuitem', { name: 'Grace Fixture · 2' }).click();
  await expect(panel.locator('.retro-sprint-row')).toHaveText([/RS-9/, /RS-7/]);
  await expect(panel.locator('.retro-sprint-tile')).toHaveText([/^1\s*Done\s*2 pts$/, /^0\s*Done after close\s*0 pts$/, /^1\s*Not done\s*3 pts$/]);

  await panel.locator('.retro-sprint-picker').click();
  await page.getByRole('menuitemradio', { name: 'Sprint 9 (active)' }).click();
  await expect(person).toHaveText('Grace Fixture · 1');
  await expect(panel.locator('.retro-sprint-row')).toHaveText([/RS-14/]);
  await panel.locator('.retro-sprint-picker').click();
  await page.getByRole('menuitemradio', { name: 'Sprint 7' }).click();
  await expect(person).toHaveText('Grace Fixture · 1');
  await person.click();
  await page.getByRole('menuitem', { name: 'Everyone' }).click();
  await expect(panel.locator('.retro-sprint-row')).toHaveCount(3);
  expect(reportCalls(jira)).toHaveLength(3);
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
    await page.locator('.app-header-actions button:visible').last().click({ trial: true });
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
