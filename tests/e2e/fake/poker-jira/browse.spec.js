import { expect, test } from '../_harness/fixtures.js';
import { R2 } from '../poker-table/_poker.js';
import { OTHER_TEAM, R1, TEAM, bucket, openBrowse, round, row, setupJira } from './_jira.js';

const pointing = round(R1, 1, { ticketKey: 'RS-12' });
const pointed = round(R2, 2, { ticketKey: 'RS-31', state: 'revealed', active: false, winner: 3 });

test('buckets keep the server order; Pointing and Pointed rows are hidden by default and blocked when shown', async ({ page, fake }) => {
  const { browseQueries } = setupJira(fake, { rounds: [pointing, pointed] });
  const panel = await openBrowse(page);

  await expect(panel.locator('.jira-bucket-name')).toHaveText(['Sprint 9', 'Sprint 10', 'Backlog']);
  await expect(bucket(page, 'Sprint 9')).toContainText('Active');
  expect(browseQueries[0]).toEqual({ status: 'not-done', points: 'unpointed', scope: 'board' });
  await expect(row(page, 'RS-20')).toBeVisible();
  await expect(row(page, 'RS-12')).toHaveCount(0);
  await expect(row(page, 'RS-31')).toHaveCount(0);

  await panel.getByRole('button', { name: 'Filters' }).click();
  await panel.getByRole('switch').click();
  await expect(row(page, 'RS-12').locator('.jira-reason')).toHaveText('Pointing');
  await expect(row(page, 'RS-31').locator('.jira-reason')).toHaveText('Pointed');
  await expect(row(page, 'RS-12').getByRole('button', { name: /^Add/ })).toHaveCount(0);
  await expect(row(page, 'RS-20').getByRole('button', { name: 'Add RS-20 to rounds' })).toBeEnabled();
  await expect(row(page, 'RS-12').locator('svg[aria-label="Story"]')).toBeVisible();
  await expect(row(page, 'RS-12').locator('.jira-parent')).toHaveText('Checkout epic');
  await expect(row(page, 'RS-12').locator('.jira-status')).toHaveText('To Do');
  await expect(bucket(page, 'Backlog').getByRole('button', { name: 'Add all' })).toBeDisabled();
});

test('a pending preview round does not block its issue', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'ABC-1' }), round(R2, 2, { ticketKey: 'RS-20', pending: true, pendingOwner: '0a000000-0000-4000-8000-000000000002' })] });
  await openBrowse(page);
  await expect(row(page, 'RS-20').getByRole('button', { name: 'Add RS-20 to rounds' })).toBeEnabled();
});

test('search runs on Enter, not per keystroke, and shows pointed issues anyway', async ({ page, fake }) => {
  const { browseQueries } = setupJira(fake, { rounds: [pointing] });
  const panel = await openBrowse(page);
  const before = browseQueries.length;

  const search = panel.getByRole('searchbox', { name: 'Search Jira' });
  await search.fill('rs-12');
  await page.waitForTimeout(300);
  expect(browseQueries.length).toBe(before);
  await search.press('Enter');
  await expect.poll(() => browseQueries.at(-1)?.q).toBe('rs-12');
  await expect(row(page, 'RS-12').locator('.jira-reason')).toHaveText('Pointing');
});

test('filters and collapsed buckets persist per team', async ({ page, fake }) => {
  const { browseQueries } = setupJira(fake);
  const panel = await openBrowse(page);

  await panel.getByRole('button', { name: 'Filters' }).click();
  await panel.locator('.jira-filter-status').click();
  await page.getByRole('menuitem', { name: 'All statuses' }).click();
  await expect.poll(() => browseQueries.at(-1)?.status).toBe('all');
  await panel.locator('.jira-filter-points').click();
  await page.getByRole('menuitem', { name: '5 pts' }).click();
  await expect.poll(() => browseQueries.at(-1)?.points).toBe('5');
  await expect(row(page, 'RS-13')).toBeVisible();
  await expect(row(page, 'RS-12')).toHaveCount(0);
  await bucket(page, 'Sprint 9').getByRole('button', { name: /Sprint 9/ }).click();
  await expect(bucket(page, 'Sprint 9').getByRole('button', { name: /Sprint 9/ })).toHaveAttribute('aria-expanded', 'false');
  await expect(row(page, 'RS-13')).toHaveCount(0);

  const seen = browseQueries.length;
  const reloaded = await openBrowse(page);
  await expect.poll(() => browseQueries.length).toBeGreaterThan(seen);
  expect(browseQueries[seen]).toEqual({ status: 'all', points: '5', scope: 'board' });
  await expect(reloaded.locator('.jira-filter-status')).toHaveText('All statuses');
  await expect(bucket(page, 'Sprint 9').getByRole('button', { name: /Sprint 9/ })).toHaveAttribute('aria-expanded', 'false');

  const stored = await page.evaluate(([team, other]) => [localStorage.getItem(`rs.jira.filters:${team}`), localStorage.getItem(`rs.jira.filters:${other}`)], [TEAM, OTHER_TEAM]);
  expect(JSON.parse(stored[0])).toMatchObject({ status: 'all', points: '5', showFilters: true });
  expect(stored[1]).toBeNull();
});

test('stored filters with unknown values fall back to the defaults', async ({ page, fake }) => {
  const { browseQueries } = setupJira(fake);
  await page.addInitScript((team) => localStorage.setItem(`rs.jira.filters:${team}`, JSON.stringify({ status: 'Bogus', points: 'many', scope: 'all', hidePointed: 'yes' })), TEAM);
  await openBrowse(page);
  expect(browseQueries[0]).toEqual({ status: 'not-done', points: 'unpointed', scope: 'all' });
});

test('a Jira failure shows its reason and Refresh tries again', async ({ page, fake }) => {
  setupJira(fake);
  const remove = fake.route('GET', '/api/teams/:teamId/jira/browse', { status: 503, body: { code: 'jira_unavailable', status: 503 } });
  const panel = await openBrowse(page);
  await expect(panel.locator('.jira-notice')).toHaveText(/Jira is unavailable right now/);
  remove();
  await panel.getByRole('button', { name: 'Refresh' }).click();
  await expect(row(page, 'RS-20')).toBeVisible();
  await expect(panel.locator('.jira-notice')).toBeHidden();
});
