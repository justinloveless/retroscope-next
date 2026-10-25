// Security against the real stack (§j). The XSS corpus: markup-looking card text, javascript: links, a hostile display
// name mentioned on a card, and hostile ADF from Jira. Every context fails the test on a dialog, a CSP violation or a
// page error (_real.js); here the DOM is also searched for event-handler attributes and script URLs. Members only
// (UX18 decisions 1, 2): a signed-out visitor is sent to sign in before any page of a team, a non-member gets exactly
// the answer a team or room that does not exist gets (S9), and a member removed while a board is open loses it.
import { randomInt, randomUUID } from 'node:crypto';
import { BASE, PASSWORD, createBoard, createPoker, createTeam, expect, json, seedUsers, test } from './_real.js';
import { drawer, isPhone, jiraTable, openBrowse, row } from './_jira.js';
import { addCard, cardsIn, column, openBoard, retroBoard, typeCard } from './_retro.js';

const HOSTILE_NAME = '<img src=x onerror=alert(16)>';
const NO_ACCESS = "You can't open this page";
const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/** A well-formed room code that no room has: ten random letters of the code alphabet. */
const missingCode = () => Array.from({ length: 10 }, () => CROCKFORD[randomInt(CROCKFORD.length)]).join('');
const field = (page, label) => page.getByLabel(label, { exact: true }).filter({ visible: true });

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

test('a signed-out visitor is sent to sign in before any page of a team, and comes back to it after', async ({ open }) => {
  const [annUser] = await seedUsers(['Ann Gate']);
  const ann = await open(annUser);
  const team = await createTeam(ann.page.request);
  const poker = await createPoker(ann.page.request, team.id);
  const board = await createBoard(ann.page.request, team.id);
  const visitor = await open();

  // The gate answers before any room lookup, so a room that does not exist gets the same answer as one that does.
  for (const path of [
    `/teams/${team.id}`,
    `/teams/${team.id}?tab=retros`,
    `/teams/${team.id}/settings`,
    `/teams/${team.id}/poker/${poker.code}`,
    `/poker/${poker.code}`,
    `/retro/${board.code}`,
    `/retro/${missingCode()}`,
    '/teams',
    '/account',
    '/mentions',
    '/admin/users',
  ]) {
    const response = await visitor.page.request.get(path, { maxRedirects: 0 });
    expect(response.status(), path).toBe(302);
    expect(response.headers()['cache-control'], path).toBe('no-store');
    const to = new URL(response.headers().location, BASE);
    expect(to.origin, path).toBe(new URL(BASE).origin);
    expect(to.pathname, path).toBe('/login');
    expect(to.searchParams.get('return'), path).toBe(path);
  }
  for (const path of ['/', '/login', '/invite', '/auth/verify']) {
    expect((await visitor.page.request.get(path, { maxRedirects: 0 })).status(), path).toBe(200);
  }

  await visitor.page.goto(`/retro/${board.code}`);
  await expect(visitor.page).toHaveURL(/\/login\?return=/);
  await field(visitor.page, 'Email').fill(annUser.email);
  await field(visitor.page, 'Password').fill(PASSWORD);
  await visitor.page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(visitor.page).toHaveURL(new RegExp(`/retro/${board.code}$`));
  await expect(column(visitor.page, 'Good')).toBeVisible();
});

