import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, CAROL, ROOM, TYPE_HELPER, TYPE_IDEAS, boardFor, openBoard, sent, serveTeam } from './_actions.js';

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
