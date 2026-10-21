import { json } from '../../../web-fake-server/http.js';
import { ADMIN, MEMBER, TEAM_ID, TYPE_1, TYPE_2, setupTeam, teamUrl, choose, expect, test } from './_team.js';

test.describe('kudos tab', () => {
  test('shows the leaderboard per period with a column per kudos type', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/endorsements/leaderboard`, (req) => {
      calls.leaderboard.push(req);
      const all = req.query.get('period') === 'all';
      return json({
        entries: [
          { pid: MEMBER, name: 'Mia Member', total: all ? 9 : 3, counts: { [TYPE_1]: all ? 6 : 2, [TYPE_2]: all ? 3 : 1 } },
          { pid: ADMIN, name: 'Adam Admin', total: 1, counts: { [TYPE_2]: 1 } },
        ],
      });
    });
    await page.goto(teamUrl('?tab=kudos'));

    const table = page.locator('.team-leaderboard');
    await expect(table.locator('thead th')).toHaveText(['#', 'Member', '🤝Helper', '🎓Teacher', 'Total']);
    await expect(table.locator(`tr[data-pid="${MEMBER}"] td`)).toHaveText(['1', 'MMMia Member', '2', '1', '3']);
    await expect(table.locator(`tr[data-pid="${ADMIN}"] td`)).toHaveText(['2', 'AAAdam Admin', '0', '1', '1']);
    expect(calls.leaderboard.at(-1).query.get('period')).toBe('30d');

    await choose(page.getByLabel('Period'), 'All time');
    await expect(page).toHaveURL(/period=all/);
    await expect(table.locator(`tr[data-pid="${MEMBER}"] td`).last()).toHaveText('9');
    expect(calls.leaderboard.at(-1).query.get('period')).toBe('all');
  });

  test('says so when nobody got kudos yet', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl('?tab=kudos'));
    await expect(page.getByText('No kudos in this period yet.')).toBeVisible();
  });
});