test('a non-member gets the same answer as for a team or room that does not exist', async ({ open }) => {
  const [annUser, ollyUser] = await seedUsers(['Ann Private', 'Olly Outsider']);
  const ann = await open(annUser);
  const olly = await open(ollyUser);
  const team = await createTeam(ann.page.request, [], `Hidden team ${randomUUID().slice(0, 8)}`);
  const poker = await createPoker(ann.page.request, team.id);
  const board = await createBoard(ann.page.request, team.id, { title: 'Hidden board title' });
  const nobody = { team: randomUUID(), code: missingCode() };

  const answer = async (path) => {
    const response = await olly.page.request.get(path, { headers: { 'X-RS': '1' } });
    return { status: response.status(), type: response.headers()['content-type'], body: await response.text() };
  };
  for (const [theirs, missing] of [
    [`/api/teams/${team.id}`, `/api/teams/${nobody.team}`],
    [`/api/teams/${team.id}/members`, `/api/teams/${nobody.team}/members`],
    [`/api/teams/${team.id}/boards`, `/api/teams/${nobody.team}/boards`],
    [`/api/teams/${team.id}/poker-sessions`, `/api/teams/${nobody.team}/poker-sessions`],
    [`/api/teams/${team.id}/action-items`, `/api/teams/${nobody.team}/action-items`],
    [`/api/poker/by-code/${poker.code}`, `/api/poker/by-code/${nobody.code}`],
    [`/api/retro/by-code/${board.code}`, `/api/retro/by-code/${nobody.code}`],
  ]) {
    const seen = await answer(theirs);
    expect(seen.status, theirs).toBe(404);
    expect(seen, theirs).toEqual(await answer(missing));
  }

  // A room page's boot document carries the room's snapshot for members only.
  const boot = async (path) => (await olly.page.request.get(`/api/boot?path=${encodeURIComponent(path)}`)).json();
  for (const [theirs, missing] of [
    [`/retro/${board.code}`, `/retro/${nobody.code}`],
    [`/poker/${poker.code}`, `/poker/${nobody.code}`],
    [`/teams/${team.id}/poker/${poker.code}`, `/teams/${nobody.team}/poker/${nobody.code}`],
  ]) {
    const seen = await boot(theirs);
    expect(seen.room, theirs).toBeUndefined();
    expect(seen, theirs).toEqual(await boot(missing));
  }

  // One screen for all of them, which names neither the team nor the room. A context of its own, with its own
  // address, so these page loads do not share the per-address budget of room-code lookups (30 a minute) above.
  const browsing = await open(ollyUser);
  const screens = new Set();
  for (const path of [
    `/teams/${team.id}`,
    `/teams/${nobody.team}`,
    `/teams/${team.id}?tab=retros`,
    `/retro/${board.code}`,
    `/retro/${nobody.code}`,
    `/teams/${team.id}/poker/${poker.code}`,
    `/poker/${nobody.code}`,
  ]) {
    await browsing.page.goto(path);
    const heading = browsing.page.getByRole('heading', { level: 1, name: NO_ACCESS });
    await expect(heading, path).toBeVisible();
    screens.add(await browsing.page.getByRole('main').filter({ has: heading }).innerText());
    for (const secret of [team.name, 'Hidden board title']) {
      expect(await browsing.page.locator('body').innerText(), path).not.toContain(secret);
      expect(await browsing.page.title(), path).not.toContain(secret);
    }
  }
  expect([...screens]).toHaveLength(1);
});

test('a member removed while the board is open is told they no longer have access', async ({ open }) => {
  const { players, team, board } = await retroBoard(open, ['Ann Remover', 'Bob Removed']);
  const [ann, bob] = players;
  await addCard(bob.page, 'Good', 'Written while still a member');

  const removed = await ann.page.request.delete(`/api/teams/${team.id}/members/${bob.user.id}`,
    { headers: { 'X-RS': '1', Origin: BASE } });
  expect(removed.ok(), await removed.text()).toBe(true);
  await expect(bob.page.getByRole('heading', { level: 1, name: 'You no longer have access' })).toBeVisible({ timeout: 15_000 });
  await expect(bob.page.locator('.retro-card')).toHaveCount(0);
  await expect(cardsIn(ann.page, 'Good').filter({ hasText: 'Written while still a member' })).toHaveCount(1);

  // From then on the board is one of the pages Bob cannot open, like any other.
  const lookup = await bob.page.request.get(`/api/retro/by-code/${board.code}`, { headers: { 'X-RS': '1' } });
  expect(lookup.status()).toBe(404);
  await bob.page.reload();
  await expect(bob.page.getByRole('heading', { level: 1, name: NO_ACCESS })).toBeVisible();
});
