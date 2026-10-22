// Jira helpers for the real-stack specs. FakeJira gives every browser project its own Jira project and board
// (tests/Fakes.Host/SeedData.cs), so specs that edit issues never meet another project's run.
import { connectJira, createPoker, createTeam, expect, seedUsers } from './_real.js';
import { openTable } from './_poker.js';

const PLAYGROUNDS = { chromium: ['CHR', 11], firefox: ['FFX', 12], webkit: ['WBK', 13], 'mobile-webkit': ['MWK', 14] };

export function playground(testInfo) {
  const [projectKey, boardId] = PLAYGROUNDS[testInfo.project.name];
  return { projectKey, boardId, key: (n) => `${projectKey}-${n}` };
}

export const isPhone = (page) => page.viewportSize().width < 768;
export const row = (page, key) => page.locator(`.jira-row[data-key="${key}"]`);
export const bucket = (page, name) => page.locator('.jira-bucket').filter({ has: page.locator('.jira-bucket-name', { hasText: name }) });
export const drawer = (page) => page.locator('.jira-drawer');

/** A team of `names` (the first owns it) with Jira on this project's playground, and a team poker session. */
export async function jiraTable(open, testInfo, names) {
  const users = await seedUsers(names);
  const players = [];
  for (const user of users) {
    players.push(await open(user));
  }
  const team = await createTeam(players[0].page.request, players.slice(1).map((p) => p.page.request));
  const ground = playground(testInfo);
  await connectJira(players[0].page.request, team.id, ground);
  const session = await createPoker(players[0].page.request, team.id);
  const path = `/teams/${team.id}/poker/${session.code}`;
  return { players, team, ground, session, path };
}

/** Opens the team poker page and the Browse Jira panel (a sheet on phones). */
export async function openBrowse(page, path) {
  await page.goto(path);
  await page.locator('.nt-table').waitFor();
  await expect(page.locator('.jira-toggle')).toBeVisible();
  const panel = page.locator('.jira-browse');
  if (isPhone(page)) {
    await page.locator('.jira-toggle').click();
  }
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute('aria-busy', 'false');
  return panel;
}

export { openTable };
