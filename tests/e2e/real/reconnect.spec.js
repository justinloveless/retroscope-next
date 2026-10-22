// Reconnects over the real server (§j): a context that goes offline mid-vote flushes its outbox when it comes back
// and everyone converges; a server restart mid-session loses no acknowledged vote and every page carries on.
import { compose, expect, networkSwitch, sql, test } from './_real.js';
import { handCard, seat, table } from './_poker.js';

test('a vote cast offline is sent when the context comes back, and everyone converges', async ({ open }) => {
  const { players, session } = await table(open, ['Ann Offline', 'Bob Offline']);
  const [ann, bob] = players;
  const network = await networkSwitch(bob.context);
  await bob.page.reload();
  await expect(bob.page.locator('.nt-seat')).toHaveCount(1);

  await network.offline();
  await handCard(bob.page, 5).click();
  await bob.page.getByRole('button', { name: 'Lock In' }).click();
  await expect(handCard(bob.page, 5).locator('.nt-hand-locked')).toBeVisible();
  await ann.page.waitForTimeout(1000);
  await expect(seat(ann.page, bob.user)).toHaveAttribute('data-status', 'empty');

  await network.online();
  await expect(seat(ann.page, bob.user)).toHaveAttribute('data-status', 'locked', { timeout: 20_000 });
  await handCard(ann.page, 8).click();
  await ann.page.getByRole('button', { name: 'Lock In' }).click();
  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  for (const { page } of players) {
    await expect(page.locator('.nt-stack-pill')).toHaveText(['1 x 5 pts', '1 x 8 pts']);
  }
  expect(await sql(`select v.points, v.locked from poker_votes v join poker_rounds r on r.id = v.round_id
    where r.session_id = '${session.id}' and v.principal_id = '${bob.user.id}'`)).toEqual([['5', 't']]);
});

test('a server restart mid-session keeps every acknowledged vote and the pages carry on', async ({ open, stackLock }) => {
  test.setTimeout(480_000);
  const { players } = await table(open, ['Ann Restart', 'Bob Restart']);
  const [ann, bob] = players;
  await handCard(ann.page, 3).click();
  await ann.page.getByRole('button', { name: 'Lock In' }).click();
  await expect(seat(bob.page, ann.user)).toHaveAttribute('data-status', 'locked');

  // Nobody else may see the outage: wait until every other test is done and hold the stack.
  await stackLock.exclusive();
  const started = Date.now();
  await compose('restart', 'app');
  for (const { page } of players) {
    await expect(page.locator('.nt-seat')).toHaveCount(1, { timeout: 60_000 });
  }

  await handCard(bob.page, 13).click();
  await bob.page.getByRole('button', { name: 'Lock In' }).click();
  await expect(seat(ann.page, bob.user)).toHaveAttribute('data-status', 'locked', { timeout: 30_000 });
  await expect(seat(bob.page, ann.user)).toHaveAttribute('data-status', 'locked');
  expect(Date.now() - started).toBeLessThan(120_000);

  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  for (const { page } of players) {
    await expect(page.locator('.nt-stack-pill')).toHaveText(['1 x 3 pts', '1 x 13 pts']);
  }
});
