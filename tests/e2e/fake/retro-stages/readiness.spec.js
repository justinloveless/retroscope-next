import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, CAROL, ROOM, STAGED, openBoard, sent, serveStages, snapshot } from './_stages.js';

const count = (page) => page.locator('.retro-ready-count');

test('readiness counts only the people on the board', async ({ page, fake }) => {
  const snap = snapshot({ config: STAGED, presence: [ALICE, BOB], readiness: { stage: 'thinking', ready: [BOB.pid, CAROL.pid] } });
  await openBoard(page, fake, { snap });
  await expect(count(page)).toHaveText('1/2 ready');
  await expect(count(page)).toHaveAttribute('title', 'Ready: Bob');

  fake.tx(ROOM, [{ k: 'presence.join', d: { ...CAROL } }]);
  await expect(count(page)).toHaveText('2/3 ready');
  fake.tx(ROOM, [{ k: 'presence.leave', d: { pid: BOB.pid } }]);
  await expect(count(page)).toHaveText('1/2 ready');
  fake.tx(ROOM, [{ k: 'presence.leave', d: { pid: CAROL.pid } }]);
  await expect(count(page)).toHaveText('0/1 ready');
  await expect(count(page)).toHaveAttribute('title', 'Nobody is ready yet');
});

test('marking ready shows at once and sends the current stage', async ({ page, fake }) => {
  const snap = snapshot({ config: STAGED, stage: 'voting', readiness: { stage: 'voting', ready: [] }, presence: [ALICE, BOB] });
  serveStages(fake, snap);
  await openBoard(page, fake, { snap });
  fake.onCommand('retro.ready.set', () => {});

  const toggle = page.getByRole('button', { name: 'Mark ready' });
  await toggle.click();
  await expect(count(page)).toHaveText('1/2 ready');
  await expect(page.getByRole('button', { name: 'Ready' })).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => sent(fake, 'retro.ready.set')).toEqual([{ stage: 'voting', ready: true }]);
});

test('ready marks of an earlier stage do not count, and a stage change clears them', async ({ page, fake }) => {
  const snap = snapshot({ config: STAGED, stage: 'voting', readiness: { stage: 'thinking', ready: [ALICE.pid, BOB.pid] }, presence: [ALICE, BOB] });
  await openBoard(page, fake, { snap });
  await expect(count(page)).toHaveText('0/2 ready');

  fake.tx(ROOM, [{ k: 'retro.readiness', d: { stage: 'voting', ready: [ALICE.pid, BOB.pid] } }]);
  await expect(count(page)).toHaveText('2/2 ready');
  await expect(page.locator('.retro-ready')).toHaveAttribute('data-all', 'true');
  fake.tx(ROOM, [{ k: 'retro.stage', d: { stage: 'discussing' } }, { k: 'retro.readiness', d: { stage: 'discussing', ready: [] } }]);
  await expect(count(page)).toHaveText('0/2 ready');
});
