import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, CAROL, MEMBERS, ROOM, TYPES, TYPE_HELPER, TYPE_IDEAS, boardFor, card, column, openBoard, sent, serveTeam } from './_actions.js';

const kudosButton = (page) => page.locator('.retro-kudos-tab');
const drawer = (page) => page.getByRole('dialog', { name: 'Give kudos' });
const kudosFor = (page, name) => drawer(page).getByRole('group', { name: `Kudos for ${name}` });

const T1 = 1_760_000_300_000;

function endorsement(n, fields) {
  return { id: `ed000000-0000-4000-8000-${String(n).padStart(12, '0')}`, typeId: TYPE_HELPER, from: ALICE.pid, to: BOB.pid, createdAt: T1 + n, ...fields };
}

/** Answers give and revoke like the board decider, with the per-board limit read live. */
function serveKudos(fake, limit) {
  const given = [];
  fake.onCommand('retro.endorse.give', (ctx) => {
    const d = ctx.frame.d;
    if (given.filter((e) => e.from === ctx.conn.me.pid).length >= limit.max) {
      ctx.nack('endorse_limit');
      return;
    }
    const added = { id: d.endorsementId, typeId: d.typeId, from: ctx.conn.me.pid, to: d.toUserId, createdAt: T1 };
    given.push(added);
    ctx.commit([{ k: 'retro.endorsement.added', d: { endorsement: added } }]);
  });
  fake.onCommand('retro.endorse.revoke', (ctx) => {
    const index = given.findIndex((e) => e.id === ctx.frame.d.endorsementId);
    given.splice(index, 1);
    ctx.commit([{ k: 'retro.endorsement.removed', d: { endorsementId: ctx.frame.d.endorsementId } }]);
  });
  return given;
}

test('giving and taking back kudos, within the per-board limit', async ({ page, fake }) => {
  serveTeam(fake);
  serveKudos(fake, { max: 1 });
  await openBoard(page, fake, { snap: boardFor({ maxEndorsements: 1 }) });
  await expect(kudosButton(page)).toHaveAccessibleName('Kudos 1 left');

  await kudosButton(page).click();
  await expect(drawer(page).getByRole('status')).toHaveText('You have 1 of 1 kudos left on this board.');
  await expect(drawer(page).locator('.retro-kudos-person')).toHaveCount(2);
  await expect(kudosFor(page, 'Bob').getByRole('button')).toHaveText(['🤝Team Player', '💡Innovator']);

  await kudosFor(page, 'Bob').getByRole('button', { name: 'Team Player' }).click();
  await expect(kudosFor(page, 'Bob').getByRole('button', { name: 'Team Player' })).toHaveAttribute('aria-pressed', 'true');
  await expect(drawer(page).getByRole('status')).toHaveText('You have 0 of 1 kudos left on this board.');
  await expect(kudosFor(page, 'Carol Danvers').getByRole('button', { name: 'Innovator' })).toBeDisabled();
  await expect(kudosButton(page)).toHaveAccessibleName('Kudos 0 left');

  await kudosFor(page, 'Bob').getByRole('button', { name: 'Team Player' }).click();
  await expect(kudosFor(page, 'Bob').getByRole('button', { name: 'Team Player' })).toHaveAttribute('aria-pressed', 'false');
  await expect(kudosFor(page, 'Carol Danvers').getByRole('button', { name: 'Innovator' })).toBeEnabled();

  const [give] = sent(fake, 'retro.endorse.give');
  expect(give).toEqual({ endorsementId: expect.stringMatching(/^[0-9a-f-]{36}$/), toUserId: BOB.pid, typeId: TYPE_HELPER });
  expect(sent(fake, 'retro.endorse.revoke')).toEqual([{ endorsementId: give.endorsementId }]);
});

test('the limit follows team.settings live', async ({ page, fake }) => {
  serveTeam(fake);
  const limit = { max: 1 };
  serveKudos(fake, limit);
  await openBoard(page, fake, { snap: boardFor({ maxEndorsements: 1, endorsements: [endorsement(1)] }) });
  await kudosButton(page).click();
  await expect(drawer(page).getByRole('status')).toHaveText('You have 0 of 1 kudos left on this board.');
  await expect(kudosFor(page, 'Carol Danvers').getByRole('button', { name: 'Team Player' })).toBeDisabled();

  limit.max = 2;
  fake.tx(ROOM, [{ k: 'team.settings', d: { maxEndorsements: 2 } }]);
  await expect(drawer(page).getByRole('status')).toHaveText('You have 1 of 2 kudos left on this board.');
  await kudosFor(page, 'Carol Danvers').getByRole('button', { name: 'Team Player' }).click();
  await expect(kudosFor(page, 'Carol Danvers').getByRole('button', { name: 'Team Player' })).toHaveAttribute('aria-pressed', 'true');

  fake.tx(ROOM, [{ k: 'team.settings', d: { maxEndorsements: 1 } }]);
  await expect(drawer(page).getByRole('status')).toHaveText('You have 0 of 1 kudos left on this board.');
  await expect(kudosFor(page, 'Bob').getByRole('button', { name: 'Innovator' })).toBeDisabled();
});

