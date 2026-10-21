import { expect, test } from '../_harness/fixtures.js';
import { R1, TEAM, bucket, commands, openBrowse, round, row, sent, setupJira } from './_jira.js';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

test('the + button adds one issue as a new round, which then blocks the row', async ({ page, fake }) => {
  setupJira(fake);
  const panel = await openBrowse(page);
  await panel.getByRole('button', { name: 'Filters' }).click();
  await panel.getByRole('switch').click();

  await row(page, 'RS-20').getByRole('button', { name: 'Add RS-20 to rounds' }).click();
  const { d } = await sent(fake, 'poker.round.create');
  expect(d).toEqual({ roundId: expect.stringMatching(UUID), ticketKey: 'RS-20' });
  await expect(row(page, 'RS-20').locator('.jira-reason')).toHaveText('Pointing');
  await expect(page.locator('.nt-round-name')).toHaveText('Round 2 · RS-20');
});

test('Add all adds every unblocked issue of a bucket in one batch, in bucket order', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-13' })] });
  await page.addInitScript((team) => localStorage.setItem(`rs.jira.filters:${team}`, JSON.stringify({ status: 'all', points: 'any', hidePointed: false })), TEAM);
  await openBrowse(page);

  await expect(row(page, 'RS-13').locator('.jira-reason')).toHaveText('Pointing');
  await bucket(page, 'Sprint 9').getByRole('button', { name: 'Add all' }).click();
  const { d } = await sent(fake, 'poker.round.createMany');
  expect(d.rounds.map((r) => r.ticketKey)).toEqual(['RS-12', 'RS-14']);
  expect(new Set(d.rounds.map((r) => r.roundId)).size).toBe(2);
  await expect(page.getByText('Added 2 tickets to active rounds')).toBeVisible();
  await expect(bucket(page, 'Sprint 9').getByRole('button', { name: 'Add all' })).toBeDisabled();
});

test('an issue key typed by hand is added; anything else is refused before sending', async ({ page, fake }) => {
  setupJira(fake);
  const panel = await openBrowse(page);
  const input = panel.getByRole('textbox', { name: 'Issue key to add' });

  await input.fill('nope');
  await input.press('Enter');
  await expect(page.getByText('Enter an issue key such as PROJ-123.')).toBeVisible();
  expect(commands(fake, 'poker.round.create')).toHaveLength(0);

  await input.fill('42');
  await input.press('Enter');
  expect((await sent(fake, 'poker.round.create')).d.ticketKey).toBe('42');
  await expect(page.locator('.nt-round-name')).toHaveText('Round 2 · RS-42');
  await expect(input).toHaveValue('');
});

test('New ticket creates the issue in Jira and adds it to the rounds', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  const panel = await openBrowse(page);

  await panel.getByRole('button', { name: 'New ticket' }).click();
  const dialog = page.getByRole('dialog', { name: 'New Jira ticket' });
  await dialog.getByRole('textbox', { name: 'Summary' }).fill('Pay with a gift card');
  await dialog.getByRole('textbox', { name: 'Description' }).fill('Balance first, then the **card**.');
  await dialog.getByRole('button', { name: 'Create in Jira' }).click();

  await expect(dialog).toBeHidden();
  const create = jira.calls.find((c) => c.method === 'POST' && c.route === '/issues');
  expect(create.body).toEqual({ summary: 'Pay with a gift card', description: 'Balance first, then the **card**.' });
  expect((await sent(fake, 'poker.round.create')).d.ticketKey).toBe('RS-32');
  await expect(page.getByText('Created RS-32')).toBeVisible();
});

test('a refused create keeps the dialog open with Jira’s reason', async ({ page, fake }) => {
  setupJira(fake);
  const panel = await openBrowse(page);
  fake.route('POST', '/api/teams/:teamId/jira/issues', { status: 422, body: { code: 'jira_validation', status: 422, fields: { summary: 'Summary is too vague.' } } });

  await panel.getByRole('button', { name: 'New ticket' }).click();
  const dialog = page.getByRole('dialog', { name: 'New Jira ticket' });
  await dialog.getByRole('textbox', { name: 'Summary' }).fill('Stuff');
  await dialog.getByRole('button', { name: 'Create in Jira' }).click();
  await expect(page.getByText('Summary is too vague.')).toBeVisible();
  await expect(dialog).toBeVisible();
  expect(commands(fake, 'poker.round.create')).toHaveLength(0);
});
