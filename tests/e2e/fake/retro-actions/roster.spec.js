import { expect, test } from '../_harness/fixtures.js';
import {
  ALICE, BOARD_ID, BOB, CAROL, OTHER_BOARD, TEAM_ID, actionCard, boardFor, cardId, cardsIn, item, openBoard, serveTeam, teamRoom, teamSubs,
} from './_actions.js';

function serveFullTeam(fake) {
  serveTeam(fake, teamRoom({
    actionItems: [
      item(1, { sourceBoardId: BOARD_ID, sourceCardId: actionCard(1).id, assignedTo: CAROL.pid }),
      item(2, { text: 'Carol owns the deploy', sourceBoardId: OTHER_BOARD, sourceCardId: cardId(92), assignedTo: CAROL.pid }),
    ],
  }));
}

test('a member sees the same board with the team parts', async ({ page, fake }) => {
  serveFullTeam(fake);
  await openBoard(page, fake, { snap: boardFor({ cards: [actionCard(1)] }), me: BOB });
  await expect(page.locator('.retro-kudos-tab')).toBeVisible();
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
  await expect(page.locator('.retro-kudos-tab')).toBeHidden();
  await expect(cardsIn(page, 'Action Items').locator('.retro-action-item')).toBeHidden();
  await expect.poll(() => fake.received.some(({ frame }) => frame.t === 'unsub' && frame.r === `team:${TEAM_ID}`)).toBe(true);
});
