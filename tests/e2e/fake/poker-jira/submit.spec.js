import { expect, test } from '../_harness/fixtures.js';
import { BOB, CODE, R2, setupPoker, snapshot } from '../poker-table/_poker.js';
import { ME, R1, ROOM, TEAM, commands, jiraEntry, round, sent, setupJira } from './_jira.js';

const revealed = (extra = {}) => round(R1, 1, { ticketKey: 'RS-12', state: 'revealed', winner: 5, revealedAt: 1792497600000, ...extra });
const submitBox = (page) => page.locator('.jira-submit');
const chip = (page, points) => submitBox(page).getByRole('button', { name: String(points), exact: true });

async function openTable(page) {
  await page.goto(`/teams/${TEAM}/poker/${CODE}`);
  await page.locator('.nt-table').waitFor();
}

test('a revealed round suggests the winner; submitting shows pending, then the result, from the room', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed()] });
  await openTable(page);

  await expect(chip(page, 5)).toHaveAttribute('aria-pressed', 'true');
  await chip(page, 8).click();
  await expect(chip(page, 8)).toHaveAttribute('aria-pressed', 'true');
  await submitBox(page).getByRole('button', { name: 'Submit 8 pts to RS-12' }).click();
  expect((await sent(fake, 'poker.jira.submitPoints')).d).toEqual({ roundId: R1, points: 8 });

  await expect(submitBox(page)).toHaveAttribute('data-state', 'pending');
  await expect(submitBox(page).getByRole('status')).toHaveText('Submitting 8 pts to RS-12… (Ada)');
  await expect(submitBox(page).getByRole('button', { name: /^Submit/ })).toBeDisabled();

  fake.tx(ROOM, [
    { k: 'poker.jira.points', d: { roundId: R1, key: 'RS-12', points: 8, status: 'ok', by: ME.pid } },
    { k: 'poker.round.updated', d: { round: revealed({ version: 2, jira: { points: 8, at: 1792497660000, by: ME.pid } }) } },
  ]);
  await expect(submitBox(page)).toHaveAttribute('data-state', 'ok');
  await expect(submitBox(page).getByRole('status')).toHaveText('RS-12 has 8 pts in Jira · submitted by Ada');
});

test('minimized results put Show results beside the Submit button', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed()] });
  await openTable(page);
  await expect(page.getByRole('dialog', { name: 'Results' })).toBeVisible();
  await page.getByRole('dialog', { name: 'Results' }).getByRole('button', { name: 'Minimize' }).click();

  const send = submitBox(page).getByRole('button', { name: 'Submit 5 pts to RS-12' });
  const show = page.getByRole('button', { name: 'Show results' });
  await expect(show).toBeFocused();
  await expect(submitBox(page).getByRole('button', { name: 'Show results' })).toBeVisible();
  const [a, b] = [await send.boundingBox(), await show.boundingBox()];
  expect(Math.abs((a.y + a.height / 2) - (b.y + b.height / 2))).toBeLessThanOrEqual(2);
  expect(b.x).toBeGreaterThan(a.x);
  await expect(page.locator('.nt-dock .nt-winner')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Replay round' })).toHaveCount(0);

  await show.click();
  await expect(page.getByRole('dialog', { name: 'Results' })).toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Results' }).getByRole('button', { name: 'Replay round' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Show results' })).toHaveCount(0);
});

test('a round minimized before the team’s Jira settings arrive moves Show results beside Submit when they do', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed()] });
  await openTable(page);
  await page.getByRole('dialog', { name: 'Results' }).getByRole('button', { name: 'Minimize' }).click();
  await expect(submitBox(page).getByRole('button', { name: 'Show results' })).toBeVisible();

  // The reload restores the minimized round from this tab's memory; the team's settings are held back.
  let release;
  const held = new Promise((resolve) => {
    release = resolve;
  });
  await page.route(`**/api/teams/${TEAM}`, async (route) => {
    await held;
    await route.continue();
  });
  await page.reload();
  await page.locator('.nt-table').waitFor();
  const bar = page.locator('.nt-dock .nt-actions');
  await expect(bar.getByRole('button', { name: 'Show results' })).toBeVisible();
  await expect(submitBox(page)).toBeHidden();

  release();
  await expect(submitBox(page).getByRole('button', { name: 'Show results' })).toBeVisible();
  await expect(bar.getByRole('button', { name: 'Show results' })).toHaveCount(0);
});

test('a minimized ended round with Jira keeps only the submit row, and the empty bar goes', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed({ active: false })] });
  await openTable(page);
  await page.getByRole('dialog', { name: 'Results' }).getByRole('button', { name: 'Minimize' }).click();
  await expect(submitBox(page).getByRole('button', { name: 'Show results' })).toBeFocused();
  await expect(submitBox(page).getByRole('button', { name: 'Submit 5 pts to RS-12' })).toBeVisible();
  await expect(page.locator('.nt-dock-bar')).toBeHidden();
});