test('a refused give rolls back and says why', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  fake.onCommand('retro.endorse.give', (ctx) => ctx.nack('endorse_limit'));
  await kudosButton(page).click();
  const target = kudosFor(page, 'Bob').getByRole('button', { name: 'Innovator' });

  await target.click();
  await expect(page.getByRole('alert').filter({ hasText: 'You have given all your kudos on this board.' })).toBeVisible();
  await expect(target).toHaveAttribute('aria-pressed', 'false');
});

test('kudos received by others show as counts in the drawer', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor({ endorsements: [endorsement(1, { from: CAROL.pid }), endorsement(2, { from: CAROL.pid, typeId: TYPE_IDEAS })] }) });
  await kudosButton(page).click();
  await expect(kudosFor(page, 'Bob').getByRole('button', { name: 'Team Player' })).toContainText('1');
  await expect(kudosFor(page, 'Bob').getByRole('button', { name: 'Team Player' })).toHaveAttribute('aria-pressed', 'false');
});

test('N opens and closes the drawer, and focus goes back to the tab', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await expect(kudosButton(page)).toHaveAttribute('aria-keyshortcuts', 'N');

  await page.keyboard.press('n');
  await expect(drawer(page)).toBeVisible();
  // N still closes it with the focus on one of the drawer's own buttons.
  await kudosFor(page, 'Bob').getByRole('button', { name: 'Innovator' }).focus();
  await page.keyboard.press('n');
  await expect(drawer(page)).toBeHidden();
  await expect(kudosButton(page)).toBeFocused();

  await page.keyboard.press('Shift+N');
  await page.keyboard.press('Control+n');
  await expect(drawer(page)).toBeHidden();
});

test('N is typed, not a shortcut, in the composer and while another dialog is open', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  const good = column(page, 'Good');
  await good.locator('.retro-add-card').click();
  const input = good.locator('.rs-editor-input');
  await expect(input).toBeFocused();
  await page.keyboard.type('nn');
  await expect(input).toHaveText('nn');
  await expect(drawer(page)).toBeHidden();

  await page.getByRole('button', { name: 'Add column' }).click();
  const other = page.getByRole('dialog', { name: 'Add column' });
  await other.getByRole('button', { name: 'Cancel' }).focus();
  await page.keyboard.press('n');
  await expect(drawer(page)).toBeHidden();
  await expect(other).toBeVisible();
});

test('N is not a shortcut while a menu is open', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await column(page, 'Good').locator('.retro-sort').click();
  const menu = page.getByRole('menu', { name: 'Sort cards' });
  await expect(menu).toBeVisible();
  await page.keyboard.press('n');
  await expect(drawer(page)).toBeHidden();
  await expect(menu).toBeVisible();

  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await page.keyboard.press('n');
  await expect(drawer(page)).toBeVisible();
});

test('the drawer closes when the tab hides: the viewer leaves the team', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await kudosButton(page).click();
  await expect(drawer(page)).toBeVisible();

  fake.tx(ROOM, [{ k: 'retro.members', d: { members: MEMBERS.slice(1) } }]);
  await expect(drawer(page)).toBeHidden();
  await expect(kudosButton(page)).toBeHidden();
  await expect(kudosButton(page)).not.toBeFocused();
});

test('the drawer closes when the tab hides: every kudos type is archived', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await kudosButton(page).click();
  await expect(drawer(page)).toBeVisible();

  // Types change only with a fresh snapshot: the server restarts and the client resubscribes.
  fake.room(ROOM, { snapshot: boardFor({ endorsementTypes: TYPES.map((t) => ({ ...t, archived: true })) }) });
  fake.bye({ newEpoch: true });
  await expect(kudosButton(page)).toBeHidden();
  await expect(drawer(page)).toBeHidden();
  await expect(kudosButton(page)).not.toBeFocused();
  await page.keyboard.press('n');
  await expect(drawer(page)).toBeHidden();
});

