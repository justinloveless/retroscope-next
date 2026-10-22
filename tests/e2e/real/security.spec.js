// The XSS corpus against the real stack (§j security): markup-looking card text, javascript: links, a hostile display
// name mentioned on a card, and hostile ADF from Jira. Every context fails the test on a dialog, a CSP violation or a
// page error (_real.js); here the DOM is also searched for event-handler attributes and script URLs.
import { createBoard, createTeam, expect, json, seedUsers, test } from './_real.js';
import { drawer, isPhone, jiraTable, openBrowse, row } from './_jira.js';
import { addCard, cardsIn, openBoard, typeCard } from './_retro.js';

const HOSTILE_NAME = '<img src=x onerror=alert(16)>';

/** Every element carrying an on* attribute, and every link or frame whose URL is not http(s), mailto, a path or a blob. */
async function liveScript(page) {
  return page.evaluate(() => {
    const found = [];
    for (const el of document.querySelectorAll('*')) {
      for (const { name, value } of el.attributes) {
        if (name.toLowerCase().startsWith('on')) {
          found.push(`${el.tagName} ${name}`);
        }
        if (['href', 'src', 'action', 'formaction', 'xlink:href'].includes(name.toLowerCase())
          && /^\s*(javascript|data|vbscript):/i.test(value) && !/^data:image\//i.test(value)) {
          found.push(`${el.tagName} ${name}=${value}`);
        }
      }
    }
    return found;
  });
}

test('hostile card text, links and a hostile display name stay inert on the board', async ({ open }) => {
  const [annUser, malloryUser] = await seedUsers(['Ann Safe', 'Mallory Name']);
  const ann = await open(annUser);
  const mallory = await open(malloryUser);
  const players = [ann, mallory];
  // Renamed before the board's room loads: a loaded room keeps the roster names it read (docs/decisions/U45.md).
  const renamed = await mallory.page.request.patch('/api/me', json({ fullName: HOSTILE_NAME }));
  expect(renamed.ok(), await renamed.text()).toBe(true);
  const team = await createTeam(ann.page.request, [mallory.page.request]);
  const board = await createBoard(ann.page.request, team.id);
  for (const { page } of players) {
    await openBoard(page, board.code);
  }

  for (const text of [
    '<img src=x onerror=alert(1)>',
    '<script>alert(2)</script>',
    '[click](javascript:alert(3))',
    '<a href="javascript:alert(4)">x</a>',
    '"><svg onload=alert(5)>',
  ]) {
    await addCard(mallory.page, 'Good', text);
  }
  await typeCard(ann.page, 'Bad', 'Ask @');
  await ann.page.getByRole('option', { name: HOSTILE_NAME }).click();
  await ann.page.keyboard.press('Enter');

  for (const { page } of players) {
    await expect(cardsIn(page, 'Good')).toHaveCount(5);
    await expect(cardsIn(page, 'Good').filter({ hasText: '<img src=x onerror=alert(1)>' })).toHaveCount(1);
    await expect(cardsIn(page, 'Bad').first().locator('.mention')).toHaveText(`@${HOSTILE_NAME}`);
    await expect(page.locator('.retro-author-name', { hasText: HOSTILE_NAME }).first()).toBeVisible();
    expect(await liveScript(page)).toEqual([]);
  }
});

test('hostile ADF from Jira renders without live links or handlers', async ({ open }, testInfo) => {
  const { players, path, ground } = await jiraTable(open, testInfo, ['Ann Adf']);
  const [ann] = players;
  await openBrowse(ann.page, path);
  const key = ground.key(10);
  await row(ann.page, key).locator('.jira-row-open').click();
  const sheet = drawer(ann.page);
  await expect(sheet).toHaveAttribute('data-key', key);
  await expect(sheet.locator('.jira-loading')).toHaveCount(0);
  await expect(sheet.locator('.jira-description')).toContainText('<script>alert(12)</script>');
  await expect(sheet.locator('.jira-description')).toContainText('<img src=x onerror=alert(15)>');
  await expect(sheet.locator('.jira-description a')).toHaveCount(0);
  await expect(sheet).toContainText('<img src=x onerror=alert(10)> summary');
  expect(await liveScript(ann.page)).toEqual([]);
  if (!isPhone(ann.page)) {
    await sheet.locator('.jira-description').getByText('click me').click();
  }
  await ann.page.waitForTimeout(500);
  expect(await liveScript(ann.page)).toEqual([]);
});
