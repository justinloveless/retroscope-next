import { expect, test } from '../_harness/fixtures.js';
import {
  ALICE, BOARD_ID, BOB, CAROL, OTHER_BOARD, TEAM_ID, actionCard, boardFor, cardId, cardsIn, item, openBoard, serveTeam, snapshot, teamRoom, teamSubs,
} from './_actions.js';

const DAVE = { pid: 'd4000000-0000-4000-8000-000000000005', kind: 'user', name: 'Dave Visitor' };
const GUEST = { pid: '90000000-0000-4000-8000-000000000004', kind: 'guest', name: 'Sneaky Penguin' };

// What a non-member gets on a `link` team board: no members, endorsements or limit (S12).
const visitorSnapshot = () => snapshot({ teamId: TEAM_ID, cards: [actionCard(1)], presence: [ALICE, DAVE, GUEST] });

function serveFullTeam(fake) {
  serveTeam(fake, teamRoom({
    actionItems: [
      item(1, { sourceBoardId: BOARD_ID, sourceCardId: actionCard(1).id, assignedTo: CAROL.pid }),
      item(2, { text: 'Carol owns the deploy', sourceBoardId: OTHER_BOARD, sourceCardId: cardId(92), assignedTo: CAROL.pid }),
    ],
  }));
}

for (const viewer of [DAVE, GUEST]) {
  test(`a ${viewer.kind} who is not a member sees no roster, kudos or team items`, async ({ page, fake }) => {
    serveFullTeam(fake);
    await openBoard(page, fake, { snap: visitorSnapshot(), me: viewer });
    await expect(cardsIn(page, 'Action Items')).toHaveCount(1);
    // Give a wrongly subscribing page time to paint what it would have received.
    await page.waitForTimeout(500);

    await expect(page.locator('.retro-kudos-button')).toBeHidden();
    await expect(page.locator('.retro-prev-actions')).toHaveCount(0);
    await expect(cardsIn(page, 'Action Items').locator('.retro-action-item')).toBeHidden();
    await expect(page.locator('body')).not.toContainText(CAROL.name);
    expect(teamSubs(fake)).toEqual([]);
    expect(fake.requests.filter((r) => r.path.startsWith('/api/teams/'))).toEqual([]);
  });
}

test('a member sees the same board with the team parts', async ({ page, fake }) => {
  serveFullTeam(fake);
  await openBoard(page, fake, { snap: boardFor({ cards: [actionCard(1)] }), me: BOB });
  await expect(page.locator('.retro-kudos-button')).toBeVisible();
  await expect(page.locator('.retro-prev-actions')).toContainText('Carol owns the deploy');
  await expect(cardsIn(page, 'Action Items').locator('.retro-assignee')).toHaveText('Carol Danvers');
  await expect.poll(() => teamSubs(fake).length).toBe(1);
});

test('losing the roster drops the team subscription and its parts', async ({ page, fake }) => {
  serveFullTeam(fake);
  await openBoard(page, fake, { snap: boardFor({ cards: [actionCard(1)] }), me: BOB });
  await expect(page.locator('.retro-prev-actions')).toBeVisible();

  fake.tx(`board:${BOARD_ID}`, [{ k: 'retro.members', d: { members: [{ pid: ALICE.pid, name: ALICE.name, role: 'owner' }] } }]);
  await expect(page.locator('.retro-prev-actions')).toHaveCount(0);
  await expect(page.locator('.retro-kudos-button')).toBeHidden();
  await expect(cardsIn(page, 'Action Items').locator('.retro-action-item')).toBeHidden();
  await expect.poll(() => fake.received.some(({ frame }) => frame.t === 'unsub' && frame.r === `team:${TEAM_ID}`)).toBe(true);
});
