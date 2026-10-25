// Golden transcripts in the browser: every frame released one at a time, and the page must land on the
// round `expectedNav` names (R13), which ?round=N reflects.
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { expect, test } from '../_harness/fixtures.js';
import { memberMe } from '../_harness/me.js';
import { apply, fromSnapshot, roundById } from '../../../../web/src/poker/model.js';
import { CODE, R1, R2, ROOM, openTable, round, setupPoker, snapshot } from './_poker.js';

const DIR = fileURLToPath(new URL('../../../golden/transcripts/', import.meta.url));
const transcripts = readdirSync(DIR)
  .filter((name) => name.startsWith('poker-') && name.endsWith('.json'))
  .sort()
  .map((name) => JSON.parse(readFileSync(DIR + name, 'utf8')));

for (const transcript of transcripts) {
  test(`transcript ${transcript.name} navigates like the server says`, async ({ page, fake }) => {
    const frames = transcript.frames.filter((f) => f.t === 'snap' || f.t === 'tx');
    const [snap, ...txs] = frames;
    const viewer = { pid: transcript.viewer.pid, kind: 'user', name: 'Viewer' };
    fake.me = viewer;
    fake.boot = (path) => ({ build: 'fake', theme: 'light', me: memberMe(viewer, snap.d.teamId ? [{ id: snap.d.teamId, name: 'Platform' }] : []), path });
    fake.route('GET', '/api/poker/by-code/:code', { body: { id: snap.d.sessionId, access: 'granted' } });
    const player = fake.replayTranscript(transcript, { step: true });

    await page.goto(`/poker/${snap.d.code}`);
    await player.next(1);
    let state = fromSnapshot(snap.d, snap.o, viewer.pid);
    let viewed = transcript.expectedNav[0];
    const expectRound = async () => {
      await expect(page).toHaveURL(new RegExp(`[?&]round=${roundById(state, viewed).number}$`));
    };
    await expectRound();

    for (const [i, tx] of txs.entries()) {
      await player.next(1);
      state = tx.ev.reduce(apply, state);
      viewed = transcript.expectedNav[i + 1] ?? viewed;
      if (state.deleted) {
        await expect(page.getByRole('heading', { name: 'Session deleted' })).toBeVisible();
        break;
      }
      await expect(page.locator('.nt-round-name')).toContainText(`Round ${roundById(state, viewed).number}`);
      await expectRound();
    }
  });
}

test('a ?round=N deep link wins, and arrow keys step through the rounds', async ({ page, fake }) => {
  setupPoker(fake, { snap: snapshot({ rounds: [round(R1, 1), round(R2, 2, { ticketKey: 'ABC-2' })], currentRoundId: R2 }) });
  await openTable(page, `/poker/${CODE}?round=1`);
  await expect(page.locator('.nt-round-name')).toHaveText('Round 1');
  await page.keyboard.press('ArrowRight');
  await expect(page).toHaveURL(/[?&]round=2$/);
  await expect(page.locator('.nt-round-name')).toHaveText('Round 2 · ABC-2');
  await page.keyboard.press('ArrowRight');
  await expect(page).toHaveURL(/[?&]round=2$/);
  await page.getByRole('button', { name: 'Previous round' }).click();
  await expect(page).toHaveURL(/[?&]round=1$/);
});

test('a ticket edit is debounced, optimistic and flushed on blur', async ({ page, fake }) => {
  setupPoker(fake);
  fake.onCommand('poker.round.setTicket', (ctx) => ctx.commit([{
    k: 'poker.round.updated',
    d: { round: round(R1, 1, { version: ctx.frame.d.baseVersion + 1, ticketKey: ctx.frame.d.ticketKey?.toUpperCase() }) },
  }]));
  await openTable(page);
  const field = page.getByRole('textbox', { name: 'Ticket key' });
  await field.fill('abc-77');
  await expect.poll(() => fake.received.filter(({ frame }) => frame.k === 'poker.round.setTicket').length, { timeout: 3000 }).toBe(1);
  const sent = fake.received.find(({ frame }) => frame.k === 'poker.round.setTicket').frame.d;
  expect(sent).toEqual({ roundId: R1, ticketKey: 'abc-77', baseVersion: 1 });

  await field.fill('');
  await field.press('Tab');
  const cleared = await fake.waitForFrame((f) => f.k === 'poker.round.setTicket' && f.d.ticketKey === null);
  expect(cleared.frame.d).toEqual({ roundId: R1, ticketKey: null, baseVersion: 2 });
  await expect(field).toHaveValue('');
  await expect(field).toHaveAttribute('placeholder', 'Round 1');
  fake.tx(ROOM, [{ k: 'poker.round.updated', d: { round: round(R1, 1, { version: 4, ticketKey: 'XYZ-9' }) } }]);
  await expect(field).toHaveValue('XYZ-9');
});