test('the suggested chip is the nearest to the winner, the higher one on a tie', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed({ winner: 4 }), round(R2, 2, { ticketKey: 'RS-13', state: 'revealed', winner: 34 })] });
  await openTable(page);
  await expect(chip(page, 5)).toHaveAttribute('aria-pressed', 'true');
  await expect(chip(page, 3)).toHaveAttribute('aria-pressed', 'false');
  await page.getByRole('button', { name: 'Next round' }).click();
  await expect(submitBox(page).getByRole('button', { name: 'Submit 21 pts to RS-13' })).toBeEnabled();
  await expect(chip(page, 21)).toHaveAttribute('aria-pressed', 'true');
});

test('another player’s submit is visible to everyone, including a failure and its reason', async ({ page, fake }) => {
  setupJira(fake, { viewer: BOB, rounds: [revealed()] });
  await openTable(page);
  await expect(submitBox(page)).toHaveAttribute('data-state', 'idle');

  fake.tx(ROOM, [{ k: 'poker.jira.points', d: { roundId: R1, key: 'RS-12', points: 5, status: 'pending', by: ME.pid } }]);
  await expect(submitBox(page).getByRole('status')).toHaveText('Submitting 5 pts to RS-12… (Ada)');
  await expect(chip(page, 3)).toBeDisabled();

  fake.tx(ROOM, [{ k: 'poker.jira.points', d: { roundId: R1, key: 'RS-12', points: 5, status: 'failed', by: ME.pid, code: 'jira_unavailable' } }]);
  await expect(submitBox(page)).toHaveAttribute('data-state', 'failed');
  await expect(submitBox(page).getByRole('status')).toHaveText(/Could not submit 5 pts to RS-12: Jira is unavailable right now/);
  await expect(submitBox(page).getByRole('button', { name: 'Submit 5 pts to RS-12' })).toBeEnabled();
});

test('a round already submitted shows its points from the snapshot', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed({ jira: { points: 3, at: 1792497660000, by: BOB.pid } })] });
  await openTable(page);
  await expect(submitBox(page).getByRole('status')).toHaveText('RS-12 has 3 pts in Jira · submitted by Bob');
});

test('a submit still in flight is refused with jira_pending and said so', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed()] });
  fake.onCommand('poker.jira.submitPoints', (ctx) => ctx.nack('jira_pending'));
  await openTable(page);
  await submitBox(page).getByRole('button', { name: 'Submit 5 pts to RS-12' }).click();
  await expect(page.getByText('A submit for this round is still in progress.')).toBeVisible();
});

test('submitting needs a Jira key on the round and the right to edit Jira', async ({ page, fake }) => {
  setupJira(fake, { rounds: [revealed({ ticketKey: undefined })] });
  await openTable(page);
  await expect(submitBox(page).getByRole('status')).toHaveText('Add a Jira issue key to this round to submit points.');
  await expect(chip(page, 5)).toBeDisabled();
  await expect(submitBox(page).getByRole('button', { name: 'Submit to Jira' })).toBeDisabled();
});

test('members of a team that keeps Jira edits to admins cannot submit', async ({ page, fake }) => {
  setupJira(fake, { membersCanEdit: false, rounds: [revealed()] });
  await openTable(page);
  await expect(submitBox(page).getByRole('status')).toHaveText('Only team admins can write to Jira in this team.');
  await expect(submitBox(page).getByRole('button', { name: 'Submit 5 pts to RS-12' })).toBeDisabled();
});

test('admins of such a team can', async ({ page, fake }) => {
  setupJira(fake, { role: 'admin', membersCanEdit: false, rounds: [revealed()] });
  await openTable(page);
  await expect(submitBox(page).getByRole('button', { name: 'Submit 5 pts to RS-12' })).toBeEnabled();
});

test('the chips are not shown while the round is voting', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-12' })] });
  await openTable(page);
  await expect(jiraEntry(page)).toBeVisible();
  await expect(submitBox(page)).toBeHidden();
});

test('quick sessions and teams without Jira get no Jira UI and no Jira requests', async ({ page, fake }) => {
  setupPoker(fake, { snap: snapshot({ teamId: undefined, rounds: [revealed()] }) });
  await page.goto(`/poker/${CODE}`);
  await page.locator('.nt-table').waitFor();
  await page.waitForTimeout(300);
  await expect(jiraEntry(page)).toHaveCount(0);
  await expect(page.locator('.nt-panel-left')).toBeHidden();
  await expect(submitBox(page)).toHaveCount(0);
  expect(fake.requests.filter((r) => r.path.startsWith('/api/teams/'))).toEqual([]);

  fake.route('GET', `/api/teams/${TEAM}`, { body: { id: TEAM, name: 'No Jira', role: 'member', jira: { configured: false } } });
  fake.room(ROOM, { snapshot: snapshot({ rounds: [revealed()] }), overlay: { own: [], heldByThisTab: false } });
  await page.goto(`/teams/${TEAM}/poker/${CODE}`);
  await page.locator('.nt-table').waitFor();
  await expect.poll(() => fake.requests.filter((r) => r.path === `/api/teams/${TEAM}`).length).toBe(1);
  await page.waitForTimeout(300);
  await expect(jiraEntry(page)).toHaveCount(0);
  await expect(page.locator('.nt-panel-left')).toBeHidden();
  await expect(submitBox(page)).toHaveCount(0);
  expect(fake.requests.filter((r) => r.path.includes('/jira'))).toEqual([]);
  expect(commands(fake, 'poker.jira.submitPoints')).toHaveLength(0);
});
