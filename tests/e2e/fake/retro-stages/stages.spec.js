import { expect, test } from '../_harness/fixtures.js';
import {
  ALICE, BOB, ROOM, STAGED, asPerson, openAs, openBoard, personPage, sent, servePeople, serveStages, snapshot,
} from './_stages.js';

const pill = (page, name) => page.locator('.retro-stage-pill', { hasText: name });
const advance = (page) => page.locator('.retro-stage-advance');

test('the stage bar shows only with stages on and follows the stage', async ({ page, fake }) => {
  const snap = snapshot();
  serveStages(fake, snap);
  await openBoard(page, fake, { snap });
  await expect(page.locator('.retro-stages')).toBeHidden();

  fake.tx(ROOM, [{ k: 'retro.config', d: { config: STAGED } }]);
  await expect(page.locator('.retro-stages')).toBeVisible();
  await expect(pill(page, 'Thinking')).toHaveAttribute('aria-current', 'step');
  await expect(pill(page, 'Voting')).toHaveAttribute('data-state', 'upcoming');

  fake.tx(ROOM, [{ k: 'retro.stage', d: { stage: 'discussing' } }, { k: 'retro.readiness', d: { stage: 'discussing', ready: [] } }]);
  await expect(pill(page, 'Discussing')).toHaveAttribute('aria-current', 'step');
  await expect(pill(page, 'Thinking')).toHaveAttribute('data-state', 'done');
  await expect(pill(page, 'Voting')).toHaveAttribute('data-state', 'done');
  await expect(advance(page)).toHaveText('Close retro ›');
});

test('a facilitator advances and jumps; the stage waits for the server', async ({ page, fake }) => {
  const snap = snapshot({ config: STAGED });
  const hold = [];
  await openBoard(page, fake, { snap });
  const stages = serveStages(fake, snap);
  fake.onCommand('retro.stage.set', (ctx) => hold.push(ctx));

  await advance(page).click();
  await expect.poll(() => sent(fake, 'retro.stage.set')).toEqual([{ from: 'thinking', to: 'voting' }]);
  await expect(pill(page, 'Thinking')).toHaveAttribute('aria-current', 'step');
  stages.decide(hold.shift());
  await expect(pill(page, 'Voting')).toHaveAttribute('aria-current', 'step');
  await expect(advance(page)).toHaveText('Start discussion ›');

  await pill(page, 'Closed').click();
  await expect.poll(() => sent(fake, 'retro.stage.set').at(-1)).toEqual({ from: 'voting', to: 'closed' });
  stages.decide(hold.shift());
  await expect(pill(page, 'Closed')).toHaveAttribute('aria-current', 'step');
  await expect(advance(page)).toBeHidden();
  await expect(page.locator('.retro-ready')).toBeHidden();

  await page.getByRole('button', { name: 'Reopen' }).click();
  stages.decide(hold.shift());
  await expect(pill(page, 'Discussing')).toHaveAttribute('aria-current', 'step');
  await page.getByRole('button', { name: 'Reset' }).click();
  await expect.poll(() => sent(fake, 'retro.stage.set').at(-1)).toEqual({ from: 'discussing', to: 'thinking' });
});

test('two facilitators move the stage at once: one wins and the other is told', async ({ page, fake, browser }) => {
  const snap = snapshot({ config: STAGED, presence: [ALICE, BOB] });
  servePeople(fake, { alice: ALICE, bob: BOB }, snap);
  const stages = serveStages(fake, snap);
  const held = [];
  fake.onCommand('retro.stage.set', (ctx) => {
    held.push(ctx);
    if (held.length === 2) {
      for (const command of held) {
        stages.decide(command);
      }
    }
  });
  await asPerson(page, fake, 'alice');
  const bob = await personPage(browser, fake, 'bob');
  await openAs(page);
  await openAs(bob);

  await advance(page).click();
  await advance(bob).click();
  await expect.poll(() => fake.received.filter(({ frame }) => frame.k === 'retro.stage.set').length).toBe(2);

  for (const viewer of [page, bob]) {
    await expect(pill(viewer, 'Voting')).toHaveAttribute('aria-current', 'step');
  }
  const told = [page, bob].map((viewer) => viewer.getByRole('status').filter({ hasText: 'Someone else changed the stage first' }));
  await expect.poll(async () => (await told[0].count()) + (await told[1].count())).toBe(1);
  expect(stages.stage).toBe('voting');
  expect(bob.cspViolations).toEqual([]);
  await bob.context().close();
});

test('with readiness enforced the facilitator advances only when everyone present is ready', async ({ page, fake }) => {
  const config = { ...STAGED, enforceReadiness: true };
  const snap = snapshot({ config, presence: [ALICE, BOB] });
  const stages = serveStages(fake, snap);
  await openBoard(page, fake, { snap });

  await expect(advance(page)).toBeDisabled();
  for (const name of ['Voting', 'Discussing', 'Closed']) {
    await expect(pill(page, name)).toBeDisabled();
  }
  await page.getByRole('button', { name: 'Mark ready' }).click();
  await expect(page.locator('.retro-ready-count')).toHaveText('1/2 ready');
  await expect(advance(page)).toBeDisabled();

  stages.ready.add(BOB.pid);
  fake.tx(ROOM, [{ k: 'retro.readiness', d: { stage: 'thinking', ready: [ALICE.pid, BOB.pid].sort() } }]);
  await expect(page.locator('.retro-ready-count')).toHaveText('2/2 ready');
  await expect(advance(page)).toBeEnabled();
  await advance(page).click();
  await expect(pill(page, 'Voting')).toHaveAttribute('aria-current', 'step');
  await expect(page.locator('.retro-ready-count')).toHaveText('0/2 ready');
  await expect(page.getByRole('button', { name: 'Reset' })).toBeEnabled();
});
