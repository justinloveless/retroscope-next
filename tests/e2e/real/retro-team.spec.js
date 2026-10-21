// A team's retros over the real server (§j): action items reaching the team tab and Jira and coming back as
// previous action items on the next board, kudos with the per-board limit and the recipient's celebration,
// a board made from a new template, and a password-protected board.
import { connectJira, createBoard, createTeam, expect, seedUsers, test } from './_real.js';
import { playground } from './_jira.js';
import { addCard, cardsIn, column, retroBoard } from './_retro.js';

test('an action item reaches the team tab and Jira, and comes back on the next board', async ({ open }, testInfo) => {
  const { players, team } = await retroBoard(open, ['Ann Action', 'Bob Action']);
  const [ann, bob] = players;
  const ground = playground(testInfo);
  await connectJira(ann.page.request, team.id, ground);
  await ann.page.reload();
  await addCard(bob.page, 'Action Items', 'Fix the flaky deploy');

  const controls = cardsIn(ann.page, 'Action Items').first().locator('.retro-action-item');
  await controls.locator('.retro-assignee').click();
  await ann.page.getByRole('menuitem', { name: 'Bob Action' }).click();
  await expect(cardsIn(bob.page, 'Action Items').first().locator('.retro-assignee')).toHaveText('Bob Action');
  await controls.getByRole('button', { name: 'Create in Jira' }).click();
  await expect(controls.locator('.retro-jira-key')).toHaveText(new RegExp(`^${ground.projectKey}-\\d+$`));

  await bob.page.goto(`/teams/${team.id}?tab=actions`);
  const item = bob.page.locator('.team-item').filter({ hasText: 'Fix the flaky deploy' });
  await expect(item).toBeVisible();
  await expect(item).toContainText('Bob Action');

  const next = await createBoard(ann.page.request, team.id, { title: 'Next sprint retro' });
  await ann.page.goto(`/retro/${next.code}`);
  const previous = ann.page.locator('.retro-prev-actions .retro-prev-item').filter({ hasText: 'Fix the flaky deploy' });
  await expect(previous).toBeVisible();
  await previous.locator('.retro-done').click();
  await expect(previous.locator('.retro-done')).toHaveAttribute('aria-pressed', 'true');
  // Done on the board: the team tab, filtered to open items, drops it live; it is listed under Done.
  await expect(item).toHaveCount(0);
  await bob.page.getByRole('button', { name: 'Status: Open' }).click();
  await bob.page.getByRole('menuitem', { name: 'Done' }).click();
  await expect(item.getByRole('button', { name: 'Re-open' })).toBeVisible();
});

test('kudos stop at the per-board limit and the recipient celebrates', async ({ open }) => {
  const { players } = await retroBoard(open, ['Ann Kudos', 'Bob Kudos', 'Cy Kudos']);
  const [ann, bob] = players;
  const kudosFor = (name) => ann.page.getByRole('dialog', { name: 'Give kudos' }).getByRole('group', { name: `Kudos for ${name}` });

  await ann.page.locator('.retro-kudos-button').click();
  const status = ann.page.getByRole('dialog', { name: 'Give kudos' }).getByRole('status');
  await expect(status).toHaveText('You have 3 of 3 kudos left on this board.');
  await kudosFor('Bob Kudos').getByRole('button', { name: 'Team Player' }).click();
  await expect(bob.page.locator('.retro-celebration')).toContainText('Ann Kudos recognised you as Team Player.');
  await expect(bob.page.locator('.retro-celebration')).toHaveCount(0, { timeout: 10_000 });

  await kudosFor('Bob Kudos').getByRole('button', { name: 'Innovator' }).click();
  await kudosFor('Cy Kudos').getByRole('button', { name: 'Problem Solver' }).click();
  await expect(status).toHaveText('You have 0 of 3 kudos left on this board.');
  await expect(kudosFor('Cy Kudos').getByRole('button', { name: 'Innovator' })).toBeDisabled();
  await kudosFor('Bob Kudos').getByRole('button', { name: 'Team Player' }).click();
  await expect(status).toHaveText('You have 1 of 3 kudos left on this board.');
  await expect(kudosFor('Cy Kudos').getByRole('button', { name: 'Innovator' })).toBeEnabled();
});

test('a board made from a new template has its columns', async ({ open }) => {
  const [owner] = await seedUsers(['Ann Template']);
  const { page } = await open(owner);
  const team = await createTeam(page.request);
  await page.goto(`/teams/${team.id}/settings`);
  await page.getByRole('button', { name: 'New template' }).click();
  const dialog = page.getByRole('dialog', { name: 'New template' });
  await dialog.getByLabel('Name', { exact: true }).fill('Mad Sad Glad');
  await dialog.getByLabel('Column 1 title').fill('Glad');
  await dialog.getByLabel('Column 2 title').fill('Sad');
  await dialog.getByRole('button', { name: 'Create template' }).click();
  await expect(page.locator('.team-template').filter({ hasText: 'Mad Sad Glad' })).toBeVisible();

  await page.goto(`/teams/${team.id}`);
  await page.getByRole('button', { name: 'New board' }).first().click();
  const form = page.getByRole('dialog', { name: 'New retro board' });
  await form.getByLabel('Title').fill('From the template');
  await form.getByRole('button', { name: /^Template:/ }).click();
  await page.getByRole('menuitem', { name: 'Mad Sad Glad' }).click();
  await form.getByRole('button', { name: 'Create board' }).click();
  await expect(page).toHaveURL(/\/retro\//);
  await expect(page.locator('.retro-column-list > .retro-column .retro-column-title')).toHaveText(['Glad', 'Sad', 'Kudos', 'Action Items']);
});

test('a password board lets a visitor in only with the password', async ({ open }) => {
  const { players, board } = await retroBoard(open, ['Ann Password']);
  const [ann] = players;
  await ann.page.getByRole('button', { name: 'Share' }).click();
  const share = ann.page.getByRole('dialog', { name: 'Share this board' });
  await share.getByRole('radio', { name: 'People with the password' }).check();
  await share.getByLabel('New board password').fill('hunter22');
  await share.getByRole('button', { name: 'Save' }).click();
  await expect(share.getByText('This board needs a password. Share it along with the link.')).toBeVisible();

  const [visitorUser] = await seedUsers(['Vic Visitor']);
  const visitor = await open(visitorUser);
  await visitor.page.goto(`/retro/${board.code}`);
  await visitor.page.getByLabel('Board password').fill('wrong one');
  await visitor.page.getByRole('button', { name: 'Open board' }).click();
  await expect(visitor.page.getByRole('alert')).toHaveText('That password is not right.');
  await visitor.page.getByLabel('Board password').fill('hunter22');
  await visitor.page.getByRole('button', { name: 'Open board' }).click();
  await expect(column(visitor.page, 'Good')).toBeVisible();
  await addCard(visitor.page, 'Good', 'Glad to be here');
  await expect(cardsIn(ann.page, 'Good').filter({ hasText: 'Glad to be here' })).toHaveCount(1);
});
