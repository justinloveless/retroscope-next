// Stages, readiness, the shared timer and focus over the real server (§j): two facilitators racing for the stage,
// a timer two contexts agree on with their clocks a minute apart, and a late joiner who lands on the focused card.
import { expect, networkSwitch, test } from './_real.js';
import { addCard, cardsIn, configure, openBoard, retroBoard } from './_retro.js';

const SKEW_MS = 30_000;
const pill = (page, name) => page.locator('.retro-stage-pill', { hasText: name });
const advance = (page) => page.locator('.retro-stage-advance');
const readyCount = (page) => page.locator('.retro-ready-count');

test('two facilitators: readiness gates the stage, and the slower of two moves is told', async ({ open }) => {
  const { players } = await retroBoard(open, ['Ann Stage', 'Bob Stage']);
  const [ann, bob] = players;
  await configure(ann.page, { switches: ['Structured stages (thinking, voting, discussing)', 'Advance only when everyone present is ready'] });
  for (const { page } of players) {
    await expect(pill(page, 'Thinking')).toHaveAttribute('aria-current', 'step');
    await expect(readyCount(page)).toHaveText('0/2 ready');
  }

  await ann.page.getByRole('button', { name: 'Mark ready' }).click();
  await expect(readyCount(bob.page)).toHaveText('1/2 ready');
  await bob.page.getByRole('button', { name: 'Mark ready' }).click();
  await expect(readyCount(ann.page)).toHaveText('2/2 ready');
  await expect(readyCount(bob.page)).toHaveText('2/2 ready');

  // Bob's move waits in his outbox while Ann's lands, so it reaches the server with a stale stage.
  const network = await networkSwitch(bob.context);
  await bob.page.reload();
  await expect(readyCount(bob.page)).toHaveText('2/2 ready');
  await network.offline();
  await advance(bob.page).click();
  await advance(ann.page).click();
  await expect(pill(ann.page, 'Voting')).toHaveAttribute('aria-current', 'step');
  await network.online();

  // Bob's socket comes back on the client's reconnect backoff, which grows while offline.
  for (const { page } of players) {
    await expect(pill(page, 'Voting')).toHaveAttribute('aria-current', 'step', { timeout: 20_000 });
    await expect(readyCount(page)).toHaveText('0/2 ready');
  }
  await expect(bob.page.getByRole('status').filter({ hasText: 'Someone else changed the stage first' })).toBeVisible();
  await expect(ann.page.getByRole('status').filter({ hasText: 'Someone else changed the stage first' })).toHaveCount(0);
});

test('the timer agrees across two contexts whose clocks are a minute apart', async ({ open }) => {
  const { players, board } = await retroBoard(open, ['Ann Timer', 'Bob Timer']);
  const [ann, bob] = players;
  for (const [player, skew] of [[ann, SKEW_MS], [bob, -SKEW_MS]]) {
    await player.page.clock.install({ time: Date.now() + skew });
    await openBoard(player.page, board.code);
  }

  await ann.page.getByRole('button', { name: 'Timer', exact: true }).click();
  const dialog = ann.page.getByRole('dialog', { name: 'Set the timer' });
  await dialog.getByLabel('Minutes').fill('5');
  await dialog.getByLabel('Seconds').fill('0');
  await dialog.getByRole('button', { name: 'Start timer' }).click();

  const display = (page) => page.getByRole('timer');
  const realDeadline = async (page, skew) => Number(await display(page).getAttribute('data-deadline')) - skew;
  for (const { page } of players) {
    await expect(display(page)).toHaveText(/^[45]:[0-9]{2}$/);
  }
  await expect.poll(async () => Math.abs((await realDeadline(ann.page, SKEW_MS)) - (await realDeadline(bob.page, -SKEW_MS))))
    .toBeLessThan(1000);
  const deadline = await realDeadline(ann.page, SKEW_MS);
  expect(Math.abs(deadline - (Date.now() + 300_000))).toBeLessThan(5000);
  await expect.poll(async () => (await display(bob.page).textContent()) === (await display(ann.page).textContent())).toBe(true);

  await bob.page.getByRole('button', { name: 'Pause timer' }).click();
  for (const { page } of players) {
    await expect(page.locator('.retro-timer')).toHaveAttribute('data-state', 'paused');
  }
  await expect.poll(async () => (await display(bob.page).textContent()) === (await display(ann.page).textContent())).toBe(true);
});

test('a late joiner lands on the focused card', async ({ open }) => {
  const { players, board } = await retroBoard(open, ['Ann Focus', 'Bob Focus', 'Cy Focus']);
  const [ann, bob, cy] = players;
  await cy.page.goto('about:blank');
  for (const text of ['First point', 'Second point']) {
    await addCard(bob.page, 'Good', text);
  }
  await cardsIn(ann.page, 'Good').filter({ hasText: 'Second point' }).locator('.retro-focus-toggle').click();
  const banner = (page) => page.getByRole('region', { name: 'Focused card' });
  await expect(banner(bob.page).locator('.retro-focus-body')).toHaveText('Second point');

  await openBoard(cy.page, board.code);
  await expect(banner(cy.page).locator('.retro-focus-body')).toHaveText('Second point');
  await expect(banner(cy.page).locator('.retro-focus-column')).toHaveText('Good');
  await expect(banner(cy.page).locator('.retro-focus-author .retro-author-name')).toHaveText('Bob Focus');

  await banner(ann.page).getByRole('button', { name: 'Stop focusing' }).click();
  await expect(banner(cy.page)).toHaveCount(0);
});
