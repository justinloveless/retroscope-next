import { expect, test } from '../_harness/fixtures.js';
import {
  ALICE, BOB, GUEST, ROOM, alarms, asPerson, fakeAudio, openAs, openBoard, personPage, sent, servePeople, skewedClock, snapshot,
} from './_stages.js';

const SKEW_MS = 30_000;
const display = (page) => page.getByRole('timer');

function running(fake, ms, fields = {}) {
  return { state: 'running', durationMs: 300_000, alarm: true, music: false, endsAt: fake.now() + ms, ...fields };
}

/** Where the page puts the end of the timer on the real clock, from its own skewed one. */
async function realDeadline(page, skew) {
  return Number(await display(page).getAttribute('data-deadline')) - skew;
}

async function settled(fake, page) {
  const after = fake.received.length;
  await fake.waitForFrame((f) => f.t === 'ping', { after: 0 });
  await page.waitForTimeout(300);
  return after;
}

async function unlockAudio(page) {
  await page.keyboard.press('Shift');
  await expect.poll(() => page.evaluate(() => window.__tones !== undefined)).toBe(true);
}

test('a facilitator starts the timer from the dialog; a guest is told to sign in', async ({ page, fake }) => {
  await openBoard(page, fake, { snap: snapshot() });
  await page.getByRole('button', { name: 'Timer' }).click();
  const dialog = page.getByRole('dialog', { name: 'Set the timer' });
  await expect(dialog.getByLabel('Minutes')).toHaveValue('15');
  await dialog.getByLabel('Minutes').fill('5');
  await dialog.getByLabel('Seconds').fill('30');
  await dialog.getByRole('switch', { name: 'Play background music' }).click();
  await dialog.getByRole('button', { name: 'Start timer' }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(() => sent(fake, 'retro.timer.start')).toEqual([{ durationMs: 330_000, alarm: true, music: true, from: 'idle' }]);

  await page.getByRole('button', { name: 'Timer' }).click();
  await dialog.getByLabel('Minutes').fill('121');
  await dialog.getByRole('button', { name: 'Start timer' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Choose between 1 second and 120 minutes.');
  await dialog.getByRole('button', { name: 'Cancel' }).click();

  await openBoard(page, fake, { snap: snapshot({ presence: [ALICE, GUEST] }), me: GUEST });
  await expect(page.locator('.retro-timer-hint')).toHaveText('Sign in to use the timer');
  await expect(page.getByRole('button', { name: 'Timer' })).toBeHidden();
});

test('the countdown follows the server and pause, resume and reset go through it', async ({ page, fake }) => {
  const snap = snapshot({ timer: running(fake, 125_000) });
  await openBoard(page, fake, { snap });
  await expect(display(page)).toHaveText(/^2:0[45]$/);

  fake.onCommand('retro.timer.pause', (ctx) => ctx.commit([{ k: 'retro.timer', d: { state: 'paused', durationMs: 300_000, alarm: true, music: false, remainingMs: 61_000 } }]));
  await page.getByRole('button', { name: 'Pause timer' }).click();
  await expect(display(page)).toHaveText('1:01');
  await expect(page.locator('.retro-timer')).toHaveAttribute('data-state', 'paused');
  await expect(display(page)).not.toHaveAttribute('data-deadline');

  fake.onCommand('retro.timer.resume', (ctx) => ctx.commit([{ k: 'retro.timer', d: running(fake, 61_000, { alarm: true }) }]));
  await page.getByRole('button', { name: 'Resume timer' }).click();
  await expect(page.locator('.retro-timer')).toHaveAttribute('data-state', 'running');
  await page.getByRole('button', { name: 'Reset timer' }).click();
  await expect.poll(() => [sent(fake, 'retro.timer.pause'), sent(fake, 'retro.timer.resume'), sent(fake, 'retro.timer.reset')])
    .toEqual([[{ from: 'running' }], [{ from: 'paused' }], [{ from: 'running' }]]);
});

test('two tabs whose clocks are 60 s apart agree on the countdown within 100 ms', async ({ page, fake, browser }) => {
  fake.clockOffsetMs = 12_345;
  const timer = running(fake, 300_000);
  servePeople(fake, { alice: ALICE, bob: BOB }, snapshot({ timer }));
  await asPerson(page, fake, 'alice', { init: [skewedClock(SKEW_MS)] });
  const bob = await personPage(browser, fake, 'bob', { init: [skewedClock(-SKEW_MS)] });
  await openAs(page);
  await openAs(bob);
  await settled(fake, page);

  const truth = timer.endsAt - fake.clockOffsetMs;
  await expect.poll(async () => Math.abs((await realDeadline(page, SKEW_MS)) - truth)).toBeLessThan(100);
  await expect.poll(async () => Math.abs((await realDeadline(bob, -SKEW_MS)) - truth)).toBeLessThan(100);
  expect(Math.abs((await realDeadline(page, SKEW_MS)) - (await realDeadline(bob, -SKEW_MS)))).toBeLessThan(100);
  await expect(display(page)).toHaveText(/^[45]:[0-9]{2}$/);
  // The two displays are read one after the other, so a read can straddle a second boundary; they must
  // show the same text whenever both are read within the same second.
  await expect.poll(async () => (await display(bob).textContent()) === (await display(page).textContent())).toBe(true);
  expect(bob.cspViolations).toEqual([]);
  await bob.context().close();
});

test('a wall-clock jump while running resamples the clock through the socket', async ({ page, fake }) => {
  const timer = running(fake, 300_000);
  servePeople(fake, { alice: ALICE }, snapshot({ timer }));
  await page.addInitScript(() => {
    window.__onlineEvents = 0;
    window.addEventListener('online', () => {
      window.__onlineEvents++;
    });
  });
  await asPerson(page, fake, 'alice', { init: [skewedClock(0)] });
  await openAs(page);
  await settled(fake, page);
  await expect.poll(async () => Math.abs((await realDeadline(page, 0)) - timer.endsAt)).toBeLessThan(100);

  const after = fake.received.length;
  await page.evaluate(() => {
    window.__skew += 60_000;
  });
  await fake.waitForFrame((f) => f.t === 'ping', { after, timeout: 3000 });
  await expect.poll(async () => Math.abs((await realDeadline(page, 60_000)) - timer.endsAt)).toBeLessThan(100);
  await expect(display(page)).toHaveText(/^4:[0-9]{2}$/);
  expect(await page.evaluate(() => window.__onlineEvents)).toBe(0);
});

test('the alarm plays once per end time, whether the tab or the server sees it first', async ({ page, fake }) => {
  await page.addInitScript(fakeAudio.fn);
  const first = running(fake, 1500);
  await openBoard(page, fake, { snap: snapshot({ timer: first }) });
  await unlockAudio(page);

  await expect(display(page)).toHaveText('0:00', { timeout: 4000 });
  await expect.poll(() => alarms(page)).toBe(1);
  fake.tx(ROOM, [{ k: 'retro.timer', d: { ...first, state: 'idle', endsAt: first.endsAt, expired: true } }]);
  await page.waitForTimeout(500);
  expect(await alarms(page)).toBe(1);

  const second = running(fake, 3000);
  fake.tx(ROOM, [{ k: 'retro.timer', d: second }]);
  await expect(display(page)).toHaveText(/^0:0[23]$/);
  fake.tx(ROOM, [{ k: 'retro.timer', d: { ...second, state: 'idle', expired: true } }]);
  await expect.poll(() => alarms(page)).toBe(2);
  await page.waitForTimeout(3500);
  expect(await alarms(page)).toBe(2);

  fake.tx(ROOM, [{ k: 'retro.timer', d: { ...second, state: 'idle', endsAt: fake.now() - 60_000, expired: true } }]);
  const silent = running(fake, 800, { alarm: false });
  fake.tx(ROOM, [{ k: 'retro.timer', d: silent }]);
  await page.waitForTimeout(1500);
  expect(await alarms(page)).toBe(2);
});

test('the music starts where the shared timer is, and stops when it pauses', async ({ page, fake }) => {
  await page.addInitScript(fakeAudio.fn);
  const paused = { state: 'paused', durationMs: 300_000, alarm: true, music: true, remainingMs: 239_000 };
  await openBoard(page, fake, { snap: snapshot({ timer: paused }) });
  const timer = page.locator('.retro-timer');
  fake.onCommand('retro.timer.resume', (ctx) => ctx.commit([{ k: 'retro.timer', d: running(fake, 239_000, { music: true }) }]));
  fake.onCommand('retro.timer.pause', (ctx) => ctx.commit([{ k: 'retro.timer', d: paused }]));

  await expect(timer).not.toHaveAttribute('data-music-position');
  await page.getByRole('button', { name: 'Resume timer' }).click();
  await expect.poll(async () => Number(await timer.getAttribute('data-music-position'))).toBeGreaterThanOrEqual(61_000);
  expect(Number(await timer.getAttribute('data-music-position'))).toBeLessThan(62_000);
  await expect.poll(() => page.evaluate(() => window.__tones.filter((t) => t.type === 'triangle').length)).toBeGreaterThan(0);

  await page.getByRole('button', { name: 'Pause timer' }).click();
  await expect(timer).not.toHaveAttribute('data-music-position');

  await page.getByRole('button', { name: 'Timer sound' }).click();
  await page.getByRole('dialog', { name: 'Timer sound' }).getByRole('switch', { name: 'Mute' }).click();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Resume timer' }).click();
  await expect(timer).toHaveAttribute('data-state', 'running');
  await page.waitForTimeout(500);
  await expect(timer).not.toHaveAttribute('data-music-position');
  expect(await page.evaluate(() => localStorage.getItem('rs.timer.muted'))).toBe('1');
});