test('Escape and a click on the backdrop close the drawer', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await kudosButton(page).click();
  await expect(drawer(page)).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(drawer(page)).toBeHidden();
  await expect(kudosButton(page)).toBeFocused();

  await kudosButton(page).click();
  await expect(drawer(page)).toBeVisible();
  // The sheet hangs from the bottom edge, so the top left corner is backdrop.
  await page.mouse.click(5, 5);
  await expect(drawer(page)).toBeHidden();
  await expect(kudosButton(page)).toBeFocused();
});

test('the drawer is a bottom sheet with a card per teammate, or says there is nobody', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor() });
  await kudosButton(page).click();
  await expect(drawer(page)).toBeVisible();
  await expect(drawer(page).locator('.retro-kudos-person')).toHaveCount(2);
  const viewport = page.viewportSize();
  await expect.poll(async () => {
    const box = await drawer(page).boundingBox();
    return [Math.round(box.x), Math.round(box.width), Math.round(box.y + box.height)];
  }).toEqual([0, viewport.width, viewport.height]);
  await page.keyboard.press('Escape');

  fake.tx(ROOM, [{ k: 'retro.members', d: { members: [MEMBERS[0]] } }]);
  await kudosButton(page).click();
  await expect(drawer(page).locator('.retro-kudos-person')).toHaveCount(0);
  await expect(drawer(page)).toContainText('No other team members to give kudos to.');
});

test('the tab leaves room under the last card of a long column', async ({ page, fake }) => {
  serveTeam(fake);
  const cards = Array.from({ length: 14 }, (_, i) => card(i + 1, { body: `A longer card ${i + 1} so the column outgrows the screen` }));
  await openBoard(page, fake, { snap: boardFor({ cards }) });
  await expect(kudosButton(page)).toBeVisible();
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  const addCard = column(page, 'Good').locator('.retro-add-card');
  await expect.poll(async () => {
    const [tab, last] = [await kudosButton(page).boundingBox(), await addCard.boundingBox()];
    return last.y + last.height <= tab.y;
  }).toBe(true);
});

test('the recipient gets a celebration, once', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor(), me: BOB });
  const added = endorsement(5, { typeId: TYPE_IDEAS });

  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: added, celebrate: true } }]);
  const celebration = page.locator('.retro-celebration');
  await expect(celebration).toBeVisible();
  await expect(celebration).toContainText('You got kudos!');
  await expect(celebration).toContainText('Alice recognised you as Innovator.');
  await expect(celebration).toContainText('💡');
  await expect(page.locator('.retro-confetti')).toHaveCount(24);
  await expect(celebration).toHaveCount(0, { timeout: 6000 });
  await expect(page.locator('.retro-confetti')).toHaveCount(0);

  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: added, celebrate: true } }]);
  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: endorsement(6, { to: CAROL.pid }) } }]);
  await page.waitForTimeout(300);
  await expect(celebration).toHaveCount(0);
});

test('a celebration closes on click', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor(), me: BOB });
  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: endorsement(7), celebrate: true } }]);
  await page.locator('.retro-celebration').click();
  await expect(page.locator('.retro-celebration')).toHaveCount(0);
});

test('a celebration shows over the open drawer, closes on click and outlives the drawer', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor(), me: BOB });
  await kudosButton(page).click();
  await expect(drawer(page)).toBeVisible();

  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: endorsement(8), celebrate: true } }]);
  const celebration = page.locator('.retro-celebration');
  await expect(drawer(page).locator('.retro-celebration')).toBeVisible();
  // The click lands on the card, so the page under the modal sheet is not what holds it.
  await celebration.click();
  await expect(celebration).toHaveCount(0);
  await expect(page.locator('.retro-confetti')).toHaveCount(0);
  await expect(drawer(page)).toBeVisible();

  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: endorsement(9, { typeId: TYPE_IDEAS }), celebrate: true } }]);
  await expect(drawer(page).locator('.retro-celebration')).toContainText('Alice recognised you as Innovator.');
  await page.keyboard.press('Escape');
  await expect(drawer(page)).toBeHidden();
  await expect(celebration).toBeVisible();
  await celebration.click();
  await expect(celebration).toHaveCount(0);
});

test('a celebration that started under the board moves into the drawer when it opens', async ({ page, fake }) => {
  serveTeam(fake);
  await openBoard(page, fake, { snap: boardFor(), me: BOB });
  fake.tx(ROOM, [{ k: 'retro.endorsement.added', d: { endorsement: endorsement(10), celebrate: true } }]);
  await expect(page.locator('.retro-celebration')).toBeVisible();
  await page.keyboard.press('n');
  await expect(drawer(page)).toBeVisible();
  await expect(drawer(page).locator('.retro-celebration')).toBeVisible();
  await drawer(page).locator('.retro-celebration').click();
  await expect(page.locator('.retro-celebration')).toHaveCount(0);
});
