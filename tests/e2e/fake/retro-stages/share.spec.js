import { expect, test } from '../_harness/fixtures.js';
import { json, problem } from '../../../web-fake-server/http.js';
import {
  ALICE, BOARD_ID, BOB, CAROL, CODE, GUEST, ROOM, asPerson, openAs, openBoard, personPage, servePeople, snapshot, teamSnapshot,
} from './_stages.js';

const shareDialog = (page) => page.getByRole('dialog', { name: 'Share this board' });

async function openShare(page) {
  await page.getByRole('button', { name: 'Share' }).click();
  return shareDialog(page);
}

test('everyone can copy the link; only managers see who may open the board', async ({ page, fake }) => {
  await openBoard(page, fake, { snap: teamSnapshot(), me: BOB });
  const dialog = await openShare(page);
  await expect(dialog.getByRole('textbox', { name: 'Board link' })).toHaveValue(`${fake.url}/retro/${CODE}`);
  await expect(dialog.getByText('Anyone with the link can open this board.')).toBeVisible();
  await expect(dialog.getByRole('radio')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Copy' }).click();
  await expect(dialog.getByRole('button', { name: /^(Copied|Press Ctrl\+C)$/ })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Copy' })).toBeVisible({ timeout: 4000 });
  await dialog.getByRole('button', { name: 'Close' }).click();

  await openBoard(page, fake, { snap: snapshot({ presence: [ALICE, GUEST] }), me: GUEST });
  await openShare(page);
  await expect(shareDialog(page).getByRole('radio')).toHaveCount(0);
});

test('a team admin protects the board with a password, shares it, and a visitor gets in with it', async ({ page, fake, browser }) => {
  let password = null;
  let visibility = 'link';
  const snap = teamSnapshot();
  servePeople(fake, { alice: ALICE, carol: CAROL }, snap, { boot: (me) => (me === ALICE ? { r: ROOM } : null) });
  fake.route('PATCH', `/api/retro/${BOARD_ID}`, (req) => {
    visibility = req.body.visibility ?? visibility;
    password = req.body.password ?? password;
    fake.tx(ROOM, [{ k: 'retro.board.meta', d: { ...snap.meta, visibility } }]);
    return { status: 204 };
  });
  fake.route('GET', `/api/retro/by-code/${CODE}`, () => json({ id: BOARD_ID, access: visibility === 'password' ? 'password' : 'granted' }));
  fake.route('POST', `/api/retro/${BOARD_ID}/unlock`, (req) => (req.body.password === password ? { status: 204 } : problem(403, 'wrong_password')));

  await asPerson(page, fake, 'alice');
  await openAs(page);
  const dialog = await openShare(page);
  await expect(dialog.getByRole('radio', { name: 'Anyone with the link' })).toBeChecked();
  await expect(dialog.getByRole('radio', { name: 'Team members only' })).toBeVisible();
  await dialog.getByRole('radio', { name: 'People with the password' }).check();
  await dialog.getByLabel('New board password').fill('abc');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Use at least 4 characters.');
  await dialog.getByLabel('New board password').fill('hunter22');
  await dialog.getByRole('button', { name: 'Save' }).click();

  await expect(dialog.getByRole('textbox', { name: 'Board password', exact: true })).toHaveValue('hunter22');
  await expect(dialog.getByText('It will not be shown again.')).toBeVisible();
  await expect(dialog.getByText('This board needs a password. Share it along with the link.')).toBeVisible();
  expect(fake.requests.filter((r) => r.method === 'PATCH').map((r) => r.json)).toEqual([{ visibility: 'password', password: 'hunter22' }]);
  expect(fake.requests.find((r) => r.method === 'PATCH').headers['x-rs']).toBe('1');

  const carol = await personPage(browser, fake, 'carol');
  await carol.goto(`/retro/${CODE}`);
  await carol.getByLabel('Board password').fill('wrong');
  await carol.getByRole('button', { name: 'Open board' }).click();
  await expect(carol.getByRole('alert')).toHaveText('That password is not right.');
  await carol.getByLabel('Board password').fill('hunter22');
  await carol.getByRole('button', { name: 'Open board' }).click();
  await expect(carol.locator('.retro-column')).toHaveCount(3);
  expect(carol.cspViolations).toEqual([]);
  await carol.context().close();
});

test('changing nothing closes the dialog; a refused change says why', async ({ page, fake }) => {
  fake.route('PATCH', `/api/retro/${BOARD_ID}`, () => problem(403, 'forbidden'));
  await openBoard(page, fake, { snap: snapshot({ meta: { title: 'Sprint 42 retro', visibility: 'password', archived: false } }) });
  const dialog = await openShare(page);
  await expect(dialog.getByRole('radio', { name: 'Team members only' })).toHaveCount(0);
  await expect(dialog.getByRole('radio', { name: 'People with the password' })).toBeChecked();
  await expect(dialog.getByText('Leave blank to keep the current password.', { exact: false })).toBeVisible();
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  expect(fake.requests.filter((r) => r.method === 'PATCH')).toEqual([]);

  await openShare(page);
  await dialog.getByRole('radio', { name: 'Anyone with the link' }).check();
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Only the board\'s creator or a team admin can change who may open it.');
});
